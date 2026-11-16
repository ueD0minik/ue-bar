// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Bar.Views;

namespace UeBar.App.Bar.Media;

/// <summary>
/// Image for an image ID of a module (cover). Loads through <see cref="ModuleImageLoader"/>
/// (only with a visible panel, cached). Real http(s) addresses without an image source in the module are
/// loaded directly. Without an image it stays empty – the placeholder icon lies underneath.
/// </summary>
internal sealed class CoverImage : Image
{
    public static readonly DependencyProperty ImageIdProperty = DependencyProperty.Register(
        nameof(ImageId), typeof(string), typeof(CoverImage), new PropertyMetadata(null, (d, _) => ((CoverImage)d).Reload()));

    /// <summary>Module of the image ID if the image is not inside a tile (pill, Now Playing).</summary>
    public static readonly DependencyProperty ModuleIdProperty = DependencyProperty.Register(
        nameof(ModuleId), typeof(string), typeof(CoverImage), new PropertyMetadata(null, (d, _) => ((CoverImage)d).Reload()));

    public string? ModuleId
    {
        get => (string?)GetValue(ModuleIdProperty);
        set => SetValue(ModuleIdProperty, value);
    }

    public CoverImage()
    {
        Stretch = Stretch.UniformToFill;
        Loaded += (_, _) => Reload();
    }

    public string? ImageId
    {
        get => (string?)GetValue(ImageIdProperty);
        set => SetValue(ImageIdProperty, value);
    }

    private async void Reload()
    {
        // async void only here in the app view (event handler); errors are caught.
        try
        {
            var id = ImageId;
            if (string.IsNullOrWhiteSpace(id) || !IsLoaded)
            {
                Source = null;
                return;
            }

            var moduleId = ModuleId ?? FindModuleId();
            var loader = ModuleImageLoader.Current;
            if (moduleId is null || loader is null)
            {
                Source = DirectUrl(id);
                return;
            }

            var cached = loader.TryGetCached(moduleId, id, out var found);
            Source = found ? cached ?? DirectUrl(id) : null;
            if (found)
            {
                return;
            }

            var image = await loader.GetAsync(moduleId, id) ?? DirectUrl(id);
            if (ImageId == id)
            {
                Source = image;
            }
        }
        catch (Exception)
        {
            Source = null;
        }
    }

    private string? FindModuleId()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is TileView { DataContext: TileViewModel vm })
            {
                return vm.Model.ModuleId;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static ImageSource? DirectUrl(string id)
    {
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.DecodePixelWidth = ModuleImageLoader.DecodeWidth;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        return image;
    }
}
