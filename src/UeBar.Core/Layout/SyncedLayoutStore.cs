// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UeBar.Core.Abstractions;
using UeBar.Core.Json;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.Core.Layout;

/// <summary>Result of a reconcile (for log, diagnostics and tests).</summary>
public enum LayoutSyncOutcome
{
    /// <summary>No remote store – local only.</summary>
    LocalOnly,

    /// <summary>Remote store currently unreachable; local changes stay pending.</summary>
    Unavailable,

    /// <summary>Both equal.</summary>
    InSync,

    /// <summary>Local state uploaded (pending change or first upload).</summary>
    Uploaded,

    /// <summary>Remote state taken over (local copy replaced, backed up first).</summary>
    Downloaded,

    /// <summary>Local file broken or similar – nothing touched.</summary>
    Failed,
}

/// <summary>Short state for tray/diagnostics.</summary>
public sealed record LayoutSyncStatus(
    string StoreName,
    bool Pending,
    DateTimeOffset? LastSyncedAt,
    LayoutSyncOutcome? LastOutcome,
    string? LastProblem);

/// <summary>
/// Base layout with a local copy and an optional remote store (SPEC 9, M5b):
/// <list type="bullet">
/// <item>Always reads the local copy (<c>layout.json</c>) – fast start, also offline.
/// If it is missing, the remote store is asked.</item>
/// <item>Saves locally first (with backup) and marks it "pending", then uploads.
/// Without a connection it stays pending and goes up on the next reconcile.</item>
/// <item>Reconciles on every notification of the remote store (reconnect, live change):
/// equal → nothing; only remote changed → take over; both changed → newer <c>updatedAt</c> wins,
/// the other version is backed up first (no silent overwrite).</item>
/// </list>
/// The state (pending, last common state) lives in <c>layout.sync.json</c>.
/// </summary>
public sealed class SyncedLayoutStore : ILayoutStore, IDisposable
{
    private readonly LocalFileLayoutStore _cache;
    private readonly IRemoteLayoutSource _remote;
    private readonly string _stateFile;
    private readonly string? _backupDirectory;
    private readonly IUeLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private SyncState _state;
    private LayoutSyncOutcome? _lastOutcome;
    private string? _lastProblem;
    private Task _lastSync = Task.CompletedTask;
    private bool _disposed;

    public SyncedLayoutStore(LocalFileLayoutStore cache, IRemoteLayoutSource remote, string stateFile, string? backupDirectory, IUeLogger? log = null)
    {
        _cache = cache;
        _remote = remote;
        _stateFile = stateFile;
        _backupDirectory = backupDirectory;
        _log = log ?? NullLogger.Instance;
        _state = LoadState();
        _cache.Changed += OnCacheChanged;
        _remote.Changed += OnRemoteChanged;
    }

    public string StoreId => _remote.Current?.StoreId ?? _cache.StoreId;

    public string StoreDisplayName => _remote.Current?.StoreDisplayName ?? _cache.StoreDisplayName;

    /// <summary>Local changes that are not in the remote store yet.</summary>
    public bool IsPending
    {
        get
        {
            lock (_lock)
            {
                return _state.Pending;
            }
        }
    }

    /// <summary>The most recently started background reconcile (for tests).</summary>
    public Task LastSync
    {
        get
        {
            lock (_lock)
            {
                return _lastSync;
            }
        }
    }

    public LayoutSyncStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new LayoutSyncStatus(StoreDisplayName, _state.Pending, _state.LastSyncedAt, _lastOutcome, _lastProblem);
            }
        }
    }

    /// <summary>The local copy was replaced by the remote state or changed by hand.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Local copy; if missing, the remote state (then stored locally). Null if there is no
    /// layout anywhere or the remote store is unreachable.
    /// </summary>
    public async Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        var local = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            return local;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Created by a reconcile in the meantime?
            local = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (local is not null || _remote.Current is not { } remote)
            {
                return local;
            }

            LayoutDocument? fromRemote;
            try
            {
                fromRemote = await remote.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (LayoutStoreUnavailableException ex)
            {
                Remember(LayoutSyncOutcome.Unavailable, ex.Message);
                _log.Info(null, $"No local layout copy and {remote.StoreDisplayName} not reachable: {ex.Message}");
                return null;
            }

            if (fromRemote is null)
            {
                Remember(LayoutSyncOutcome.InSync, null);
                return null;
            }

            await _cache.SaveAsync(fromRemote, cancellationToken).ConfigureAwait(false);
            MarkSynced(fromRemote);
            Remember(LayoutSyncOutcome.Downloaded, null);
            _log.Info(null, $"Layout loaded from {remote.StoreDisplayName} (no local copy): {Describe(fromRemote)}");
            return fromRemote;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Save locally (with backup), mark as pending and upload in the background.</summary>
    public async Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _cache.SaveAsync(layout, cancellationToken).ConfigureAwait(false);
            MarkPending(layout.UpdatedAt ?? DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }

        StartSync("after saving");
    }

    /// <summary>Reconcile with the remote store (see class description).</summary>
    public async Task<LayoutSyncOutcome> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _cache.Changed -= OnCacheChanged;
        _remote.Changed -= OnRemoteChanged;
    }

    // ------------------------------------------------------------------ Reconcile

    private async Task<LayoutSyncOutcome> SyncCoreAsync(CancellationToken cancellationToken)
    {
        if (_remote.Current is not { } remote)
        {
            return Remember(LayoutSyncOutcome.LocalOnly, null);
        }

        LayoutDocument? local;
        try
        {
            local = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _log.Error(null, $"Reconcile skipped: {ex.Message}");
            return Remember(LayoutSyncOutcome.Failed, ex.Message);
        }

        LayoutDocument? theirs;
        try
        {
            theirs = await remote.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LayoutStoreUnavailableException ex)
        {
            return Remember(LayoutSyncOutcome.Unavailable, ex.Message);
        }

        SyncState state;
        lock (_lock)
        {
            state = _state;
        }

        if (local is null && theirs is null)
        {
            return Remember(LayoutSyncOutcome.InSync, null);
        }

        if (local is null)
        {
            await _cache.SaveAsync(theirs!, cancellationToken).ConfigureAwait(false);
            MarkSynced(theirs!);
            _log.Info(null, $"Layout taken over from {remote.StoreDisplayName}: {Describe(theirs!)}");
            RaiseChanged();
            return Remember(LayoutSyncOutcome.Downloaded, null);
        }

        if (theirs is null)
        {
            // First connection (migration): exists locally, nothing remote yet → upload.
            _log.Info(null, $"{remote.StoreDisplayName} has no layout yet – uploading the local one ({Describe(local)})");
            return await UploadAsync(remote, local, cancellationToken).ConfigureAwait(false);
        }

        var localHash = ContentHash(local);
        var theirHash = ContentHash(theirs);
        if (localHash == theirHash)
        {
            if (state.Pending || state.LastSyncedHash != localHash)
            {
                MarkSynced(local);
            }

            return Remember(LayoutSyncOutcome.InSync, null);
        }

        // Never reconciled (e.g. first connection, both already have a layout): like "both changed".
        var neverSynced = state.LastSyncedHash is null;
        var theirsChanged = theirHash != state.LastSyncedHash; // changed on another device?
        var localTime = state.Pending ? Max(state.PendingSince, local.UpdatedAt) : local.UpdatedAt ?? _cache.LastWriteTime;
        var localWins = (state.Pending || neverSynced)
            && (!theirsChanged || (localTime ?? DateTimeOffset.MinValue) > (theirs.UpdatedAt ?? DateTimeOffset.MinValue));

        if (localWins)
        {
            if (theirsChanged)
            {
                // Both changed: the local version is newer. Back up the remote one first.
                var backup = WriteBackup(theirs, remote.StoreId);
                _log.Warn(null,
                    $"Layout conflict: local ({Stamp(localTime, local.UpdatedBy)}) is newer than {remote.StoreDisplayName} ({Stamp(theirs.UpdatedAt, theirs.UpdatedBy)}) – " +
                    $"local version wins, the other one is backed up{(backup is null ? "" : $" ({Path.GetFileName(backup)})")}");
            }

            return await UploadAsync(remote, local, cancellationToken).ConfigureAwait(false);
        }

        // Remote state wins. The local copy is backed up when overwritten (LocalFileLayoutStore).
        if (state.Pending || neverSynced)
        {
            _log.Warn(null,
                $"Layout conflict: {remote.StoreDisplayName} ({Stamp(theirs.UpdatedAt, theirs.UpdatedBy)}) is newer than the pending local change " +
                $"({Stamp(localTime, local.UpdatedBy)}) – taking over the remote version, the local one is backed up");
        }
        else
        {
            _log.Info(null, $"Layout in {remote.StoreDisplayName} was changed ({Stamp(theirs.UpdatedAt, theirs.UpdatedBy)}) – taking it over");
        }

        await _cache.SaveAsync(theirs, cancellationToken).ConfigureAwait(false);
        MarkSynced(theirs);
        RaiseChanged();
        return Remember(LayoutSyncOutcome.Downloaded, null);
    }

    private async Task<LayoutSyncOutcome> UploadAsync(ILayoutStore remote, LayoutDocument local, CancellationToken cancellationToken)
    {
        try
        {
            await remote.SaveAsync(local, cancellationToken).ConfigureAwait(false);
        }
        catch (LayoutStoreUnavailableException ex)
        {
            MarkPending(null);
            _log.Info(null, $"Upload to {remote.StoreDisplayName} not possible – stays pending: {ex.Message}");
            return Remember(LayoutSyncOutcome.Unavailable, ex.Message);
        }

        MarkSynced(local);
        _log.Info(null, $"Layout uploaded to {remote.StoreDisplayName}: {Describe(local)}");
        return Remember(LayoutSyncOutcome.Uploaded, null);
    }

    private void StartSync(string reason)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var previous = _lastSync;
            _lastSync = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    await SyncAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error(null, $"Layout-Abgleich ({reason}) fehlgeschlagen", ex);
                }
            });
        }
    }

    private void OnRemoteChanged(object? sender, EventArgs e) => StartSync("store notification");

    private void OnCacheChanged(object? sender, EventArgs e)
    {
        // layout.json changed by hand: counts as a local change (pending) and is shown.
        _log.Info(null, "layout.json changed from outside – reconciling");
        MarkPending(DateTimeOffset.Now);
        RaiseChanged();
        StartSync("file changed by hand");
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Error in layout Changed handler", ex);
        }
    }

    // ------------------------------------------------------------------ State

    private void MarkPending(DateTimeOffset? since)
    {
        lock (_lock)
        {
            _state = _state with
            {
                Pending = true,
                PendingSince = since is null ? _state.PendingSince : Max(_state.PendingSince, since),
            };
            SaveState(_state);
        }
    }

    private void MarkSynced(LayoutDocument layout)
    {
        lock (_lock)
        {
            _state = new SyncState(false, null, ContentHash(layout), DateTimeOffset.Now);
            SaveState(_state);
        }
    }

    private LayoutSyncOutcome Remember(LayoutSyncOutcome outcome, string? problem)
    {
        lock (_lock)
        {
            _lastOutcome = outcome;
            _lastProblem = problem;
        }

        return outcome;
    }

    private SyncState LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                return UeBarJson.Deserialize<SyncState>(File.ReadAllText(_stateFile, Encoding.UTF8)) ?? SyncState.Initial;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Reconcile state not readable ({ex.Message}) – starting fresh");
        }

        return SyncState.Initial;
    }

    private void SaveState(SyncState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, UeBarJson.Serialize(state), new UTF8Encoding(false));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Reconcile state not saved: {ex.Message}");
        }
    }

    private string? WriteBackup(LayoutDocument layout, string source)
    {
        if (_backupDirectory is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var safe = string.Concat(source.Where(char.IsLetterOrDigit));
            var path = Path.Combine(_backupDirectory, $"layout.backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safe}.json");
            File.WriteAllText(path, UeBarJson.Serialize(layout), new UTF8Encoding(false));
            LocalFileLayoutStore.PruneBackups(_backupDirectory, "layout");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Backup of the remote layout failed: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>Content without timestamps – two versions with the same content count as equal.</summary>
    internal static string ContentHash(LayoutDocument layout)
    {
        var copy = LayoutLayers.Clone(layout);
        copy.UpdatedAt = null;
        copy.UpdatedBy = null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UeBarJson.Serialize(copy))));
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static string Stamp(DateTimeOffset? at, string? by) =>
        $"{(at is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "no time")}{(by is null ? "" : $", {by}")}";

    private static string Describe(LayoutDocument layout) =>
        $"{layout.Tabs.Count} tabs, {layout.AllTiles.Count()} tiles, as of {Stamp(layout.UpdatedAt, layout.UpdatedBy)}";

    /// <summary>Content of layout.sync.json.</summary>
    internal sealed record SyncState(bool Pending, DateTimeOffset? PendingSince, string? LastSyncedHash, DateTimeOffset? LastSyncedAt)
    {
        public static SyncState Initial { get; } = new(false, null, null, null);
    }
}
