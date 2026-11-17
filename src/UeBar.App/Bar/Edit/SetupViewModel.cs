// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.Edit;

/// <summary>A choice in the first start (e.g. an area).</summary>
internal sealed class SetupChoiceViewModel(LayoutSuggestionChoice choice) : ObservableObject
{
    public string Id { get; } = choice.Id;

    public string Label { get; } = choice.Name;

    public string? Icon { get; } = choice.Icon;

    public string? Detail { get; } = choice.Detail;

    /// <summary>Checked (named like this because of the shared button style).</summary>
    public bool IsCurrent
    {
        get;
        set => SetProperty(ref field, value);
    } = choice.Selected;
}

/// <summary>A module section in the first start.</summary>
internal sealed class SetupSectionViewModel : ObservableObject
{
    public SetupSectionViewModel(LayoutSetupSection section, IReadOnlyCollection<string>? keepSelected)
    {
        ModuleId = section.ModuleId;
        Question = section.Question;
        Hint = section.Hint;
        Icon = section.ModuleIcon;
        foreach (var choice in section.Choices)
        {
            var vm = new SetupChoiceViewModel(choice);
            if (keepSelected is not null)
            {
                vm.IsCurrent = keepSelected.Contains(choice.Id);
            }

            Choices.Add(vm);
        }

        ToggleCommand = new RelayCommand<SetupChoiceViewModel>(c =>
        {
            if (c is not null)
            {
                c.IsCurrent = !c.IsCurrent;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public string ModuleId { get; }

    public string Question { get; }

    public string? Hint { get; }

    public string Icon { get; }

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    public ObservableCollection<SetupChoiceViewModel> Choices { get; } = [];

    public RelayCommand<SetupChoiceViewModel> ToggleCommand { get; }

    public event EventHandler? SelectionChanged;

    public IReadOnlyCollection<string> Selected => [.. Choices.Where(c => c.IsCurrent).Select(c => c.Id)];
}

/// <summary>
/// First start in the panel (M5b): there is no layout yet. Every module shows its choice (generic,
/// through <see cref="LayoutSetup"/>); "Apply" builds the layout from it, "Start empty" creates an empty one.
/// Runs on the UI thread.
/// </summary>
internal sealed class SetupViewModel : ObservableObject
{
    private readonly ModuleRegistry _registry;

    public SetupViewModel(ModuleRegistry registry, bool canCancel = false)
    {
        _registry = registry;
        CanCancel = canCancel;
        ApplyCommand = new RelayCommand(() => Finish(empty: false), () => HasSelection && !IsBusy);
        EmptyCommand = new RelayCommand(() => Finish(empty: true), () => !IsBusy && !CanCancel);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        Refresh();
    }

    /// <summary>"Show first start again" (M7): there already is a layout, cancelling is possible.</summary>
    public bool CanCancel { get; }

    public bool IsFirstStart => !CanCancel;

    public RelayCommand CancelCommand { get; }

    public event EventHandler? Cancelled;

    public ObservableCollection<SetupSectionViewModel> Sections { get; } = [];

    public RelayCommand ApplyCommand { get; }

    public RelayCommand EmptyCommand { get; }

    /// <summary>Hint below the heading (e.g. "not connected yet").</summary>
    public string? Notice
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public bool HasSelection => Sections.Any(s => s.Choices.Any(c => c.IsCurrent));

    public bool IsBusy
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ApplyCommand.NotifyCanExecuteChanged();
                EmptyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Done: chosen (or empty) layout. The app saves it.</summary>
    public event EventHandler<LayoutDocument>? Completed;

    /// <summary>Read the modules' offers again (module connected, status changed). Checks are kept.</summary>
    public void Refresh()
    {
        var previous = Sections.ToDictionary(s => s.ModuleId, s => s.Selected);
        Sections.Clear();
        foreach (var section in LayoutSetup.GetSections(_registry))
        {
            var vm = new SetupSectionViewModel(section, previous.TryGetValue(section.ModuleId, out var kept) && kept.Count > 0 ? kept : null);
            vm.SelectionChanged += (_, _) => OnSelectionChanged();
            Sections.Add(vm);
        }

        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void Finish(bool empty)
    {
        IsBusy = true;
        var selection = empty
            ? new Dictionary<string, IReadOnlyCollection<string>>()
            : Sections.ToDictionary(s => s.ModuleId, s => s.Selected);
        Completed?.Invoke(this, LayoutSetup.Build(_registry, selection));
    }
}
