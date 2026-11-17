// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.Core.Plugins;

public enum PluginTrust
{
    /// <summary>Never decided, or the plugin's files changed since the decision → ask (again).</summary>
    Unknown,
    Allowed,
    Denied,
}

/// <summary>One remembered decision: this plugin with exactly these files.</summary>
public sealed class PluginTrustEntry
{
    public string Hash { get; set; } = "";

    public bool Allowed { get; set; }

    public DateTimeOffset DecidedAt { get; set; }

    /// <summary>For the reader of the file only.</summary>
    public string? Name { get; set; }

    public string? Version { get; set; }
}

/// <summary>
/// Remembers which plugins the user allowed or refused (<c>%APPDATA%\ueBar\plugin-trust.json</c>).
/// A decision belongs to the checksum of the plugin's files: if they change, it no longer counts.
/// </summary>
public sealed class PluginTrustStore
{
    private readonly string? _path;
    private readonly IUeLogger _log;
    private readonly Lock _lock = new();
    private Dictionary<string, PluginTrustEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">File; null = in memory only (tests).</param>
    public PluginTrustStore(string? path, IUeLogger? log = null)
    {
        _path = path;
        _log = log ?? NullLogger.Instance;
        Load();
    }

    public PluginTrust Check(string id, string hash)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || !string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return PluginTrust.Unknown;
            }

            return entry.Allowed ? PluginTrust.Allowed : PluginTrust.Denied;
        }
    }

    /// <summary>Was there a decision for this plugin with other files (so this is a changed plugin)?</summary>
    public bool HasOtherDecision(string id, string hash)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var entry) && !string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Set(string id, string hash, bool allowed, string? name = null, string? version = null)
    {
        lock (_lock)
        {
            _entries[id] = new PluginTrustEntry { Hash = hash, Allowed = allowed, DecidedAt = DateTimeOffset.UtcNow, Name = name, Version = version };
            Save();
        }

        _log.Info(null, $"Plugin '{id}' {(allowed ? "allowed" : "refused")} by the user (files {Short(hash)})");
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_entries.Remove(id))
            {
                Save();
            }
        }
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var file = UeBarJson.Deserialize<TrustFile>(File.ReadAllText(_path));
            _entries = new Dictionary<string, PluginTrustEntry>(file?.Plugins ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable = nothing decided: every plugin that is not bundled asks again.
            _log.Warn(null, $"plugin-trust.json could not be read – plugins will ask again: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, UeBarJson.Serialize(new TrustFile { Plugins = _entries }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(null, "plugin-trust.json could not be written", ex);
        }
    }

    private sealed class TrustFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, PluginTrustEntry> Plugins { get; set; } = [];
    }
}
