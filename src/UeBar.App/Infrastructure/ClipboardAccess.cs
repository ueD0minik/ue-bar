// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.App.Interop;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Writes to the Windows clipboard (P2-5) – for the pins' "Copy" and for plugins (<see cref="IClipboardAccess"/>).
/// P2-6: any content (<see cref="IHostClipboardWriter"/>) and who wrote it – the clipboard's sequence number after
/// a write is remembered with the module, so the watcher can say "this one came from the clipboard history".
/// Always on the UI thread (the clipboard needs STA); another app holding it briefly is retried.
/// </summary>
internal sealed class ClipboardAccess(Dispatcher dispatcher, IUeLogger log) : IClipboardAccess, IHostClipboardWriter
{
    private readonly Lock _lock = new();
    private (uint Sequence, string? Module) _lastWrite;

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken) =>
        RunAsync("text", null, () => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true), cancellationToken);

    public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken) =>
        RunAsync("image", null, () => Clipboard.SetDataObject(ImageData(png), copy: true), cancellationToken);

    public Task<bool> SetContentAsync(string moduleId, ClipboardContent content, CancellationToken cancellationToken) =>
        RunAsync(Describe(content), moduleId, () => Clipboard.SetDataObject(Build(content), copy: true), cancellationToken);

    /// <summary>The module that wrote the clipboard with this sequence number, or null (another app, or changed since).</summary>
    public string? WriterOf(uint sequence)
    {
        lock (_lock)
        {
            return _lastWrite.Sequence == sequence ? _lastWrite.Module : null;
        }
    }

    /// <summary>Image as bitmap (all apps) and as PNG (keeps transparency, e.g. for Office).</summary>
    public static DataObject ImageData(byte[] png)
    {
        var data = new DataObject();
        AddImage(data, png);
        return data;
    }

    private static void AddImage(DataObject data, byte[] png)
    {
        using (var stream = new MemoryStream(png))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            data.SetImage(OpaqueForBitmap(frame));
        }

        data.SetData("PNG", new MemoryStream(png), autoConvert: false);
    }

    /// <summary>
    /// P2-6-Fix 2: the old bitmap format has no transparency – transparent corners (window screenshots on Windows 11)
    /// would turn black. They get the color of the window's edge instead; the PNG next to it keeps the transparency.
    /// </summary>
    private static BitmapSource OpaqueForBitmap(BitmapSource image)
    {
        if (image.Format != System.Windows.Media.PixelFormats.Bgra32 && image.Format != System.Windows.Media.PixelFormats.Pbgra32)
        {
            return image; // no alpha channel at all
        }

        var stride = image.PixelWidth * 4;
        var bytes = new byte[stride * image.PixelHeight];
        new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(bytes, stride, 0);
        var pixels = new UeBar.Bar.CapturedPixels(bytes, image.PixelWidth, image.PixelHeight);
        if (!UeBar.Bar.WindowCaptureRules.HasTransparency(pixels))
        {
            return image;
        }

        var flat = UeBar.Bar.WindowCaptureRules.FlattenCorners(pixels);
        var result = BitmapSource.Create(flat.Width, flat.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, flat.Bgra, flat.Stride);
        result.Freeze();
        return result;
    }

    /// <summary>Every part the content has, so each app can take the richest one it knows.</summary>
    private static DataObject Build(ClipboardContent content)
    {
        var data = new DataObject();
        if (!string.IsNullOrEmpty(content.Text))
        {
            data.SetData(DataFormats.UnicodeText, content.Text, autoConvert: true);
        }

        if (!string.IsNullOrEmpty(content.Html))
        {
            data.SetData(DataFormats.Html, content.Html, autoConvert: false);
        }

        if (!string.IsNullOrEmpty(content.Rtf))
        {
            data.SetData(DataFormats.Rtf, content.Rtf, autoConvert: false);
        }

        if (content.Files is { Count: > 0 } files)
        {
            var list = new StringCollection();
            list.AddRange([.. files]);
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)), autoConvert: false); // copy, not move
        }

        if (content.ImagePng is { Length: > 0 } png)
        {
            AddImage(data, png);
        }

        return data;
    }

    private static string Describe(ClipboardContent content) =>
        content.ImagePng is { Length: > 0 } ? "image" : content.Files is { Count: > 0 } ? "files" : content.Html is not null || content.Rtf is not null ? "rich text" : "text";

    private async Task<bool> RunAsync(string what, string? moduleId, Action write, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    write();
                    lock (_lock)
                    {
                        _lastWrite = (ClipInterop.GetClipboardSequenceNumber(), moduleId);
                    }
                }, DispatcherPriority.Normal, cancellationToken);
                return true;
            }
            catch (COMException ex) when (attempt < 6)
            {
                log.Debug(moduleId, $"Clipboard busy ({what}, try {attempt}): {ex.Message}");
                await Task.Delay(40 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException or FileFormatException or ArgumentException or IOException)
            {
                log.Warn(moduleId, $"Clipboard ({what}) failed: {ex.Message}");
                return false;
            }
        }

        return false;
    }
}
