// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.App.Localization;
using UeBar.App.Pins;
using UeBar.Bar;
using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.App.Bar.ViewModels;

/// <summary>A pin in the list of the bar (P2-5).</summary>
internal sealed class PinItemViewModel(PinRecord pin, PinsViewModel owner) : ObservableObject
{
    public string Id { get; } = pin.Id;

    public string Title { get; } = string.IsNullOrWhiteSpace(pin.Title) ? Loc.Get("Pins_Untitled") : pin.Title;

    public string Icon { get; } = pin.Kind == PinKind.Image ? "U+EB9F" : "U+E70B";

    public bool IsVisible { get; } = !pin.Hidden;

    public bool IsClickThrough { get; } = pin.ClickThrough;

    public string Detail { get; } = string.Join(" · ", new[]
    {
        pin.Kind == PinKind.Image ? Loc.Get("Pins_Image") : Loc.Get("Pins_Text"),
        pin.Hidden ? Loc.Get("Pins_Hidden") : null,
        pin.ClickThrough ? Loc.Get("Pins_ClickThrough") : null,
        pin.Locked ? Loc.Get("Pins_Locked") : null,
    }.Where(s => s is not null));

    public string VisibilityGlyph => IsVisible ? "" : "";

    public string VisibilityTip => IsVisible ? Loc.Get("Pins_HideOne") : Loc.Get("Pins_ShowOne");

    public RelayCommand ToggleCommand => field ??= new RelayCommand(() => owner.Controller.SetHidden(Id, IsVisible));

    public RelayCommand CloseCommand => field ??= new RelayCommand(() => owner.Controller.Close(Id));

    public RelayCommand ReleaseClickThroughCommand => field ??= new RelayCommand(() => owner.Controller.SetClickThrough(Id, false));
}

/// <summary>
/// Pin list in the panel (P2-5): every pin with show/hide and close, "Hide all"/"Show all", "Close all" (asks
/// once), "Pin a note" with a short field. Click-through is lifted here too – the pin itself cannot be
/// clicked any more.
/// </summary>
internal sealed class PinsViewModel : ObservableObject
{
    public PinsViewModel(PinController controller)
    {
        Controller = controller;
        controller.Changed += (_, _) => Refresh();
        Refresh();
    }

    public PinController Controller { get; }

    public ObservableCollection<PinItemViewModel> Items { get; } = [];

    public bool HasPins => Items.Count > 0;

    public bool AnyVisible => Items.Any(i => i.IsVisible);

    public string ToggleAllText => AnyVisible ? Loc.Get("Pins_HideAll") : Loc.Get("Pins_ShowAll");

    public string HotkeyText => Hotkey.TryParse(Controller.Options.Hotkey)?.DisplayText is { } text ? Loc.F("Pins_HotkeyHint", text) : "";

    /// <summary>Text of the quick note field.</summary>
    public string NoteText
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    public string? NoteMessage
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>"Close all" was clicked once – now it asks.</summary>
    public bool IsConfirmingCloseAll
    {
        get;
        set => SetProperty(ref field, value);
    }

    public RelayCommand AddNoteCommand => field ??= new RelayCommand(() =>
    {
        if (string.IsNullOrWhiteSpace(NoteText))
        {
            NoteMessage = Loc.Get("Pins_NoteEmpty");
            return;
        }

        if (Controller.CreateNote(NoteText))
        {
            NoteText = "";
            NoteMessage = null;
        }
        else
        {
            NoteMessage = Loc.F("Pins_TooMany", PinManager.MaxPins);
        }
    });

    public RelayCommand ToggleAllCommand => field ??= new RelayCommand(Controller.ToggleAll);

    public RelayCommand CloseAllCommand => field ??= new RelayCommand(() => IsConfirmingCloseAll = true);

    public RelayCommand ConfirmCloseAllCommand => field ??= new RelayCommand(() =>
    {
        IsConfirmingCloseAll = false;
        Controller.CloseAll();
    });

    public RelayCommand CancelCloseAllCommand => field ??= new RelayCommand(() => IsConfirmingCloseAll = false);

    /// <summary>Raised when the list changed its size (the panel adapts its height).</summary>
    public event EventHandler? ContentChanged;

    public void Refresh()
    {
        Items.Clear();
        foreach (var pin in Controller.Manager.Pins.OrderByDescending(p => p.CreatedAt))
        {
            Items.Add(new PinItemViewModel(pin, this));
        }

        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(AnyVisible));
        OnPropertyChanged(nameof(ToggleAllText));
        OnPropertyChanged(nameof(HotkeyText));
        if (!HasPins)
        {
            IsConfirmingCloseAll = false;
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}
