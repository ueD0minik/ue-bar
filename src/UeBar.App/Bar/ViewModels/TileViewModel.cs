// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.Bar.ViewModels;

/// <summary>
/// A tile in the grid: position/size + the model from the module. Changes to the model
/// come on any thread; collections are mirrored to the UI thread here.
/// </summary>
internal sealed class TileViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private INotifyCollectionChanged? _elements;
    private bool _disposed;

    public TileViewModel(TileModel model, TileSize size, double left, double top, double width, double height, ModuleRegistry registry, IUeLogger log, TileConfig? config = null, bool isEditing = false)
    {
        Model = model;
        Config = config;
        IsEditing = isEditing;
        Size = size;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _registry = registry;
        _log = log;
        Model.PropertyChanged += OnModelChanged;
        HookElements();
        ResyncElements();
    }

    public TileModel Model { get; }

    /// <summary>Configuration in the layout (for edit mode).</summary>
    public TileConfig? Config { get; }

    /// <summary>Edit mode: tapping selects, elements are locked, dragging moves.</summary>
    public bool IsEditing { get; }

    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Being dragged right now (half transparent).</summary>
    public bool IsDragging
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>The dragged tile would land here.</summary>
    public bool IsDropTarget
    {
        get;
        set => SetProperty(ref field, value);
    }
    public TileSize Size { get; }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>1x1: only icon, name, state.</summary>
    public bool IsSmall => Size.Columns <= 1 && Size.Rows <= 1;

    public bool ShowElements => !IsSmall && !Model.IsPlaceholder;

    /// <summary>Operable: no placeholder and not "unavailable"/"disconnected".</summary>
    public bool IsInteractive => !IsEditing && !Model.IsPlaceholder && !Model.IsUnavailable;

    /// <summary>Unavailable tiles are greyed out.</summary>
    public double CardOpacity => Model.IsUnavailable && !IsEditing ? 0.45 : 1;

    /// <summary>State line: a short hint (error) has priority over the state.</summary>
    public string? DisplayState => string.IsNullOrWhiteSpace(Model.Alert) ? Model.StateText : Model.Alert;

    /// <summary>Elements shown in this size (without TapAction), only changed on the UI thread.</summary>
    public ObservableCollection<TileElement> VisibleElements { get; } = [];

    /// <summary>Background: off = tile grey, on = accent (or the module's own color).</summary>
    public Brush CardBrush => Model.IsPlaceholder
        ? Brushes.Transparent
        : Model.IsActive ? AccentBrush : Resource("TileOffBrush");

    /// <summary>Icon color: on = white, off = the module's own color or text.</summary>
    public Brush IconBrush => Model.IsActive
        ? Resource("OnAccentTextBrush")
        : ParseBrush(Model.AccentColor) ?? Resource(Model.IsPlaceholder ? "SubTextBrush" : "TextBrush");

    public Brush TitleBrush => Model.IsActive ? Resource("OnAccentTextBrush") : Resource("TextBrush");

    /// <summary>Fill/selection in elements: half-transparent white on an active (blue) tile, otherwise accent.</summary>
    public Brush FillBrush => Model.IsActive ? Resource("OnAccentFillBrush") : AccentBrush;

    /// <summary>Secondary text in elements (artist, time): light on an active tile, otherwise grey.</summary>
    public Brush SecondaryBrush => Model.IsActive ? Resource("OnAccentTextBrush") : Resource("SubTextBrush");

    public Brush StateBrush => !string.IsNullOrWhiteSpace(Model.Alert)
        ? Resource(Model.IsActive ? "OnAccentTextBrush" : "AlertBrush")
        : Model.IsActive ? Resource("OnAccentTextBrush") : Resource("SubTextBrush");

    private Brush AccentBrush => ParseBrush(Model.AccentColor) ?? Resource("AccentBrush");

    public void Tap()
    {
        if (IsInteractive && Model.FirstElement<TapAction>() is { IsEnabled: true } tap)
        {
            _ = InteractAsync(new TileInteraction(tap.Id, InteractionKind.Tap));
        }
    }

    public async Task InteractAsync(TileInteraction interaction)
    {
        try
        {
            var result = await _registry.InteractAsync(Model, interaction);
            if (result.Outcome == GuardOutcome.TimedOut)
            {
                _log.Warn(Model.ModuleId, $"Tile {Model.TileId} does not respond in time");
            }
        }
        catch (Exception ex)
        {
            _log.Error(Model.ModuleId, "Interaction failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Model.PropertyChanged -= OnModelChanged;
        if (_elements is not null)
        {
            _elements.CollectionChanged -= OnElementsChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TileModel.StateText):
                OnPropertyChanged(nameof(DisplayState));
                break;
            case nameof(TileModel.Alert):
                OnPropertyChanged(nameof(DisplayState));
                OnPropertyChanged(nameof(StateBrush));
                break;
            case nameof(TileModel.IsUnavailable):
                OnPropertyChanged(nameof(IsInteractive));
                OnPropertyChanged(nameof(CardOpacity));
                break;
            case nameof(TileModel.IsActive):
            case nameof(TileModel.AccentColor):
            case nameof(TileModel.Placeholder):
                OnPropertyChanged(nameof(CardBrush));
                OnPropertyChanged(nameof(IconBrush));
                OnPropertyChanged(nameof(TitleBrush));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(SecondaryBrush));
                OnPropertyChanged(nameof(FillBrush));
                OnPropertyChanged(nameof(ShowElements));
                OnPropertyChanged(nameof(IsInteractive));
                break;
            case nameof(TileModel.Elements):
                HookElements();
                Dispatch(ResyncElements);
                break;
        }
    }

    private void HookElements()
    {
        if (_elements is not null)
        {
            _elements.CollectionChanged -= OnElementsChanged;
        }

        _elements = Model.Elements;
        if (_elements is not null)
        {
            _elements.CollectionChanged += OnElementsChanged;
        }
    }

    private void OnElementsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatch(ResyncElements);

    private void ResyncElements()
    {
        if (_disposed)
        {
            return;
        }

        TileElement[] snapshot;
        try
        {
            snapshot = [.. Model.Elements.Where(el => el is not null and not TapAction)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            // Changed on the module's thread meanwhile (see UiMirror) – the next change event follows.
            Dispatch(ResyncElements);
            return;
        }

        // 2x1: only the first element (e.g. brightness); 2x2 and larger: all.
        var visible = Size.Rows <= 1 ? snapshot.Take(1) : snapshot;
        VisibleElements.Clear();
        foreach (var element in visible)
        {
            VisibleElements.Add(element);
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Brush? ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        lock (BrushCache)
        {
            if (BrushCache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                BrushCache[hex] = brush;
                return brush;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
