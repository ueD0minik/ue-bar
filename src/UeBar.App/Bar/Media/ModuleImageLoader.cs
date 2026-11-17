// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UeBar.Sdk;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.Media;

/// <summary>
/// Loads images (cover art) through the module's <see cref="IImageSource"/> – the app only knows the image ID,
/// credentials stay in the module. Decodes small (96 px), keeps at most 20 images in memory
/// and only loads while the panel is visible (otherwise the request waits).
/// </summary>
internal sealed class ModuleImageLoader
{
    public const int MaxImages = 20;
    public const int DecodeWidth = 96;

    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly LinkedList<(string Key, ImageSource? Image)> _lru = new();
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.Ordinal);
    private TaskCompletionSource _visible = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _panelVisible;

    public ModuleImageLoader(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>Set at startup (views have no DI container).</summary>
    public static ModuleImageLoader? Current { get; set; }

    /// <summary>Panel visible? Only then are waiting images loaded. Only set on the UI thread.</summary>
    public bool PanelVisible
    {
        get => _panelVisible;
        set
        {
            if (_panelVisible == value)
            {
                return;
            }

            _panelVisible = value;
            if (value)
            {
                _visible.TrySetResult();
            }
            else
            {
                _visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Already loaded image (or null). found = there is an entry (also "no image").</summary>
    public ImageSource? TryGetCached(string moduleId, string imageId, out bool found)
    {
        var key = Key(moduleId, imageId);
        for (var node = _lru.First; node is not null; node = node.Next)
        {
            if (node.Value.Key == key)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                found = true;
                return node.Value.Image;
            }
        }

        found = false;
        return null;
    }

    /// <summary>Load an image (UI thread). Errors → null (the view then shows the placeholder icon).</summary>
    public Task<ImageSource?> GetAsync(string moduleId, string imageId)
    {
        var cached = TryGetCached(moduleId, imageId, out var found);
        if (found)
        {
            return Task.FromResult(cached);
        }

        var key = Key(moduleId, imageId);
        if (!_inflight.TryGetValue(key, out var task))
        {
            task = LoadAsync(moduleId, imageId, key);
            _inflight[key] = task;
        }

        return task;
    }

    private async Task<ImageSource?> LoadAsync(string moduleId, string imageId, string key)
    {
        try
        {
            if (!_panelVisible)
            {
                await _visible.Task;
            }

            var result = await _registry.InvokeAsync<ModuleImage?>(
                moduleId,
                "Load image",
                (module, ct) => module is IImageSource source ? source.LoadImageAsync(imageId, ct) : Task.FromResult<ModuleImage?>(null),
                TimeSpan.FromSeconds(12));

            var image = result.Value is { Data.Length: > 0 } data ? await Task.Run(() => Decode(data.Data)) : null;
            Remember(key, image);
            return image;
        }
        catch (Exception ex)
        {
            _log.Warn(moduleId, $"Image could not be loaded: {ex.GetType().Name}");
            Remember(key, null);
            return null;
        }
        finally
        {
            _inflight.Remove(key);
        }
    }

    private void Remember(string key, ImageSource? image)
    {
        _lru.AddFirst((key, image));
        while (_lru.Count > MaxImages)
        {
            _lru.RemoveLast();
        }
    }

    private ImageSource? Decode(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.DecodePixelWidth = DecodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            _log.Info(null, "Image not readable (format)");
            return null;
        }
    }

    private static string Key(string moduleId, string imageId) => moduleId + "|" + imageId;
}
