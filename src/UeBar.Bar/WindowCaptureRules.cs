// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>How the image of a picked window was made (P2-6-Fix 1).</summary>
public enum WindowCaptureMethod
{
    /// <summary>Windows.Graphics.Capture – the window's own content, also behind other windows.</summary>
    GraphicsCapture,

    /// <summary><c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> – the window draws itself into a bitmap.</summary>
    PrintWindow,

    /// <summary>Cut from the frozen screen – windows in front of it are in the picture.</summary>
    Screen,
}

/// <summary>A captured image as BGRA bytes (no WPF types – the rules below are testable).</summary>
public sealed record CapturedPixels(byte[] Bgra, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Rules for the picture of a single window (P2-6-Fix 1): which windows can be picked, which part of the captured
/// surface is the window (no invisible frame, no shadow), clean rounded corners, and the order of the capture ways.
/// </summary>
public static class WindowCaptureRules
{
    /// <summary>Corner radius of Windows 11 windows in device-independent pixels (small corners: 4).</summary>
    public const double CornerRadius = 8;

    public const double SmallCornerRadius = 4;

    /// <summary>Window classes that are the desktop, not a window.</summary>
    private static readonly HashSet<string> Desktop = new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW" };

    /// <summary>A window can be picked only if it is really on the screen: visible, not minimized, not cloaked, not the desktop, not tiny.</summary>
    public static bool IsSelectable(bool visible, bool minimized, bool cloaked, string className, PixelRect bounds) =>
        visible && !minimized && !cloaked && !Desktop.Contains(className) && bounds.Width > 8 && bounds.Height > 8;

    /// <summary>
    /// The visible window inside a captured surface: the surface covers the whole window rectangle (with the invisible
    /// resize frame), <paramref name="frame"/> is what DWM shows (without frame and shadow). Null if they do not overlap.
    /// </summary>
    public static PixelRect? VisiblePart(PixelRect windowRect, PixelRect frame, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        // Windows.Graphics.Capture on Windows 11 already delivers only the visible frame – then nothing is cut.
        if (Math.Abs(imageWidth - frame.Width) <= 2 && Math.Abs(imageHeight - frame.Height) <= 2)
        {
            return new PixelRect(0, 0, imageWidth, imageHeight);
        }

        // Otherwise the surface is the whole window rectangle (PrintWindow, Windows 10) – maybe a little smaller or
        // larger (rounding, DPI): scale to it.
        var sx = windowRect.Width > 0 ? (double)imageWidth / windowRect.Width : 1;
        var sy = windowRect.Height > 0 ? (double)imageHeight / windowRect.Height : 1;
        var left = (int)Math.Round((frame.X - windowRect.X) * sx);
        var top = (int)Math.Round((frame.Y - windowRect.Y) * sy);
        var right = (int)Math.Round((frame.Right - windowRect.X) * sx);
        var bottom = (int)Math.Round((frame.Bottom - windowRect.Y) * sy);
        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);
        return right - left > 0 && bottom - top > 0 ? PixelRect.FromEdges(left, top, right, bottom) : null;
    }

    /// <summary>Cuts a part out of an image.</summary>
    public static CapturedPixels Crop(CapturedPixels image, PixelRect part)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = new byte[part.Width * part.Height * 4];
        for (var y = 0; y < part.Height; y++)
        {
            Buffer.BlockCopy(image.Bgra, ((part.Y + y) * image.Stride) + (part.X * 4), data, y * part.Width * 4, part.Width * 4);
        }

        return new CapturedPixels(data, part.Width, part.Height);
    }

    /// <summary>
    /// Corner radius in physical pixels: Windows 11 rounds normal windows (preference default or round: 8 DIP, small:
    /// 4 DIP), not maximized ones and not on Windows 10. <paramref name="preference"/> = DWMWA_WINDOW_CORNER_PREFERENCE
    /// (0 default, 1 do not round, 2 round, 3 round small), null if unknown.
    /// </summary>
    public static int CornerRadiusPixels(bool windows11, bool maximized, int? preference, double scale)
    {
        if (!windows11 || maximized || preference == 1)
        {
            return 0;
        }

        var dip = preference == 3 ? SmallCornerRadius : CornerRadius;
        return (int)Math.Round(dip * (scale <= 0 ? 1 : scale));
    }

    /// <summary>
    /// Makes the picture clean: fully opaque (some apps leave alpha 0 in their surface), outside the rounded corners
    /// fully transparent – no shadow, no desktop in the corners. Changes the bytes in place.
    /// </summary>
    public static void CleanEdges(CapturedPixels image, int radius)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = image.Bgra;
        for (var i = 3; i < data.Length; i += 4)
        {
            data[i] = 255;
        }

        radius = Math.Min(radius, Math.Min(image.Width, image.Height) / 2);
        if (radius <= 0)
        {
            return;
        }

        for (var y = 0; y < radius; y++)
        {
            for (var x = 0; x < radius; x++)
            {
                // Distance of the pixel center from the circle center; outside (with half a pixel of soft edge) = transparent.
                var dx = radius - x - 0.5;
                var dy = radius - y - 0.5;
                var outside = Math.Sqrt((dx * dx) + (dy * dy)) - radius;
                if (outside <= -0.5)
                {
                    continue;
                }

                var alpha = outside >= 0.5 ? (byte)0 : (byte)Math.Round(255 * (0.5 - outside));
                Corner(image, x, y, alpha);
                Corner(image, image.Width - 1 - x, y, alpha);
                Corner(image, x, image.Height - 1 - y, alpha);
                Corner(image, image.Width - 1 - x, image.Height - 1 - y, alpha);
            }
        }
    }

    private static void Corner(CapturedPixels image, int x, int y, byte alpha)
    {
        var at = (y * image.Stride) + (x * 4);
        if (alpha == 0)
        {
            image.Bgra[at] = image.Bgra[at + 1] = image.Bgra[at + 2] = 0;
        }

        image.Bgra[at + 3] = alpha;
    }

    /// <summary>
    /// True if a capture came back empty: every pixel black (that is how a window looks that cannot draw itself this
    /// way – some GPU or protected content). A window that really is one color (an empty white page) is not empty.
    /// </summary>
    public static bool IsBlank(CapturedPixels image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4)
        {
            return true;
        }

        var data = image.Bgra;
        for (var i = 0; i < image.Width * image.Height * 4; i += 4)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The order of the capture ways: Windows.Graphics.Capture, then PrintWindow; an empty or failed result goes to the
    /// next one. Null = neither worked – the caller cuts from the frozen screen and says so.
    /// </summary>
    public static (CapturedPixels Image, WindowCaptureMethod Method)? FirstWorking(
        Func<CapturedPixels?> graphicsCapture,
        Func<CapturedPixels?> printWindow,
        Action<WindowCaptureMethod, string>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(graphicsCapture);
        ArgumentNullException.ThrowIfNull(printWindow);
        foreach (var (method, capture) in new[] { (WindowCaptureMethod.GraphicsCapture, graphicsCapture), (WindowCaptureMethod.PrintWindow, printWindow) })
        {
            CapturedPixels? image;
            try
            {
                image = capture();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException or NotSupportedException or TimeoutException or ArgumentException)
            {
                failed?.Invoke(method, ex.GetType().Name);
                continue;
            }

            if (image is null)
            {
                failed?.Invoke(method, "no image");
            }
            else if (IsBlank(image))
            {
                failed?.Invoke(method, "empty image");
            }
            else
            {
                return (image, method);
            }
        }

        return null;
    }
}
