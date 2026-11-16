// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.Bar;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.App.Bar.ViewModels;

/// <summary>An input field in the welcome assistant.</summary>
internal sealed class WelcomeFieldViewModel(SettingField setting, string? value) : ObservableObject
{
    public SettingField Field { get; } = setting;

    public string Label => Field.Label;

    public string? Description => Field.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Field.Description);

    public string Placeholder => Field.Placeholder ?? "";

    public bool IsSecret => Field.IsSecret;

    public bool IsText => !Field.IsSecret;

    public string Value
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                OnPropertyChanged(nameof(IsEmpty));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    } = value ?? "";

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public event EventHandler? Changed;
}

/// <summary>Test result line.</summary>
internal sealed record WelcomeTestLine(string Text, bool Success);

/// <summary>
/// Welcome assistant at the first start (M7) – in the panel. Generic through the "Essential" fields
/// of a module (<see cref="SettingField.Essential"/>): address(es) → access key → test the
/// connection → first-start layout (M5b). "Skip" at any time.
/// </summary>
internal sealed class WelcomeViewModel : ObservableObject
{
    private readonly ModuleRegistry _registry;
    private readonly SettingsService _settings;
    private readonly IUeLogger _log;
    private readonly WelcomeFlow _flow = new();

    public WelcomeViewModel(ModuleRegistry registry, SettingsService settings, IUeLogger log, ModuleEntry module, IReadOnlyList<SettingField> fields)
    {
        _registry = registry;
        _settings = settings;
        _log = log;
        Module = module;
        UeBar.Core.Localization.UiCultureSource.Shared.Changed += (_, _) => OnPropertyChanged(string.Empty);
        var stored = settings.GetModuleSettings(module.Id);
        foreach (var field in fields.Where(f => f.Essential))
        {
            var vm = new WelcomeFieldViewModel(field, field.IsSecret ? null : stored.GetString(field.Key));
            vm.Changed += (_, _) => Refresh();
            (field.IsSecret ? TokenFields : AddressFields).Add(vm);
        }

        NextCommand = new RelayCommand(async () => await NextAsync(), () => !IsBusy && _flow.CanGoNext(HasAddress, HasToken));
        BackCommand = new RelayCommand(() =>
        {
            _flow.Back();
            Refresh();
        }, () => !IsBusy && _flow.CanGoBack);
        SkipCommand = new RelayCommand(() => Finish(skipped: true), () => !IsBusy);
        TestCommand = new RelayCommand(async () => await TestAsync(), () => !IsBusy);
        ContinueAnywayCommand = new RelayCommand(async () =>
        {
            await SaveAsync();
            _flow.ContinueAnyway();
            Refresh();
            CheckDone();
        }, () => !IsBusy && _flow.TestPassed == false);
    }

    public ModuleEntry Module { get; }

    public string ModuleName => Module.DisplayName;

    public ObservableCollection<WelcomeFieldViewModel> AddressFields { get; } = [];

    public ObservableCollection<WelcomeFieldViewModel> TokenFields { get; } = [];

    public ObservableCollection<WelcomeTestLine> TestLines { get; } = [];

    public RelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand TestCommand { get; }

    public RelayCommand ContinueAnywayCommand { get; }

    public WelcomeStep Step => _flow.Step;

    public bool IsAddressStep => Step == WelcomeStep.Address;

    public bool IsTokenStep => Step == WelcomeStep.Token;

    public bool IsTestStep => Step == WelcomeStep.Test;

    public string StepText => Step switch
    {
        WelcomeStep.Address => UeBar.App.Localization.Loc.Get("Welcome_Step1"),
        WelcomeStep.Token => UeBar.App.Localization.Loc.Get("Welcome_Step2"),
        WelcomeStep.Test => UeBar.App.Localization.Loc.Get("Welcome_Step3"),
        _ => "",
    };

    public string Intro => Step switch
    {
        WelcomeStep.Address => UeBar.App.Localization.Loc.F("Welcome_AddressIntro", ModuleName),
        WelcomeStep.Token => UeBar.App.Localization.Loc.Get("Welcome_TokenIntro"),
        WelcomeStep.Test => UeBar.App.Localization.Loc.Get("Welcome_TestIntro"),
        _ => "",
    };

    public string NextLabel => Step == WelcomeStep.Test ? UeBar.App.Localization.Loc.Get("Welcome_SaveAndNext") : UeBar.App.Localization.Loc.Get("Common_Next");

    public bool CanContinueAnyway => _flow.TestPassed == false;

    public bool IsBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>Done (true = skipped). The app closes the assistant.</summary>
    public event EventHandler<bool>? Finished;

    private bool HasAddress => AddressFields.Count == 0 || AddressFields.Any(f => !f.IsEmpty);

    private bool HasToken => TokenFields.All(f => !f.IsEmpty || _settings.HasSecret(Module.Id, f.Field.Key));

    private async Task NextAsync()
    {
        if (Step == WelcomeStep.Test)
        {
            await SaveAsync();
        }

        if (_flow.Next(HasAddress, HasToken))
        {
            if (Step == WelcomeStep.Test)
            {
                TestLines.Clear();
                Refresh();
                await TestAsync();
            }

            Refresh();
            CheckDone();
        }
    }

    private void CheckDone()
    {
        if (Step == WelcomeStep.Layout)
        {
            Finish(skipped: false);
        }
    }

    private Dictionary<string, string> CurrentValues()
    {
        var stored = _settings.GetModuleSettings(Module.Id);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in stored.Keys)
        {
            if (stored.GetString(key) is { } v)
            {
                values[key] = v;
            }
        }

        foreach (var f in AddressFields.Concat(TokenFields))
        {
            if (!f.IsEmpty)
            {
                values[f.Field.Key] = f.Value.Trim();
            }
            else if (!f.IsSecret)
            {
                values.Remove(f.Field.Key);
            }
        }

        return values;
    }

    private async Task TestAsync()
    {
        if (Module.Module is not IModuleSettingsTest)
        {
            _flow.TestFinished(true);
            Refresh();
            return;
        }

        IsBusy = true;
        TestLines.Clear();
        TestLines.Add(new WelcomeTestLine(UeBar.App.Localization.Loc.Get("Welcome_Testing"), true));
        try
        {
            var values = CurrentValues();
            var result = await _registry.InvokeAsync(
                Module.Id,
                "Test settings",
                (m, ct) => ((IModuleSettingsTest)m).TestSettingsAsync(new ModuleSettingValues(values), ct),
                TimeSpan.FromSeconds(25));
            TestLines.Clear();
            if (result is { Succeeded: true, Value: { } lines })
            {
                foreach (var line in lines)
                {
                    TestLines.Add(new WelcomeTestLine($"{line.Title}: {line.Detail}", line.Success));
                }

                _flow.TestFinished(lines.Any(l => l.Success));
            }
            else
            {
                TestLines.Add(new WelcomeTestLine(UeBar.App.Localization.Loc.F("Welcome_TestFailed", result.Outcome), false));
                _flow.TestFinished(false);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        var changes = new List<(string, string?, bool)>();
        foreach (var f in AddressFields)
        {
            changes.Add((f.Field.Key, f.IsEmpty ? null : f.Value.Trim(), false));
        }

        foreach (var f in TokenFields.Where(f => !f.IsEmpty))
        {
            changes.Add((f.Field.Key, f.Value.Trim(), true));
        }

        _settings.SetModuleValues(Module.Id, changes);
        _log.Info(Module.Id, "Welcome assistant: settings saved – module starts");
        if (_registry.Find(Module.Id) is { IsEnabled: true })
        {
            await _registry.RestartModuleAsync(Module.Id);
        }
    }

    private void Finish(bool skipped)
    {
        if (skipped)
        {
            _flow.Skip();
        }

        _log.Info(null, skipped ? "Welcome assistant skipped" : "Welcome assistant finished");
        Finished?.Invoke(this, skipped);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsAddressStep));
        OnPropertyChanged(nameof(IsTokenStep));
        OnPropertyChanged(nameof(IsTestStep));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(CanContinueAnyway));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        ContinueAnywayCommand.NotifyCanExecuteChanged();
    }
}
