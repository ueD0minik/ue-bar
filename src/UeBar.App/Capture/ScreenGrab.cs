// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using UeBar.App.Interop;
using UeBar.Bar;

namespace UeBar.App.Capture;

/// <summary>
/// Copies the screen (P2-5): the whole desktop in physical pixels with GDI – the app is per-monitor DPI
/// aware, so monitors with different scaling come out pixel-exact. Layered windows are included (CAPTUREBLT).
/// </summary>
internal static class ScreenGrab
{
    public static BitmapSource Capture(PixelRect area)
    {
        var screen = PinInterop.GetDC(IntPtr.Zero);
        var memory = PinInterop.CreateCompatibleDC(screen);
        var bitmap = PinInterop.CreateCompatibleBitmap(screen, area.Width, area.Height);
        var old = PinInterop.SelectObject(memory, bitmap);
        try
        {
            if (!PinInterop.BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, PinInterop.SRCCOPY | PinInterop.CAPTUREBLT))
            {
                throw new InvalidOperationException("BitBlt failed");
            }

            PinInterop.SelectObject(memory, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var frozen = new WriteableBitmap(converted);
            frozen.Freeze();
            return frozen;
        }
        finally
        {
            PinInterop.DeleteObject(bitmap);
            PinInterop.DeleteDC(memory);
            PinInterop.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>The picture of a single window (P2-6-Fix 1) as PNG – with transparent rounded corners.</summary>
    public static byte[] Png(CapturedPixels image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A part of a capture (coordinates relative to the capture) as PNG.</summary>
    public static byte[] Png(BitmapSource capture, Int32Rect part)
    {
        var cropped = new CroppedBitmap(capture, part);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
