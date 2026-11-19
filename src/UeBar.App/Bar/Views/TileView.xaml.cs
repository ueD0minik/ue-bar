// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UeBar.App.Bar.ViewModels;

namespace UeBar.App.Bar.Views;

/// <summary>
/// A tile. Tapping the area = TapAction; elements report their interaction through
/// <see cref="TileEvents.InteractionRequestedEvent"/>, both go to the view model and from there
/// through the registry to the module. In edit mode: tap = select, drag = move.
/// </summary>
public partial class TileView : UserControl
{
    private const double DragThreshold = 6;

    private bool _pressed;
    private bool _dragging;
    private bool _releasing;
    private Point _pressPoint;

    public TileView()
    {
        InitializeComponent();
        AddHandler(TileEvents.InteractionRequestedEvent, new TileInteractionEventHandler((_, e) =>
        {
            e.Handled = true;
            if (DataContext is TileViewModel { IsEditing: false } vm)
            {
                _ = vm.InteractAsync(e.Interaction);
            }
        }));
    }

    private TileViewModel? Vm => DataContext as TileViewModel;

    private PanelView? Panel
    {
        get
        {
            DependencyObject? current = this;
            while (current is not null and not PanelView)
            {
                current = VisualTreeHelper.GetParent(current);
            }

            return current as PanelView;
        }
    }

    private void OnCardDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || (!vm.IsEditing && !vm.IsInteractive))
        {
            return;
        }

        _pressed = true;
        _pressPoint = e.GetPosition(Panel);
        SetPressed(true);
        if (vm.IsEditing)
        {
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnCardMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || Vm is not { IsEditing: true } vm || Panel is not { } panel)
        {
            return;
        }

        var point = e.GetPosition(panel);
        var delta = point - _pressPoint;
        if (!_dragging && delta.Length < DragThreshold)
        {
            return;
        }

        if (!_dragging)
        {
            _dragging = true;
            vm.IsDragging = true;
            SetPressed(false);
        }

        DragShift.X = delta.X;
        DragShift.Y = delta.Y;
        panel.TileDragOver(vm, e);
    }

    private void OnCardUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        SetPressed(false);
        var vm = Vm;
        if (vm is { IsEditing: true })
        {
            // Evaluate first, then release the capture – otherwise LostMouseCapture reports "cancelled".
            var wasDragging = _dragging;
            _releasing = true;
            ((UIElement)sender).ReleaseMouseCapture();
            _releasing = false;
            e.Handled = true;
            if (wasDragging)
            {
                EndDrag(vm, e, drop: true);
            }
            else if (DataContext is TileViewModel tile && Panel?.DataContext is BarViewModel bar)
            {
                bar.SelectTileCommand.Execute(tile);
            }

            return;
        }

        if (vm is { IsInteractive: true })
        {
            vm.Tap();
        }
    }

    /// <summary>P2-4: the module's image (e.g. a real app icon) replaces the glyph once it is there.</summary>
    private void OnTileImageChanged(object? sender, EventArgs e)
    {
        if (DataContext is TileViewModel vm && sender is Media.CoverImage image)
        {
            vm.HasImage = image.Source is not null;
        }
    }

    private void OnCardLeave(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            return; // while dragging the mouse capture holds the tile
        }

        _pressed = false;
        SetPressed(false);
    }

    private void OnCardLostCapture(object sender, MouseEventArgs e)
    {
        if (_releasing)
        {
            return;
        }

        if (_dragging && Vm is { } vm)
        {
            EndDrag(vm, e, drop: false);
        }

        _pressed = false;
    }

    private void EndDrag(TileViewModel vm, MouseEventArgs e, bool drop)
    {
        _dragging = false;
        DragShift.X = 0;
        DragShift.Y = 0;
        vm.IsDragging = false;
        if (drop)
        {
            Panel?.TileDrop(vm, e);
        }
        else
        {
            Panel?.TileDragCancel();
        }
    }

    private void SetPressed(bool pressed)
    {
        PressScale.ScaleX = pressed ? 0.97 : 1;
        PressScale.ScaleY = pressed ? 0.97 : 1;
    }
}
