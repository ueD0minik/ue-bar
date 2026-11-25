// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.App.Infrastructure;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.ClipHistory;

/// <summary>
/// Watches the Windows clipboard for plugins (P2-6, <see cref="IClipboardWatcher"/>). Event driven: a message-only
/// window with <c>AddClipboardFormatListener</c> exists only while some plugin watches – otherwise nothing runs. After
/// a change it waits a moment (apps write in steps), then checks the privacy marks of password managers
/// (<see cref="ClipboardPrivacy"/>) <b>before</b> reading anything; marked content is never read. Only what the
/// watchers asked for is read (images only without text, size limits). Images are encoded off the UI thread. Logs only
/// kind and size, never content.
/// </summary>
internal sealed class ClipboardWatcher : IHostClipboardWatcher, IDisposable
{
    private const int MaxIconCache = 64;

    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly ClipboardAccess _writer;
    private readonly List<WatchEntry> _watches = [];
    private readonly Dictionary<string, (string? Name, byte[]? Icon)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settle;
    private readonly uint _excludeFormat;
    private readonly uint _historyFormat;
    private readonly uint _viewerIgnoreFormat;
    private HwndSource? _window;
    private uint _lastSequence;
    private int _changes;
    private int _skippedPrivate;
    private int _imageGeneration;

    public ClipboardWatcher(Dispatcher dispatcher, IUeLogger log, ClipboardAccess writer)
    {
        _dispatcher = dispatcher;
        _log = log;
        _writer = writer;
        _excludeFormat = ClipInterop.RegisterClipboardFormat(ClipboardPrivacy.ExcludeFormat);
        _historyFormat = ClipInterop.RegisterClipboardFormat(ClipboardPrivacy.HistoryFormat);
        _viewerIgnoreFormat = ClipInterop.RegisterClipboardFormat(ClipboardPrivacy.ViewerIgnoreFormat);
        _settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ClipboardTiming.Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            OnSettled();
        };
    }

    /// <summary>For the diagnostics: changes seen, private ones skipped, watchers.</summary>
    public string Diagnostics => $"Clipboard: {_changes} changes, {_skippedPrivate} private skipped, {_watches.Count} watches, listening {_window is not null}";

    public IDisposable Watch(string moduleId, ClipboardWatchOptions options, Action<ClipboardChange> changed)
    {
        var entry = new WatchEntry(moduleId, options, changed);
        _dispatcher.BeginInvoke(() =>
        {
            if (entry.Removed)
            {
                return;
            }

            _watches.Add(entry);
            EnsureListening();
        });
        return new Unwatch(() => _dispatcher.BeginInvoke(() =>
        {
            entry.Removed = true;
            _watches.Remove(entry);
            if (_watches.Count == 0)
            {
                StopListening();
            }
        }));
    }

    public void Dispose()
    {
        if (_dispatcher.CheckAccess())
        {
            _watches.Clear();
            StopListening();
        }
    }

    // ------------------------------------------------------------------ listening

    private void EnsureListening()
    {
        if (_window is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("ueBar.Clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
        _lastSequence = ClipInterop.GetClipboardSequenceNumber(); // what is there now is not new
        if (!ClipInterop.AddClipboardFormatListener(_window.Handle))
        {
            _log.Warn(null, $"Clipboard listener could not be added (error {Marshal.GetLastWin32Error()})");
        }
        else
        {
            _log.Info(null, "Clipboard listener on");
        }
    }

    private void StopListening()
    {
        _settle.Stop();
        if (_window is null)
        {
            return;
        }

        ClipInterop.RemoveClipboardFormatListener(_window.Handle);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
        _apps.Clear();
        _log.Info(null, "Clipboard listener off");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ClipInterop.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            _settle.Stop();
            _settle.Start(); // one read after the app has finished writing
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ reading

    private void OnSettled()
    {
        if (_watches.Count == 0)
        {
            return;
        }

        var sequence = ClipInterop.GetClipboardSequenceNumber();
        if (sequence == _lastSequence)
        {
            return;
        }

        _lastSequence = sequence;
        _changes++;
        try
        {
            if (!MayRead())
            {
                _skippedPrivate++;
                _log.Debug(null, "Clipboard: marked as private – not read");
                return;
            }

            var options = Merge(_watches.Select(w => w.Options));
            var raw = Read(options);
            if (raw is null)
            {
                return;
            }

            var writer = _writer.WriterOf(sequence);
            var source = SourceApp();
            var watches = _watches.ToList();
            _ = Task.Run(() => Finish(raw, options, source, writer, watches));
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException or InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            _log.Warn(null, $"Clipboard could not be read: {ex.GetType().Name}");
        }
    }

    /// <summary>The privacy marks – read before anything else, without touching the content.</summary>
    private bool MayRead()
    {
        var exclude = ClipInterop.IsClipboardFormatAvailable(_excludeFormat);
        var ignore = ClipInterop.IsClipboardFormatAvailable(_viewerIgnoreFormat);
        var hasHistory = ClipInterop.IsClipboardFormatAvailable(_historyFormat);
        uint? value = null;
        if (hasHistory && !exclude && !ignore && _window is not null)
        {
            for (var attempt = 0; attempt < 3 && value is null; attempt++)
            {
                if (ClipInterop.OpenClipboard(_window.Handle))
                {
                    try
                    {
                        value = ClipInterop.ReadDword(_historyFormat);
                    }
                    finally
                    {
                        ClipInterop.CloseClipboard();
                    }

                    break;
                }

                Thread.Sleep(15); // another app holds it for a moment
            }
        }

        return ClipboardPrivacy.MayRead(exclude, ignore, hasHistory, value);
    }

    private static ClipboardWatchOptions Merge(IEnumerable<ClipboardWatchOptions> all)
    {
        var list = all.ToList();
        return new ClipboardWatchOptions
        {
            Images = list.Any(o => o.Images),
            MaxImageBytes = list.Max(o => o.MaxImageBytes),
            MaxTextLength = list.Max(o => o.MaxTextLength),
            MaxRichTextLength = list.Max(o => o.MaxRichTextLength),
            ThumbnailSize = list.Max(o => o.ThumbnailSize),
        };
    }

    private sealed record RawContent(string? Text, string? Html, string? Rtf, IReadOnlyList<string>? Files, Pixels? Image, byte[]? Png, bool ImageSkipped, DateTimeOffset At);

    /// <summary>On the UI thread: what the watchers want, nothing more.</summary>
    private RawContent? Read(ClipboardWatchOptions options)
    {
        IDataObject? data = null;
        for (var attempt = 1; attempt <= 4 && data is null; attempt++)
        {
            try
            {
                data = System.Windows.Clipboard.GetDataObject();
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(20 * attempt);
            }
        }

        if (data is null)
        {
            return null;
        }

        string? text = null, html = null, rtf = null;
        IReadOnlyList<string>? files = null;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
        {
            files = dropped;
        }

        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } t)
        {
            text = t.Length > options.MaxTextLength ? t[..options.MaxTextLength] : t;
        }

        if (text is not null)
        {
            html = Rich(data, DataFormats.Html, options.MaxRichTextLength);
            rtf = Rich(data, DataFormats.Rtf, options.MaxRichTextLength);
        }

        // Images only on their own: text from Office often comes with a picture of itself – that stays text.
        BitmapSource? image = null;
        byte[]? png = null;
        var skipped = false;
        if (text is null && files is null && HasImage(data))
        {
            if (!options.Images)
            {
                skipped = true;
            }
            else
            {
                (image, png) = ReadImage(data);
                if (image is not null && (long)image.PixelWidth * image.PixelHeight * 4 > options.MaxImageBytes * 8)
                {
                    image = null; // far too large even as PNG – not even encoded
                    png = null;
                    skipped = true;
                }
            }
        }

        // The pixels as plain bytes: WPF images belong to the thread that made them – the encoding happens elsewhere.
        Pixels? pixels = image is null ? null : Pixels.Of(image);
        return text is null && files is null && pixels is null && !skipped ? null : new RawContent(text, html, rtf, files, pixels, png, skipped, DateTimeOffset.Now);
    }

    /// <summary>An image as BGRA bytes – can travel to any thread.</summary>
    private sealed record Pixels(byte[] Data, int Width, int Height)
    {
        public int Stride => Width * 4;

        public static Pixels Of(BitmapSource image)
        {
            BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var data = new byte[image.PixelWidth * 4 * image.PixelHeight];
            bgra.CopyPixels(data, image.PixelWidth * 4, 0);
            return new Pixels(data, image.PixelWidth, image.PixelHeight);
        }

        /// <summary>A new image owned by the calling thread.</summary>
        public BitmapSource ToImage()
        {
            var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Stride);
            source.Freeze();
            return source;
        }
    }

    private static string? Rich(IDataObject data, string format, int max)
    {
        try
        {
            return data.GetDataPresent(format) && data.GetData(format) is string { Length: > 0 } s && s.Length <= max ? s : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool HasImage(IDataObject data) =>
        data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(DataFormats.Bitmap);

    /// <summary>PNG if an app offers it (keeps transparency), otherwise the device independent bitmap.</summary>
    private static (BitmapSource? Image, byte[]? Png) ReadImage(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
        {
            var bytes = pngStream.ToArray();
            try
            {
                var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return (frame, bytes);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
            {
                // fall back to the bitmap
            }
        }

        if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib && DibImage.Decode(dib.ToArray()) is { } decoded)
        {
            return (decoded, null);
        }

        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var copy = new WriteableBitmap(bitmap);
            copy.Freeze();
            return (copy, null);
        }

        return (null, null);
    }

    /// <summary>The app that owns the clipboard now (or the window in front) – never our own process.</summary>
    private ClipboardSourceApp? SourceApp()
    {
        var owner = ClipInterop.GetClipboardOwner();
        if (owner == IntPtr.Zero)
        {
            owner = Win32.GetForegroundWindow();
        }

        if (owner == IntPtr.Zero)
        {
            return null;
        }

        ClipInterop.GetWindowThreadProcessId(owner, out var pid);
        if (pid == 0 || pid == Environment.ProcessId || ClipInterop.ProcessPath(pid) is not { } path)
        {
            return null;
        }

        if (!_apps.TryGetValue(path, out var app))
        {
            app = (DisplayNameOf(path), AppIconPng(path));
            if (_apps.Count >= MaxIconCache)
            {
                _apps.Clear();
            }

            _apps[path] = app;
        }

        return new ClipboardSourceApp(Path.GetFileNameWithoutExtension(path), app.Name, path) { IconPng = app.Icon };
    }

    private static string? DisplayNameOf(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } name ? name.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? AppIconPng(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ off the UI thread

    private void Finish(RawContent raw, ClipboardWatchOptions options, ClipboardSourceApp? source, string? writer, IReadOnlyList<WatchEntry> watches)
    {
        try
        {
            byte[]? png = raw.Png;
            byte[]? thumbnail = null;
            var skipped = raw.ImageSkipped;
            if (raw.Image is { } pixels)
            {
                var image = pixels.ToImage();
                png ??= Encode(image);
                if (png.LongLength > options.MaxImageBytes)
                {
                    png = null;
                    skipped = true;
                }
                else
                {
                    thumbnail = Encode(Thumbnail(image, options.ThumbnailSize));
                }

                CompactSoon();
            }

            var content = new ClipboardContent { Text = raw.Text, Html = raw.Html, Rtf = raw.Rtf, Files = raw.Files, ImagePng = png };
            if (content.IsEmpty && !skipped)
            {
                return;
            }

            _log.Debug(null, $"Clipboard change: {Kind(content, skipped)}, {Size(content)} bytes{(writer is null ? "" : $", written by {writer}")}");
            foreach (var watch in watches)
            {
                if (watch.Removed)
                {
                    continue;
                }

                var o = watch.Options;
                var keepImage = png is not null && o.Images && png.LongLength <= o.MaxImageBytes;
                var change = new ClipboardChange(content with
                {
                    ImagePng = keepImage ? png : null,
                    Text = content.Text is { } t && t.Length > o.MaxTextLength ? t[..o.MaxTextLength] : content.Text,
                    Html = content.Html is { } h && h.Length <= o.MaxRichTextLength ? h : null,
                    Rtf = content.Rtf is { } r && r.Length <= o.MaxRichTextLength ? r : null,
                }, raw.At)
                {
                    Source = source,
                    ImageWidth = keepImage ? raw.Image!.Width : 0,
                    ImageHeight = keepImage ? raw.Image!.Height : 0,
                    ThumbnailPng = keepImage ? thumbnail : null,
                    ImageSkipped = skipped || (png is not null && !keepImage),
                    WrittenByModule = writer,
                };
                if (change.Content.IsEmpty && !change.ImageSkipped)
                {
                    continue;
                }

                try
                {
                    watch.Changed(change);
                }
                catch (Exception ex)
                {
                    _log.Warn(watch.ModuleId, $"Clipboard watcher failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or OutOfMemoryException or IOException)
        {
            _log.Warn(null, $"Clipboard image could not be prepared: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// An image leaves large buffers behind (pixels, PNG, the plugin's encrypted copy). An idle app allocates nothing,
    /// so the runtime would keep them for a long time: 5 s after the last image the memory is cleaned once (large
    /// objects compacted). Images are rare – this costs a few milliseconds now and then, never in a loop.
    /// </summary>
    private void CompactSoon()
    {
        var generation = Interlocked.Increment(ref _imageGeneration);
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _imageGeneration) != generation)
            {
                return; // another image came – that one cleans up
            }

            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); // also gives freed memory back to Windows
            _log.Debug(null, $"Memory cleaned after clipboard images ({GC.GetTotalMemory(false) / (1024 * 1024)} MB in use)");
        }, TaskScheduler.Default);
    }

    private static string Kind(ClipboardContent content, bool skipped) =>
        content.ImagePng is not null ? "image" : content.Files is not null ? "files" : content.Text is not null ? (content.Html is not null || content.Rtf is not null ? "rich text" : "text") : skipped ? "image (skipped)" : "?";

    private static long Size(ClipboardContent content) =>
        (content.ImagePng?.LongLength ?? 0) + ((content.Text?.Length ?? 0) * 2L) + ((content.Html?.Length ?? 0) * 2L) + ((content.Rtf?.Length ?? 0) * 2L)
        + (content.Files?.Sum(f => f.Length * 2L) ?? 0);

    public static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Thumbnail(BitmapSource image, int size)
    {
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longest <= size || longest == 0)
        {
            return image;
        }

        var scale = (double)size / longest;
        var small = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }

    private sealed class WatchEntry(string moduleId, ClipboardWatchOptions options, Action<ClipboardChange> changed)
    {
        public string ModuleId { get; } = moduleId;

        public ClipboardWatchOptions Options { get; } = options;

        public Action<ClipboardChange> Changed { get; } = changed;

        public volatile bool Removed;
    }

    private sealed class Unwatch(Action stop) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                stop();
            }
        }
    }
}

/// <summary>Device independent bitmaps from the clipboard as a WPF image (the clipboard's own conversion loses alpha and colors).</summary>
internal static class DibImage
{
    public static BitmapSource? Decode(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        if (headerSize is < 40 or > 124 || bitCount is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return null;
        }

        var palette = colorsUsed > 0 ? colorsUsed * 4 : bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        var masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS with the old header
        var offset = 14 + headerSize + masks + palette;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(offset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        try
        {
            var frame = new BmpBitmapDecoder(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}
