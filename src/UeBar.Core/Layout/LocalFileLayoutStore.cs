// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UeBar.Core.Abstractions;
using UeBar.Core.Json;
using UeBar.Core.Logging;

namespace UeBar.Core.Layout;

/// <summary>
/// Layout as a readable JSON file (no sync). Writes atomically, creates a backup before
/// overwriting (the last <see cref="MaxBackups"/>) and reports changes from outside
/// (file edited by hand) through <see cref="Changed"/>. The file watcher only runs
/// while someone has subscribed to <see cref="Changed"/>.
/// </summary>
public sealed class LocalFileLayoutStore : ILayoutStore, IDisposable
{
    public const int MaxBackups = 10;

    private readonly string _path;
    private readonly string? _backupDirectory;
    private readonly IUeLogger _log;
    private readonly Lock _lock = new();
    private EventHandler? _changed;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastKnownHash;

    public LocalFileLayoutStore(string path, string? backupDirectory = null, IUeLogger? log = null)
    {
        _path = Path.GetFullPath(path);
        _backupDirectory = backupDirectory;
        _log = log ?? NullLogger.Instance;
    }

    public string StoreId => "local";

    public string StoreDisplayName => CoreText.T["Layout_LocalStoreName"];

    public string FilePath => _path;

    public event EventHandler? Changed
    {
        add
        {
            lock (_lock)
            {
                _changed += value;
                EnsureWatcher();
            }
        }

        remove
        {
            lock (_lock)
            {
                _changed -= value;
                if (_changed is null)
                {
                    StopWatcher();
                }
            }
        }
    }

    public async Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _lastKnownHash = Hash(json);
        }

        try
        {
            return UeBarJson.Deserialize<LayoutDocument>(json) ?? new LayoutDocument();
        }
        catch (JsonException ex)
        {
            // Do not silently replace with an empty layout – otherwise a file edited by hand
            // would be gone on the next save.
            throw new InvalidDataException(CoreText.T.Format("Layout_FileInvalid", _path, ex.Message), ex);
        }
    }

    public async Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var json = UeBarJson.Serialize(layout);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        if (_backupDirectory is not null && File.Exists(_path))
        {
            BackupCurrentFile();
        }

        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _lastKnownHash = Hash(json);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Time of the file's last change (null if it does not exist).</summary>
    public DateTimeOffset? LastWriteTime => File.Exists(_path) ? new DateTimeOffset(File.GetLastWriteTime(_path)) : null;

    /// <summary>Keep only the last <see cref="MaxBackups"/> backups (names start with the timestamp).</summary>
    public static void PruneBackups(string backupDirectory, string name)
    {
        var old = Directory.EnumerateFiles(backupDirectory, $"{name}.backup-*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(MaxBackups);
        foreach (var file in old)
        {
            File.Delete(file);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _changed = null;
            StopWatcher();
        }
    }

    private void BackupCurrentFile()
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory!);
            var name = Path.GetFileNameWithoutExtension(_path);
            var target = Path.Combine(_backupDirectory!, $"{name}.backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(_path, target, overwrite: true);
            PruneBackups(_backupDirectory!, name);
        }
        catch (IOException ex)
        {
            _log.Warn(null, $"Layout backup failed: {ex.Message}");
        }
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        _debounce = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Editors often write several times – collect briefly.
        lock (_lock)
        {
            _debounce?.Change(300, Timeout.Infinite);
        }
    }

    private void OnDebounced()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return;
        }

        EventHandler? handlers;
        lock (_lock)
        {
            var hash = Hash(json);
            if (hash == _lastKnownHash)
            {
                return; // own change or nothing new
            }

            _lastKnownHash = hash;
            handlers = _changed;
        }

        try
        {
            handlers?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Error in layout Changed handler", ex);
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
