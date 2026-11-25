// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UeBar.Sdk;

namespace UeBar.Modules.Clipboard;

/// <summary>What an entry holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClipKind>))]
internal enum ClipKind
{
    Text,
    Image,
    Files,
}

/// <summary>
/// One entry of the history as the index knows it – metadata and a short preview; the content itself lives in its own
/// encrypted file and is read only when it is used.
/// </summary>
internal sealed record ClipEntry
{
    public required string Id { get; init; }

    public ClipKind Kind { get; init; }

    /// <summary>SHA-256 of the main content – the same content is never stored twice.</summary>
    public required string Hash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last copied or pasted – the list is sorted by this, newest first.</summary>
    public DateTimeOffset UsedAt { get; init; }

    /// <summary>"Merken" (★): stays until removed by hand – no limit applies to it.</summary>
    public bool Kept { get; init; }

    /// <summary>Bytes on disk (content + preview picture, encrypted).</summary>
    public long Size { get; init; }

    /// <summary>Original size of the content in bytes (text as UTF-16, picture as PNG).</summary>
    public long ContentSize { get; init; }

    /// <summary>Start of the text (whitespace folded) or the file names – for lists.</summary>
    public string Preview { get; init; } = "";

    /// <summary>More text for the search (at most <see cref="ClipStore.SearchTextLength"/> characters).</summary>
    public string? SearchText { get; init; }

    public string? AppProcess { get; init; }

    public string? AppName { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public bool HasHtml { get; init; }

    public bool HasRtf { get; init; }

    public int FileCount { get; init; }

    public bool HasThumbnail { get; init; }
}

/// <summary>Limits of the history (settings).</summary>
internal sealed record ClipLimits(int MaxEntries, TimeSpan MaxAge, long MaxTotalBytes)
{
    public static ClipLimits Default { get; } = new(100, TimeSpan.FromDays(7), 200L * 1024 * 1024);
}

/// <summary>What <see cref="ClipStore.Add"/> did.</summary>
internal sealed record ClipAddResult(ClipEntry? Entry, bool IsNew, IReadOnlyList<string> Removed)
{
    public static ClipAddResult Nothing { get; } = new(null, false, []);
}

/// <summary>
/// The stored history (P2-6): <c>index.clip</c> (list, previews, app icons) and per entry <c>&lt;id&gt;.clip</c> (content)
/// plus <c>&lt;id&gt;.thumb</c> (preview picture) – every file encrypted (<see cref="IClipProtector"/>, DPAPI). Files are
/// written atomically; a removed entry's files are deleted at once; files no entry knows are deleted on loading. Thread
/// safe (one lock); nothing here logs content.
/// </summary>
internal sealed class ClipStore
{
    public const string IndexFile = "index.clip";
    public const string DataExtension = ".clip";
    public const string ThumbExtension = ".thumb";
    public const int PreviewLength = 240;
    public const int SearchTextLength = 4000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IClipProtector _protector;
    private readonly TimeProvider _time;
    private readonly Action<string>? _warn;
    private readonly Lock _lock = new();
    private readonly List<ClipEntry> _entries = [];
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _thumbs = new(StringComparer.Ordinal);

    public ClipStore(string folder, IClipProtector protector, TimeProvider time, Action<string>? warn = null)
    {
        Folder = folder;
        _protector = protector;
        _time = time;
        _warn = warn;
    }

    public string Folder { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ClipEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _entries.Sum(e => e.Size);
            }
        }
    }

    public ClipEntry? Find(string id)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.Id == id);
        }
    }

    // ------------------------------------------------------------------ load

    /// <summary>
    /// Reads the index (an unreadable one – other user, broken – starts an empty history), drops entries whose file is
    /// gone and deletes files no entry knows. Returns the number of entries.
    /// </summary>
    public int Load()
    {
        lock (_lock)
        {
            _entries.Clear();
            _icons.Clear();
            _thumbs.Clear();
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, IndexFile);
            if (File.Exists(path))
            {
                try
                {
                    var index = JsonSerializer.Deserialize<IndexDocument>(_protector.Unprotect(File.ReadAllBytes(path)), Json);
                    foreach (var entry in index?.Entries ?? [])
                    {
                        if (File.Exists(DataPath(entry.Id)) && IsSafeId(entry.Id))
                        {
                            _entries.Add(entry);
                        }
                    }

                    foreach (var (process, icon) in index?.Icons ?? [])
                    {
                        _icons[process] = Convert.FromBase64String(icon);
                    }
                }
                catch (Exception ex) when (ex is CryptographicDataException or JsonException or IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
                {
                    _warn?.Invoke($"Clipboard history could not be read ({ex.GetType().Name}) – starting empty");
                    _entries.Clear();
                    _icons.Clear();
                }
            }

            Sort();
            foreach (var entry in _entries.Where(e => e.HasThumbnail))
            {
                if (ReadThumb(entry.Id) is { } thumb)
                {
                    _thumbs[entry.Id] = thumb;
                }
            }

            DeleteOrphans();
            SaveIndex();
            return _entries.Count;
        }
    }

    /// <summary>Deletes every file in the folder that belongs to no entry (also left-over temporary files).</summary>
    public int DeleteOrphans()
    {
        lock (_lock)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IndexFile };
            foreach (var entry in _entries)
            {
                known.Add(entry.Id + DataExtension);
                known.Add(entry.Id + ThumbExtension);
            }

            var deleted = 0;
            if (!Directory.Exists(Folder))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                var name = Path.GetFileName(file);
                var ours = name.EndsWith(DataExtension, StringComparison.OrdinalIgnoreCase) || name.EndsWith(ThumbExtension, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (ours && !known.Contains(name) && TryDelete(file))
                {
                    deleted++;
                }
            }

            return deleted;
        }
    }

    // ------------------------------------------------------------------ add

    /// <summary>
    /// Stores a clipboard change. The same content again only moves its entry to the top (and takes the new app).
    /// <paramref name="bumpOnly"/> = our own pasting: never a new entry. Applies <paramref name="limits"/> afterwards.
    /// </summary>
    public ClipAddResult Add(ClipboardChange change, ClipLimits limits, bool bumpOnly = false)
    {
        var content = change.Content;
        var kind = KindOf(content);
        if (kind is null)
        {
            return ClipAddResult.Nothing;
        }

        var hash = HashOf(kind.Value, content);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var existing = _entries.FindIndex(e => e.Hash == hash);
            if (existing >= 0)
            {
                var bumped = _entries[existing] with
                {
                    UsedAt = TopTime(now),
                    AppProcess = bumpOnly ? _entries[existing].AppProcess : change.Source?.ProcessName ?? _entries[existing].AppProcess,
                    AppName = bumpOnly ? _entries[existing].AppName : change.Source?.DisplayName ?? change.Source?.ProcessName ?? _entries[existing].AppName,
                };
                _entries[existing] = bumped;
                RememberIcon(bumpOnly ? null : change.Source);
                Sort();
                SaveIndex();
                return new ClipAddResult(bumped, false, []);
            }

            if (bumpOnly)
            {
                return ClipAddResult.Nothing;
            }

            var id = Guid.NewGuid().ToString("N");
            var dataSize = WriteData(id, content);
            long thumbSize = 0;
            if (change.ThumbnailPng is { Length: > 0 } thumb)
            {
                thumbSize = WriteProtected(ThumbPath(id), thumb);
                _thumbs[id] = thumb;
            }

            var text = content.Text ?? "";
            var entry = new ClipEntry
            {
                Id = id,
                Kind = kind.Value,
                Hash = hash,
                CreatedAt = now,
                UsedAt = TopTime(now),
                Size = dataSize + thumbSize,
                ContentSize = ContentSizeOf(content),
                Preview = kind == ClipKind.Files ? FilesPreview(content.Files!) : kind == ClipKind.Text ? Fold(text, PreviewLength) : "",
                SearchText = kind == ClipKind.Files ? string.Join(" ", content.Files!) : kind == ClipKind.Text ? (text.Length > SearchTextLength ? text[..SearchTextLength] : text) : null,
                AppProcess = change.Source?.ProcessName,
                AppName = change.Source?.DisplayName ?? change.Source?.ProcessName,
                ImageWidth = kind == ClipKind.Image ? change.ImageWidth : 0,
                ImageHeight = kind == ClipKind.Image ? change.ImageHeight : 0,
                HasHtml = !string.IsNullOrEmpty(content.Html),
                HasRtf = !string.IsNullOrEmpty(content.Rtf),
                FileCount = content.Files?.Count ?? 0,
                HasThumbnail = thumbSize > 0,
            };
            _entries.Insert(0, entry);
            RememberIcon(change.Source);
            var removed = PruneLocked(limits);
            Sort();
            SaveIndex();
            return new ClipAddResult(entry, true, removed);
        }
    }

    // ------------------------------------------------------------------ change

    /// <summary>Moves an entry to the top (used again).</summary>
    public bool Touch(string id) => Change(id, e => e with { UsedAt = TopTime(_time.GetUtcNow()) });

    /// <summary>Now – or just after the newest entry, so "to the top" holds even within the same clock tick.</summary>
    private DateTimeOffset TopTime(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _entries.Count > 0 && _entries.Max(e => e.UsedAt) is var newest && newest >= now ? newest.AddTicks(1) : now;
        }
    }

    public bool SetKept(string id, bool kept) => Change(id, e => e with { Kept = kept });

    private bool Change(string id, Func<ClipEntry, ClipEntry> change)
    {
        lock (_lock)
        {
            var at = _entries.FindIndex(e => e.Id == id);
            if (at < 0)
            {
                return false;
            }

            _entries[at] = change(_entries[at]);
            Sort();
            SaveIndex();
            return true;
        }
    }

    /// <summary>Removes an entry and deletes its files at once.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var at = _entries.FindIndex(e => e.Id == id);
            if (at < 0)
            {
                return false;
            }

            RemoveAtLocked(at);
            SaveIndex();
            return true;
        }
    }

    /// <summary>Empties the history; kept entries stay if wanted. Returns how many were removed.</summary>
    public int Clear(bool keepKept)
    {
        lock (_lock)
        {
            var removed = 0;
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (!keepKept || !_entries[i].Kept)
                {
                    RemoveAtLocked(i);
                    removed++;
                }
            }

            SaveIndex();
            DeleteOrphans();
            return removed;
        }
    }

    /// <summary>Applies the limits (count, age, total size); kept entries are never removed. Returns the removed IDs.</summary>
    public IReadOnlyList<string> Prune(ClipLimits limits)
    {
        lock (_lock)
        {
            var removed = PruneLocked(limits);
            if (removed.Count > 0)
            {
                SaveIndex();
            }

            return removed;
        }
    }

    private List<string> PruneLocked(ClipLimits limits)
    {
        var removed = new List<string>();
        var now = _time.GetUtcNow();
        Sort();

        // Oldest first: too old, then too many, then too large.
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].Kept && now - _entries[i].UsedAt > limits.MaxAge)
            {
                removed.Add(_entries[i].Id);
                RemoveAtLocked(i);
            }
        }

        var free = _entries.Where(e => !e.Kept).ToList();
        foreach (var entry in free.Skip(Math.Max(0, limits.MaxEntries)))
        {
            removed.Add(entry.Id);
            RemoveAtLocked(_entries.IndexOf(entry));
        }

        var total = _entries.Sum(e => e.Size);
        for (var i = _entries.Count - 1; i >= 0 && total > limits.MaxTotalBytes; i--)
        {
            if (!_entries[i].Kept)
            {
                total -= _entries[i].Size;
                removed.Add(_entries[i].Id);
                RemoveAtLocked(i);
            }
        }

        return removed;
    }

    private void RemoveAtLocked(int index)
    {
        var entry = _entries[index];
        _entries.RemoveAt(index);
        _thumbs.Remove(entry.Id);
        TryDelete(DataPath(entry.Id));
        TryDelete(ThumbPath(entry.Id));
    }

    // ------------------------------------------------------------------ read

    /// <summary>The content of an entry (decrypted now, not kept in memory). Null if the file is gone or unreadable.</summary>
    public ClipboardContent? LoadContent(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        string path;
        lock (_lock)
        {
            if (_entries.All(e => e.Id != id))
            {
                return null;
            }

            path = DataPath(id);
        }

        try
        {
            var bytes = _protector.Unprotect(File.ReadAllBytes(path));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            var part = JsonSerializer.Deserialize<DataDocument>(bytes.AsSpan(4, length), Json) ?? new DataDocument();
            var png = bytes.Length > 4 + length ? bytes[(4 + length)..] : null;
            return new ClipboardContent { Text = part.Text, Html = part.Html, Rtf = part.Rtf, Files = part.Files, ImagePng = png };
        }
        catch (Exception ex) when (ex is CryptographicDataException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _warn?.Invoke($"Clipboard entry could not be read ({ex.GetType().Name})");
            return null;
        }
    }

    public byte[]? Thumbnail(string id)
    {
        lock (_lock)
        {
            return _thumbs.GetValueOrDefault(id);
        }
    }

    public byte[]? AppIcon(string? process)
    {
        if (process is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _icons.GetValueOrDefault(process);
        }
    }

    // ------------------------------------------------------------------ helpers

    public static ClipKind? KindOf(ClipboardContent content) =>
        content.Files is { Count: > 0 } ? ClipKind.Files
        : !string.IsNullOrEmpty(content.Text) ? ClipKind.Text
        : content.ImagePng is { Length: > 0 } ? ClipKind.Image
        : null;

    /// <summary>The main content decides: text exactly, file paths without case, the picture byte for byte.</summary>
    public static string HashOf(ClipKind kind, ClipboardContent content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(kind.ToString() + "\n"));
        switch (kind)
        {
            case ClipKind.Files:
                sha.AppendData(Encoding.UTF8.GetBytes(string.Join("\n", content.Files!.Select(f => f.Trim().ToUpperInvariant()))));
                break;
            case ClipKind.Text:
                sha.AppendData(Encoding.UTF8.GetBytes(content.Text!));
                break;
            default:
                sha.AppendData(content.ImagePng!);
                break;
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Whitespace folded to single spaces, cut with "…".</summary>
    public static string Fold(string text, int max)
    {
        var builder = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString();
    }

    private static string FilesPreview(IReadOnlyList<string> files) =>
        Fold(string.Join(", ", files.Select(f => Path.GetFileName(f.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : f)), PreviewLength);

    private static long ContentSizeOf(ClipboardContent content) =>
        (content.ImagePng?.LongLength ?? 0) + ((content.Text?.Length ?? 0) * 2L) + ((content.Html?.Length ?? 0) * 2L) + ((content.Rtf?.Length ?? 0) * 2L)
        + (content.Files?.Sum(f => f.Length * 2L) ?? 0);

    private void RememberIcon(ClipboardSourceApp? source)
    {
        if (source is { IconPng: { Length: > 0 and < 64 * 1024 } icon })
        {
            _icons[source.ProcessName] = icon;
        }
    }

    private void Sort() => _entries.Sort((a, b) => b.UsedAt.CompareTo(a.UsedAt));

    private long WriteData(string id, ClipboardContent content)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new DataDocument { Text = content.Text, Html = content.Html, Rtf = content.Rtf, Files = content.Files?.ToList() }, Json);
        var png = content.ImagePng ?? [];
        var plain = new byte[4 + json.Length + png.Length];
        BinaryPrimitives.WriteInt32LittleEndian(plain, json.Length);
        json.CopyTo(plain, 4);
        png.CopyTo(plain, 4 + json.Length);
        return WriteProtected(DataPath(id), plain);
    }

    private long WriteProtected(string path, byte[] plain)
    {
        var data = _protector.Protect(plain);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
        return data.LongLength;
    }

    private byte[]? ReadThumb(string id)
    {
        try
        {
            var path = ThumbPath(id);
            return File.Exists(path) ? _protector.Unprotect(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is CryptographicDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var used = new HashSet<string>(_entries.Select(e => e.AppProcess).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var unused in _icons.Keys.Where(k => !used.Contains(k)).ToList())
            {
                _icons.Remove(unused);
            }

            var document = new IndexDocument
            {
                Version = 1,
                Entries = [.. _entries],
                Icons = _icons.ToDictionary(i => i.Key, i => Convert.ToBase64String(i.Value)),
            };
            WriteProtected(Path.Combine(Folder, IndexFile), JsonSerializer.SerializeToUtf8Bytes(document, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _warn?.Invoke($"Clipboard history could not be saved ({ex.GetType().Name})");
        }
    }

    private string DataPath(string id) => Path.Combine(Folder, id + DataExtension);

    private string ThumbPath(string id) => Path.Combine(Folder, id + ThumbExtension);

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private sealed class IndexDocument
    {
        public int Version { get; set; }

        public List<ClipEntry> Entries { get; set; } = [];

        public Dictionary<string, string> Icons { get; set; } = [];
    }

    private sealed class DataDocument
    {
        public string? Text { get; set; }

        public string? Html { get; set; }

        public string? Rtf { get; set; }

        public List<string>? Files { get; set; }
    }
}
