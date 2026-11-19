// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using UeBar.App.Bar.ViewModels;

namespace UeBar.App.Bar.Views;

public partial class PanelView : UserControl
{
    private double _lastHeight = -1;
    private double _maxGridHeight = double.PositiveInfinity;
    private double _maxBodyHeight = double.PositiveInfinity;
    private bool _editing;
    private TileViewModel? _dropTarget;

    public PanelView()
    {
        InitializeComponent();

        // P2-4: in edit mode files, folders, shortcuts and links can be dropped on the panel.
        AllowDrop = true;
        DragOver += OnExternalDragOver;
        Drop += OnExternalDrop;
        DragLeave += (_, _) => ClearExternalTarget();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is BarViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is BarViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                SetEditing(vm.IsEditing || vm.IsSetup);
            }
        };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BarViewModel.IsEditing) or nameof(BarViewModel.IsSetup) && sender is BarViewModel vm)
        {
            SetEditing(vm.IsEditing || vm.IsSetup); // the first start scrolls as a whole like edit mode
        }
    }

    private static IReadOnlyList<UeBar.Sdk.DroppedItem> DroppedItems(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            return UeBar.Bar.DropClassifier.FromPaths(paths);
        }

        // Address bar of a browser: "UniformResourceLocatorW" (UTF-16), otherwise the text.
        string? text = null;
        if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is System.IO.MemoryStream stream)
        {
            text = System.Text.Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
        }

        text ??= data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
        return UeBar.Bar.DropClassifier.FromText(text) is { } item ? [item] : [];
    }

    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        var accepted = DataContext is BarViewModel { Edit: not null }
            && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("UniformResourceLocatorW") || e.Data.GetDataPresent(DataFormats.UnicodeText));
        e.Effects = accepted ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;

        // P2-4-Fix 1: over a tile that takes entries (e.g. a collection) – mark it, the drop goes into it.
        var tile = accepted ? TileUnder(e) : null;
        if (!ReferenceEquals(tile, _externalTarget))
        {
            if (_externalTarget is not null)
            {
                _externalTarget.IsDropTarget = false;
            }

            _externalTarget = tile is { Config.Id: { } id } && DataContext is BarViewModel { Edit: { } edit } && edit.TakesDrop(id) ? tile : null;
            if (_externalTarget is not null)
            {
                _externalTarget.IsDropTarget = true;
            }
        }
    }

    private TileViewModel? _externalTarget;

    private void ClearExternalTarget()
    {
        if (_externalTarget is not null)
        {
            _externalTarget.IsDropTarget = false;
            _externalTarget = null;
        }
    }

    /// <summary>The tile under the mouse during a drag from outside.</summary>
    private TileViewModel? TileUnder(DragEventArgs e)
    {
        var hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
        while (hit is not null)
        {
            if (hit is TileView { DataContext: TileViewModel vm })
            {
                return vm;
            }

            hit = VisualTreeHelper.GetParent(hit) ?? LogicalTreeHelper.GetParent(hit);
        }

        return null;
    }

    private async void OnExternalDrop(object sender, DragEventArgs e)
    {
        // async void only here in the view (event handler); errors are caught.
        e.Handled = true;
        if (DataContext is not BarViewModel { Edit: { } edit })
        {
            return;
        }

        var onTile = _externalTarget?.Config?.Id;
        ClearExternalTarget();
        try
        {
            var items = DroppedItems(e.Data);
            if (items.Count > 0)
            {
                await edit.AddDroppedAsync(items, onTile);
            }
        }
        catch (Exception)
        {
            // A broken drag source (e.g. data that cannot be read) – nothing is added.
        }
    }

    /// <summary>Normally only the grid scrolls; in edit mode the whole body (the properties need space).</summary>
    private void SetEditing(bool editing)
    {
        _editing = editing;
        GridScroller.MaxHeight = editing ? double.PositiveInfinity : _maxGridHeight;
        BodyScroller.MaxHeight = editing ? _maxBodyHeight : double.PositiveInfinity;
    }

    /// <summary>
    /// The natural height of the content changed (tabs/tiles created, tab switched).
    /// The bar then adjusts its size.
    /// </summary>
    public event EventHandler? ContentHeightChanged;

    /// <summary>Assist field clicked – the app then allows the focus (M6).</summary>
    public event EventHandler? AssistInputRequested;

    /// <summary>Focus into the quick search field (after opening by hotkey).</summary>
    public void FocusQuickSearch() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        QuickSearchBox.Focus();
        Keyboard.Focus(QuickSearchBox);
    });

    private void OnQuickSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BarViewModel { QuickSearch: { } search })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                search.Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                search.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = search.ExecuteSelectedAsync();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Welcome assistant (M7): a PasswordBox cannot be bound – pass the value by hand.</summary>
    private void OnWelcomePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: WelcomeFieldViewModel field } box)
        {
            field.Value = box.Password;
        }
    }

    /// <summary>"Move to tab" (M7: dropdown list): chosen → move, list empty again.</summary>
    private void OnMoveTargetChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: Edit.Option<string?> item } combo || combo.DataContext is not Edit.EditSessionViewModel edit)
        {
            return;
        }

        var value = item.Value;
        combo.SelectedIndex = -1;
        if (edit.MoveTileToTabCommand.CanExecute(value))
        {
            edit.MoveTileToTabCommand.Execute(value);
        }
    }

    /// <summary>"more"/"less" below a long Assist answer.</summary>
    private void OnToggleAnswer(object sender, RoutedEventArgs e)
    {
        if (DataContext is BarViewModel { Assist: { } assist })
        {
            assist.IsAnswerExpanded = !assist.IsAnswerExpanded;
        }
    }

    private void OnAssistMouseDown(object sender, MouseButtonEventArgs e)
    {
        AssistInputRequested?.Invoke(this, EventArgs.Empty);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            AssistBox.Focus();
            Keyboard.Focus(AssistBox);
        });
    }

    private void OnAssistKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BarViewModel { Assist: { } assist } && assist.SendCommand.CanExecute(null))
        {
            assist.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Grip bar for moving in the pinned state.</summary>
    public FrameworkElement GripArea => Grip;

    /// <summary>Limits the height of the grid (the rest scrolls), depending on the monitor height.</summary>
    public double MaxGridHeight
    {
        get => _maxGridHeight;
        set
        {
            _maxGridHeight = value;
            SetEditing(_editing);
        }
    }

    /// <summary>Highest height of the body in edit mode (the rest scrolls), depending on the monitor height.</summary>
    public double MaxBodyHeight
    {
        get => _maxBodyHeight;
        set
        {
            _maxBodyHeight = value;
            SetEditing(_editing);
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Always measure with unlimited height: the natural height must not depend on how
        // large the shell currently is (pill = 40 px). Otherwise every measurement reports a "new" height,
        // which triggers the next measurement – endless loop (M2-Fix 1).
        var size = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        if (Math.Abs(size.Height - _lastHeight) > 0.5)
        {
            _lastHeight = size.Height;
            ContentHeightChanged?.Invoke(this, EventArgs.Empty);
        }

        return size;
    }

    // ------------------------------------------------------------------ Editing: text boxes

    /// <summary>Enter applies the text (otherwise only when leaving the field).</summary>
    private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ Editing: entries of a tile (P2-4-Fix 1)

    private TileItemRowDrag? _itemDrag;

    private sealed record TileItemRowDrag(string Id, FrameworkElement Grip);

    private void OnItemGripDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Edit.TileItemRow row } grip)
        {
            _itemDrag = new TileItemRowDrag(row.Id, grip);
            grip.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnItemGripMove(object sender, MouseEventArgs e)
    {
        if (_itemDrag is not null)
        {
            Mouse.OverrideCursor = Cursors.SizeAll;
        }
    }

    private void OnItemGripUp(object sender, MouseButtonEventArgs e)
    {
        if (_itemDrag is not { } drag)
        {
            return;
        }

        _itemDrag = null;
        drag.Grip.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;
        if (DataContext is not BarViewModel { Edit.TileItems: { } items })
        {
            return;
        }

        // New place = how many of the other rows have their middle above the mouse.
        var mouse = e.GetPosition(TileItemList).Y;
        var from = items.Items.ToList().FindIndex(r => r.Id == drag.Id);
        var target = 0;
        for (var i = 0; i < TileItemList.Items.Count; i++)
        {
            if (i != from && TileItemList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container
                && container.TranslatePoint(new Point(0, container.ActualHeight / 2), TileItemList).Y < mouse)
            {
                target++;
            }
        }

        if (target >= 0 && target != from)
        {
            items.Move(drag.Id, target);
        }
    }

    private void OnItemChooseFile(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BarViewModel { Edit.TileItems: { } items })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Title = UeBar.App.Localization.Loc.Get("Edit_ItemFile") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            items.Add(new UeBar.Sdk.DroppedItem(UeBar.Sdk.DropKind.File, dialog.FileName));
        }
    }

    private void OnItemChooseFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BarViewModel { Edit.TileItems: { } items })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UeBar.App.Localization.Loc.Get("Edit_ItemFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            items.Add(new UeBar.Sdk.DroppedItem(UeBar.Sdk.DropKind.Folder, dialog.FolderName));
        }
    }

    private void OnItemLinkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BarViewModel { Edit.TileItems: { } items })
        {
            items.AddLinkCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ Editing: drag & drop

    /// <summary>While dragging: mark the target.</summary>
    internal void TileDragOver(TileViewModel dragged, MouseEventArgs e)
    {
        var target = FindTarget(dragged, e);
        var tile = target?.Tile;
        if (!ReferenceEquals(tile, _dropTarget))
        {
            if (_dropTarget is not null)
            {
                _dropTarget.IsDropTarget = false;
            }

            _dropTarget = tile;
            if (tile is not null)
            {
                tile.IsDropTarget = true;
            }
        }
    }

    /// <summary>Released: move the tile to the target spot (or into the tab under the mouse).</summary>
    internal void TileDrop(TileViewModel dragged, MouseEventArgs e)
    {
        var target = FindTarget(dragged, e);
        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
            _dropTarget = null;
        }

        if (target is { } t && dragged.Config is { } config && DataContext is BarViewModel vm)
        {
            vm.DropTile(config.Id, t.TabId, t.Tile?.Config?.Id);
        }
    }

    /// <summary>Dragging cancelled: remove the mark.</summary>
    internal void TileDragCancel()
    {
        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
            _dropTarget = null;
        }
    }

    private (string TabId, TileViewModel? Tile)? FindTarget(TileViewModel dragged, MouseEventArgs e)
    {
        if (DataContext is not BarViewModel { SelectedTab: { } selected })
        {
            return null;
        }

        (string, TileViewModel?)? found = null;
        VisualTreeHelper.HitTest(
            this,
            hit => hit is UIElement { IsHitTestVisible: false } ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result =>
            {
                for (var current = result.VisualHit; current is not null && !ReferenceEquals(current, this); current = VisualTreeHelper.GetParent(current))
                {
                    if (current is TileView { DataContext: TileViewModel tile })
                    {
                        if (ReferenceEquals(tile, dragged))
                        {
                            return HitTestResultBehavior.Continue; // skip the dragged tile itself – search below it
                        }

                        found = (selected.Id, tile);
                        return HitTestResultBehavior.Stop;
                    }

                    if (current is Button { DataContext: TabViewModel tab })
                    {
                        found = (tab.Id, null);
                        return HitTestResultBehavior.Stop;
                    }

                    if (ReferenceEquals(current, TileGrid))
                    {
                        found = (selected.Id, null); // free area in the grid = at the end
                        return HitTestResultBehavior.Stop;
                    }
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(e.GetPosition(this)));
        return found;
    }
}
