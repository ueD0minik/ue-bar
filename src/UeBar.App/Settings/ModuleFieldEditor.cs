// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>
/// Fields of a module from <see cref="IModuleSettings"/> (M7, replaces the dialog from M3).
/// Immediate mode (settings window): every change is saved, the module restarts shortly
/// afterwards. Collect mode (welcome assistant): values stay in memory until <see cref="SaveAsync"/>.
/// Secrets are never shown: leave empty = keep the saved one.
/// </summary>
internal sealed class ModuleFieldEditor
{
    private readonly ModuleRegistry _registry;
    private readonly SettingsService _settings;
    private readonly IUeLogger _log;
    private readonly ModuleEntry _module;
    private readonly IReadOnlyList<SettingField> _fields;
    private readonly Window? _owner;
    private readonly bool _immediate;
    private readonly Dictionary<string, (string? Value, bool IsSecret)> _pending = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _restart;
    private readonly TextBlock _saved = Hint("");
    private readonly StackPanel _testResults = new() { Margin = new Thickness(0, 6, 0, 0) };

    public ModuleFieldEditor(ModuleRegistry registry, SettingsService settings, IUeLogger log, ModuleEntry module, IReadOnlyList<SettingField> fields, Window? owner, bool immediate = true)
    {
        _registry = registry;
        _settings = settings;
        _log = log;
        _module = module;
        _fields = fields;
        _owner = owner;
        _immediate = immediate;
        _restart = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _restart.Tick += async (_, _) =>
        {
            _restart.Stop();
            if (_registry.Find(_module.Id) is { IsEnabled: true })
            {
                _log.Info(_module.Id, "Settings changed – module restarts");
                await _registry.RestartModuleAsync(_module.Id);
            }

            _saved.Text = "Gespeichert.";
        };
    }

    public IReadOnlyList<SettingField> Fields => _fields;

    /// <summary>Card with all fields + "Test connection" (if the module can do it).</summary>
    public Border Build(bool withTest = true)
    {
        var current = _settings.GetModuleSettings(_module.Id);
        var rows = new List<UIElement>();
        foreach (var field in _fields)
        {
            rows.Add(BuildField(field, current));
        }

        if (withTest && _module.Module is IModuleSettingsTest)
        {
            var test = new StackPanel();
            test.Children.Add(TestButton());
            test.Children.Add(_testResults);
            rows.Add(test);
        }

        if (_immediate)
        {
            rows.Add(_saved);
        }

        return Card([.. rows]);
    }

    public Button TestButton() => Button(Loc.Get("Fields_TestConnection"), async () => await TestAsync(), icon: "");

    public StackPanel TestResults => _testResults;

    private FrameworkElement BuildField(SettingField field, ModuleSettingValues current)
    {
        var value = current.GetString(field.Key) ?? field.DefaultValue;
        switch (field.Kind)
        {
            case SettingFieldKind.Toggle:
                return SwitchRow(field.Label, field.Description, bool.TryParse(value, out var b) && b, v => Changed(field, v ? "true" : "false"));

            case SettingFieldKind.Choice when field.Choices is { Count: > 0 } choices:
            {
                var items = choices.Select(c => (c.Label, (string?)c.Value)).ToList();
                if (value is not null && choices.All(c => c.Value != value))
                {
                    items.Insert(0, (Loc.F("Fields_NotFound", value), value));
                }

                return Row(field.Label, field.Description, Combo(items, value, v => Changed(field, v), width: 320), controlBelow: true);
            }

            case SettingFieldKind.MultiChoice when field.Choices is { Count: > 0 } choices:
            {
                var selected = new HashSet<string>((value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
                var list = new StackPanel();
                foreach (var choice in choices)
                {
                    var id = choice.Value;
                    var row = SwitchRow(choice.Label, null, selected.Contains(id), on =>
                    {
                        if (on)
                        {
                            selected.Add(id);
                        }
                        else
                        {
                            selected.Remove(id);
                        }

                        Changed(field, string.Join(",", choices.Select(c => c.Value).Where(selected.Contains)));
                    });
                    row.Margin = new Thickness(0, 2, 0, 2);
                    list.Children.Add(row);
                }

                return Row(field.Label, field.Description, list, controlBelow: true);
            }

            case SettingFieldKind.Password:
            {
                var stored = _settings.HasSecret(_module.Id, field.Key);
                var hint = (stored ? Loc.Get("Fields_SecretStored") + " " : "") + (field.Description ?? "");
                return Row(field.Label, hint, TextField(null, stored ? "••••••••" : field.Placeholder, text =>
                {
                    if (text.Length > 0)
                    {
                        Changed(field, text);
                    }
                }, width: 420, password: true), controlBelow: true);
            }

            default:
                return Row(field.Label, field.Description, TextField(value, field.Placeholder, text =>
                {
                    if (field.Kind == SettingFieldKind.Number && text.Length > 0)
                    {
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
                        {
                            MessageBox.Show(_owner!, Loc.F("Fields_NotANumber", text), "ue Bar", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }

                        text = d.ToString(CultureInfo.InvariantCulture);
                    }

                    Changed(field, text.Length == 0 ? null : text);
                }, width: 420), controlBelow: true);
        }
    }

    private void Changed(SettingField field, string? value)
    {
        _pending[field.Key] = (value, field.IsSecret);
        if (!_immediate)
        {
            return;
        }

        try
        {
            _settings.SetModuleValues(_module.Id, [(field.Key, value, field.IsSecret)]);
            _saved.Text = Loc.Get("Fields_Saved");
            _restart.Stop();
            _restart.Start();
        }
        catch (Exception ex)
        {
            _log.Error(_module.Id, "Setting could not be saved", ex);
            _saved.Text = Loc.F("Fields_NotSaved", ex.Message);
        }
    }

    /// <summary>Save the collected values (collect mode) and restart the module.</summary>
    public async Task SaveAsync()
    {
        if (_pending.Count > 0)
        {
            _settings.SetModuleValues(_module.Id, _pending.Select(p => (p.Key, p.Value.Value, p.Value.IsSecret)));
            _pending.Clear();
        }

        if (_registry.Find(_module.Id) is { IsEnabled: true })
        {
            await _registry.RestartModuleAsync(_module.Id);
        }
    }

    /// <summary>Is there a value (collected or saved) for the field?</summary>
    public bool HasValue(string key) =>
        (_pending.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p.Value))
        || !string.IsNullOrWhiteSpace(_settings.GetModuleSettings(_module.Id).GetString(key));

    /// <summary>Test with the current input (+ saved secret if left empty).</summary>
    public async Task<bool> TestAsync()
    {
        _testResults.Children.Clear();
        _testResults.Children.Add(ResultLine(Loc.Get("Welcome_Testing"), null));
        var stored = _settings.GetModuleSettings(_module.Id);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in stored.Keys)
        {
            if (stored.GetString(key) is { } v)
            {
                values[key] = v;
            }
        }

        foreach (var (key, (value, _)) in _pending)
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        var result = await _registry.InvokeAsync(
            _module.Id,
            "Test settings",
            (m, ct) => ((IModuleSettingsTest)m).TestSettingsAsync(new ModuleSettingValues(values), ct),
            TimeSpan.FromSeconds(25));

        _testResults.Children.Clear();
        if (result is { Succeeded: true, Value: { } lines })
        {
            foreach (var line in lines)
            {
                _testResults.Children.Add(ResultLine($"{line.Title}: {line.Detail}", line.Success));
            }

            return lines.Any(l => l.Success); // at least one way works
        }

        _testResults.Children.Add(ResultLine(Loc.F("Welcome_TestFailed", result.Outcome), false));
        return false;
    }
}
