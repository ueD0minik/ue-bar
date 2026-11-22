// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.Core.Pins;

/// <summary>
/// All pins of this device (P2-5): create, change, hide, close, kept in <c>pins.json</c> plus one image file
/// per image pin. WPF-free and thread-safe – modules call it from any thread (through
/// <see cref="IPinService"/> on their context), the app from the UI thread. The app draws the windows and
/// follows <see cref="Changed"/>. Never part of the layout, never synchronized.
/// </summary>
public sealed class PinManager
{
    /// <summary>Owner ID of the core's own publications on the module bus (<c>pins.create_text</c>).</summary>
    public const string HostId = "pins";

    public const int MaxPins = 40;

    public const int MaxTextLength = 20_000;

    public const long MaxImageBytes = 40L * 1024 * 1024;

    private const string FileName = "pins.json";

    private readonly string _directory;
    private readonly IUeLogger _log;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly List<PinRecord> _pins = [];

    public PinManager(string directory, IUeLogger log, TimeProvider? time = null)
    {
        _directory = directory;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public string Directory => _directory;

    /// <summary>Raised after every change, on the caller's thread (the app hands it to its UI thread).</summary>
    public event EventHandler<PinChangedEventArgs>? Changed;

    /// <summary>Copies of all pins, oldest first.</summary>
    public IReadOnlyList<PinRecord> Pins
    {
        get
        {
            lock (_lock)
            {
                return [.. _pins.Select(p => p.Clone())];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pins.Count;
            }
        }
    }

    public PinRecord? Find(string id)
    {
        lock (_lock)
        {
            return _pins.FirstOrDefault(p => p.Id == id)?.Clone();
        }
    }

    public string? ImagePath(PinRecord pin) =>
        pin.ImageFile is { Length: > 0 } file ? Path.Combine(_directory, Path.GetFileName(file)) : null;

    // ------------------------------------------------------------------ load / save

    /// <summary>Reads <c>pins.json</c>; broken entries and images that are gone are dropped.</summary>
    public void Load()
    {
        var path = Path.Combine(_directory, FileName);
        List<PinRecord>? loaded = null;
        try
        {
            if (File.Exists(path))
            {
                loaded = UeBarJson.Deserialize<PinFile>(File.ReadAllText(path))?.Pins;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.Warn(null, $"Pins could not be read: {ex.Message}");
            TryBackupBrokenFile(path);
        }

        var valid = (loaded ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Where(p => p.Kind == PinKind.Text ? p.Text is not null : ImagePath(p) is { } image && File.Exists(image))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxPins)
            .ToList();
        foreach (var pin in valid)
        {
            pin.Opacity = ClampOpacity(pin.Opacity);
            pin.Zoom = Math.Clamp(pin.Zoom, 1, 16);
        }

        lock (_lock)
        {
            _pins.Clear();
            _pins.AddRange(valid);
        }

        if (loaded is not null && loaded.Count != valid.Count)
        {
            _log.Info(null, $"Pins: {loaded.Count - valid.Count} broken or without image dropped");
            Save();
        }

        RemoveOrphanImages(valid);
        _log.Info(null, $"Pins loaded: {valid.Count}");
    }

    private void Save()
    {
        PinFile snapshot;
        lock (_lock)
        {
            snapshot = new PinFile { Pins = [.. _pins.Select(p => p.Clone())] };
        }

        lock (_saveLock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, FileName);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, UeBarJson.Serialize(snapshot));
                Replace(tmp, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(null, $"Pins could not be saved: {ex.Message}");
            }
        }
    }

    /// <summary>Like the layout (P2-3): replacing can fail for a moment while someone reads the file – retry briefly.</summary>
    private static void Replace(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }

    private void TryBackupBrokenFile(string path)
    {
        try
        {
            File.Copy(path, path + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(null, $"Broken pins file not backed up: {ex.Message}");
        }
    }

    private void RemoveOrphanImages(IReadOnlyCollection<PinRecord> pins)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return;
            }

            var used = new HashSet<string>(pins.Select(p => p.ImageFile ?? "").Where(f => f.Length > 0), StringComparer.OrdinalIgnoreCase);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "pin-*.*"))
            {
                if (!used.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(null, $"Old pin images not removed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ create

    public string? CreateText(string? text, string? title = null, double? fontSize = null, string? owner = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var body = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        var pin = new PinRecord
        {
            Kind = PinKind.Text,
            Text = body,
            Title = TitleOf(title, body),
            FontSize = fontSize is > 0 ? Math.Clamp(fontSize.Value, 8, 96) : 0,
            Owner = owner,
        };
        return Add(pin);
    }

    public string? CreateImage(byte[]? data, string? title = null, ScreenRect? source = null, string? owner = null)
    {
        if (data is null || data.Length == 0 || data.Length > MaxImageBytes || ImageFormat.Detect(data) is not { } format)
        {
            return null;
        }

        var (width, height) = ImageFormat.Size(data);
        var pin = new PinRecord
        {
            Kind = PinKind.Image,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultImageTitle() : title.Trim(),
            PixelWidth = width,
            PixelHeight = height,
            Owner = owner,
            Source = source is { IsEmpty: false } s ? PinScreenArea.From(s) : null,
        };
        pin.Id = NewId();
        pin.ImageFile = $"pin-{pin.Id}.{format}";
        if (!WriteImage(pin, data))
        {
            return null;
        }

        var id = Add(pin);
        if (id is null)
        {
            DeleteImage(pin);
        }

        return id;
    }

    private string DefaultImageTitle() =>
        Localization.CoreText.T.Format("Pin_ImageTitle", _time.GetLocalNow().ToString("t", System.Globalization.CultureInfo.CurrentCulture));

    private string? Add(PinRecord pin)
    {
        lock (_lock)
        {
            if (_pins.Count >= MaxPins)
            {
                _log.Warn(pin.Owner, $"Pin not created: at most {MaxPins} pins");
                return null;
            }

            if (string.IsNullOrEmpty(pin.Id))
            {
                pin.Id = NewId();
            }

            pin.CreatedAt = _time.GetUtcNow();
            _pins.Add(pin);
        }

        _log.Info(pin.Owner, $"Pin created: {pin.Kind} {pin.Id}");
        Save();
        Raise(PinChangeKind.Added, pin);
        return pin.Id;
    }

    // ------------------------------------------------------------------ change

    /// <summary>Changes a pin (app). <paramref name="kind"/> says what for the listeners. False = no such pin.</summary>
    public bool Update(string id, PinChangeKind kind, Action<PinRecord> change)
    {
        PinRecord? changed;
        lock (_lock)
        {
            if (_pins.FirstOrDefault(p => p.Id == id) is not { } pin)
            {
                return false;
            }

            change(pin);
            pin.Opacity = ClampOpacity(pin.Opacity);
            pin.Zoom = Math.Clamp(pin.Zoom, 1, 16);
            pin.FontSize = pin.FontSize <= 0 ? 0 : Math.Clamp(pin.FontSize, 8, 96);
            if (pin.Text is { Length: > MaxTextLength } longText)
            {
                pin.Text = longText[..MaxTextLength];
            }

            changed = pin.Clone();
        }

        Save();
        Raise(kind, changed);
        return true;
    }

    /// <summary>Change from a module: only its own pins.</summary>
    public bool UpdateFromModule(string owner, string id, PinUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var pin = Find(id);
        if (pin is null || !SameOwner(pin, owner))
        {
            return false;
        }

        if (update.ImageData is { } data)
        {
            if (pin.Kind != PinKind.Image || data.Length == 0 || data.Length > MaxImageBytes || ImageFormat.Detect(data) is not { } format)
            {
                return false;
            }

            var old = pin.Clone();
            var file = $"pin-{pin.Id}-{_time.GetUtcNow().ToUnixTimeMilliseconds()}.{format}";
            var next = pin.Clone();
            next.ImageFile = file;
            if (!WriteImage(next, data))
            {
                return false;
            }

            var (w, h) = ImageFormat.Size(data);
            if (!Update(id, PinChangeKind.Content, p =>
                {
                    p.ImageFile = file;
                    p.PixelWidth = w;
                    p.PixelHeight = h;
                    p.Zoom = 1;
                    p.PanX = p.PanY = 0;
                }))
            {
                DeleteImage(next);
                return false;
            }

            DeleteImage(old);
        }

        if (update.Text is not null || update.Title is not null)
        {
            if (update.Text is not null && (pin.Kind != PinKind.Text || string.IsNullOrWhiteSpace(update.Text)))
            {
                return false;
            }

            Update(id, PinChangeKind.Content, p =>
            {
                if (update.Text is not null)
                {
                    p.Text = update.Text;
                }

                if (update.Title is not null)
                {
                    p.Title = TitleOf(update.Title, p.Text ?? "");
                }
            });
        }

        if (update.IsVisible is { } visible)
        {
            SetHidden(id, !visible);
        }

        return true;
    }

    public bool SetHidden(string id, bool hidden)
    {
        var pin = Find(id);
        if (pin is null || pin.Hidden == hidden)
        {
            return pin is not null;
        }

        return Update(id, PinChangeKind.Visibility, p => p.Hidden = hidden);
    }

    /// <summary>Hide or show all pins (hotkey, pin list in the bar).</summary>
    public void SetAllHidden(bool hidden)
    {
        foreach (var pin in Pins.Where(p => p.Hidden != hidden))
        {
            SetHidden(pin.Id, hidden);
        }
    }

    /// <summary>Are all pins hidden (or there are none)?</summary>
    public bool AllHidden
    {
        get
        {
            lock (_lock)
            {
                return _pins.All(p => p.Hidden);
            }
        }
    }

    // ------------------------------------------------------------------ close

    public bool Close(string id)
    {
        PinRecord? removed;
        lock (_lock)
        {
            var index = _pins.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return false;
            }

            removed = _pins[index];
            _pins.RemoveAt(index);
        }

        DeleteImage(removed);
        _log.Info(removed.Owner, $"Pin closed: {removed.Id}");
        Save();
        Raise(PinChangeKind.Removed, removed.Clone());
        return true;
    }

    public bool CloseFromModule(string owner, string id) =>
        Find(id) is { } pin && SameOwner(pin, owner) && Close(id);

    public void CloseAll()
    {
        foreach (var pin in Pins)
        {
            Close(pin.Id);
        }
    }

    /// <summary>Pins of a module as the SDK shows them.</summary>
    public IReadOnlyList<PinInfo> InfosOf(string owner)
    {
        lock (_lock)
        {
            return [.. _pins.Where(p => SameOwner(p, owner)).Select(p => p.ToInfo())];
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Raise(PinChangeKind kind, PinRecord pin)
    {
        try
        {
            Changed?.Invoke(this, new PinChangedEventArgs(kind, pin.Clone()));
        }
        catch (Exception ex)
        {
            _log.Error(null, "Pin listener failed", ex);
        }
    }

    private bool WriteImage(PinRecord pin, byte[] data)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllBytes(ImagePath(pin)!, data);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(pin.Owner, $"Pin image could not be saved: {ex.Message}");
            return false;
        }
    }

    private void DeleteImage(PinRecord pin)
    {
        if (ImagePath(pin) is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(pin.Owner, $"Pin image not deleted: {ex.Message}");
        }
    }

    private static bool SameOwner(PinRecord pin, string owner) => string.Equals(pin.Owner, owner, StringComparison.OrdinalIgnoreCase);

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    private static double ClampOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.2, 1) : 1;

    /// <summary>The given title, otherwise the first line of the text (shortened).</summary>
    public static string TitleOf(string? title, string text)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 40 ? line[..39].TrimEnd() + "…" : line;
    }

    private sealed class PinFile
    {
        public int Version { get; set; } = 1;

        public List<PinRecord> Pins { get; set; } = [];
    }
}

/// <summary>Recognizes image files by their first bytes (no decoding – the core has no image library).</summary>
public static class ImageFormat
{
    /// <summary>File extension ("png", "jpg", "bmp", "gif") or null if it is no image Windows shows.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpg";
        }

        if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return "bmp";
        }

        if (data.Length >= 10 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            return "gif";
        }

        return null;
    }

    /// <summary>Pixel size from the header of a PNG/BMP/GIF; (0, 0) if unknown (JPEG).</summary>
    public static (int Width, int Height) Size(ReadOnlySpan<byte> data)
    {
        switch (Detect(data))
        {
            case "png" when data.Length >= 24:
                return (BigEndian(data[16..20]), BigEndian(data[20..24]));
            case "bmp":
                return (BitConverter.ToInt32(data[18..22]), Math.Abs(BitConverter.ToInt32(data[22..26])));
            case "gif":
                return (data[6] | (data[7] << 8), data[8] | (data[9] << 8));
            default:
                return (0, 0);
        }

        static int BigEndian(ReadOnlySpan<byte> b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }
}
