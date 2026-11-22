// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text.Json;
using UeBar.Core.Logging;

namespace UeBar.Core.Plugins;

/// <summary>
/// Remembers the SHA-256 of plugin files between starts (P2-5-Fix 1): reading and hashing ~50 MB of WinRT
/// projection on every start – right after Windows started or after an update, with the virus scanner looking
/// at every file – took 15–20 s. An entry is only used while the file is unchanged: same full path, same size,
/// same last-write and creation time (UTC). Anything else is hashed again. A broken or missing cache file
/// only costs time, never trust – the checksum itself is still compared with the bundled list or the trust
/// store as before.
/// </summary>
public sealed class PluginHashCache
{
    private readonly string? _file;
    private readonly IUeLogger? _log;
    private readonly Lock _lock = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _changed;
    private int _hits;
    private int _misses;

    /// <param name="file">Where the cache lives; null = in memory only (tests).</param>
    public PluginHashCache(string? file, IUeLogger? log = null)
    {
        _file = file;
        _log = log;
    }

    /// <summary>For tests: no cache at all, every call hashes.</summary>
    public static PluginHashCache None { get; } = new(null) { Disabled = true };

    private bool Disabled { get; init; }

    public (int Hits, int Misses) Stats
    {
        get
        {
            lock (_lock)
            {
                return (_hits, _misses);
            }
        }
    }

    public void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file));
            lock (_lock)
            {
                _entries = loaded is null ? new(StringComparer.OrdinalIgnoreCase) : new(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Debug(null, $"Plugin hash cache not read ({ex.Message}) – files are hashed again");
        }
    }

    /// <summary>Writes the cache if something changed (only entries of files that still exist).</summary>
    public void Save()
    {
        Dictionary<string, Entry> snapshot;
        lock (_lock)
        {
            if (!_changed || _file is null || Disabled)
            {
                return;
            }

            snapshot = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
            _changed = false;
        }

        try
        {
            foreach (var path in snapshot.Keys.Where(p => !File.Exists(p)).ToList())
            {
                snapshot.Remove(path);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Debug(null, $"Plugin hash cache not saved: {ex.Message}");
        }
    }

    /// <summary>SHA-256 (lower-case hex) of the file – from the cache while the file is unchanged.</summary>
    public string HashOf(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var size = info.Length;
        var written = info.LastWriteTimeUtc.Ticks;
        var created = info.CreationTimeUtc.Ticks;
        if (!Disabled)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(full, out var known) && known.Size == size && known.Written == written && known.Created == created && known.Sha256 is { Length: 64 })
                {
                    _hits++;
                    return known.Sha256;
                }
            }
        }

        string hash;
        using (var stream = File.OpenRead(full))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        lock (_lock)
        {
            _misses++;
            if (!Disabled)
            {
                _entries[full] = new Entry { Size = size, Written = written, Created = created, Sha256 = hash };
                _changed = true;
            }
        }

        return hash;
    }

    public sealed class Entry
    {
        public long Size { get; set; }

        public long Written { get; set; }

        public long Created { get; set; }

        public string? Sha256 { get; set; }
    }
}
