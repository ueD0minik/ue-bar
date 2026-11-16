// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace UeBar.App.Settings;

/// <summary>The app icon (M7): pill with "ue" – for windows, the About page and the tray.</summary>
internal static class AppIcon
{
    private static ImageSource? _image;

    /// <summary>256 px image (for windows and large displays).</summary>
    public static ImageSource ImageSource => _image ??= Load();

    /// <summary>Icon for the tray in the matching size (DPI).</summary>
    public static Drawing.Icon TrayIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ueBar.ico"))
            ?? throw new InvalidOperationException("ueBar.ico missing from the resources");
        using var stream = info.Stream;
        return new Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private static ImageSource Load()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/ueBar-256.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
