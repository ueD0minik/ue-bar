// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Writes to the Windows clipboard (P2-5) – for the pins' "Copy" and for plugins (<see cref="IClipboardAccess"/>).
/// Always on the UI thread (the clipboard needs STA); another app holding it briefly is retried.
/// </summary>
internal sealed class ClipboardAccess(Dispatcher dispatcher, IUeLogger log) : IClipboardAccess
{
    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken) =>
        RunAsync("text", () => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true), cancellationToken);

    public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken) =>
        RunAsync("image", () => Clipboard.SetDataObject(ImageData(png), copy: true), cancellationToken);

    /// <summary>Image as bitmap (all apps) and as PNG (keeps transparency, e.g. for Office).</summary>
    public static DataObject ImageData(byte[] png)
    {
        var data = new DataObject();
        using (var stream = new MemoryStream(png))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            data.SetImage(frame);
        }

        data.SetData("PNG", new MemoryStream(png), autoConvert: false);
        return data;
    }

    private async Task<bool> RunAsync(string what, Action write, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await dispatcher.InvokeAsync(write, DispatcherPriority.Normal, cancellationToken);
                return true;
            }
            catch (COMException ex) when (attempt < 6)
            {
                log.Debug(null, $"Clipboard busy ({what}, try {attempt}): {ex.Message}");
                await Task.Delay(40 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException or FileFormatException or ArgumentException)
            {
                log.Warn(null, $"Clipboard ({what}) failed: {ex.Message}");
                return false;
            }
        }

        return false;
    }
}
