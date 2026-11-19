// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UeBar.Sdk;
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
                // P2-4: a module that can apply settings live keeps running (no flicker), others restart.
                await _registry.ApplyChangedSettingsAsync(_module.Id);
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
        _shown.Clear();
        foreach (var field in _fields)
        {
            var row = BuildField(field, current);
            _shown.Add((field, row));
            rows.Add(row);
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

        var card = Card([.. rows]);
        UpdateShown();
        return card;
    }

    private readonly List<(SettingField Field, UIElement Row)> _shown = [];

    /// <summary>Current value of a field of the page: entered, saved or its default (for <see cref="SettingField.ShowWhen"/>).</summary>
    private string? ValueOf(string key) =>
        _pending.TryGetValue(key, out var p) ? p.Value
        : _settings.GetModuleSettings(_module.Id).GetString(key) ?? _fields.FirstOrDefault(f => f.Key == key)?.DefaultValue;

    private void UpdateShown() => ApplyShown(_shown, ValueOf);

    /// <summary>
    /// P2-4: fields with <see cref="SettingField.ShowWhen"/> come and go with the field they depend on; the
    /// separator line of the card goes with its row.
    /// </summary>
    private static void ApplyShown(IEnumerable<(SettingField Field, UIElement Row)> rows, Func<string, string?> valueOf)
    {
        foreach (var (field, row) in rows)
        {
            row.Visibility = field.IsShown(valueOf) ? Visibility.Visible : Visibility.Collapsed;
        }

        var panel = (rows.Select(r => r.Row).FirstOrDefault() as FrameworkElement)?.Parent as Panel;
        if (panel is null)
        {
            return;
        }

        var anyBefore = false;
        UIElement? separator = null;
        foreach (UIElement child in panel.Children)
        {
            if (child is Border { Height: 1 } line)
            {
                separator = line;
                continue;
            }

            var visible = child.Visibility == Visibility.Visible;
            if (separator is not null)
            {
                separator.Visibility = visible && anyBefore ? Visibility.Visible : Visibility.Collapsed;
            }

            anyBefore |= visible;
            separator = null;
        }
    }

    public Button TestButton() => Button(Loc.Get("Fields_TestConnection"), async () => await TestAsync(), icon: "");

    public StackPanel TestResults => _testResults;

    private FrameworkElement BuildField(SettingField field, ModuleSettingValues current) =>
        BuildField(field, current.GetString(field.Key) ?? field.DefaultValue, v => Changed(field, v));

    private FrameworkElement BuildField(SettingField field, string? value, Action<string?> changed)
    {
        switch (field.Kind)
        {
            case SettingFieldKind.Button:
            {
                // P2-3: a button the module runs (e.g. "Open location access"); stores nothing.
                var result = Hint("");
                async Task RunAsync()
                {
                    var run = await _registry.InvokeAsync(_module.Id, $"Settings button {field.Key}", (m, ct) =>
                        m is IModuleSettingsActions actions ? actions.RunSettingsActionAsync(field.Key, ct) : Task.FromResult<string?>(null));
                    result.Text = run.Succeeded ? run.Value ?? "" : Loc.Get("Modules_NotResponding");
                }

                var content = new StackPanel();
                content.Children.Add(Button(field.ButtonText ?? field.Label, () => _ = RunAsync()));
                result.Margin = new Thickness(2, 6, 0, 0);
                content.Children.Add(result);
                return Row(field.Label, field.Description, content, controlBelow: true);
            }

            case SettingFieldKind.Toggle:
                return SwitchRow(field.Label, field.Description, bool.TryParse(value, out var b) && b, v => changed(v ? "true" : "false"));

            case SettingFieldKind.Choice when field.Choices is { Count: > 0 } choices:
            {
                var items = choices.Select(c => (c.Label, (string?)c.Value)).ToList();
                if (value is not null && choices.All(c => c.Value != value))
                {
                    items.Insert(0, (Loc.F("Fields_NotFound", value), value));
                }

                return Row(field.Label, field.Description, Combo(items, value, v => changed(v), width: 320), controlBelow: true);
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

                        changed(string.Join(",", choices.Select(c => c.Value).Where(selected.Contains)));
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
                        changed(text);
                    }
                }, width: 420, password: true), controlBelow: true);
            }

            case SettingFieldKind.File or SettingFieldKind.Folder:
                return Row(field.Label, field.Description, PathField(field, value, changed), controlBelow: true);

            case SettingFieldKind.List:
                return Row(field.Label, field.Description, ListField(field, value, changed), controlBelow: true);

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

                    changed(text.Length == 0 ? null : text);
                }, width: 420), controlBelow: true);
        }
    }

    /// <summary>P2-4: path as text + "Browse…" with the Windows dialog (plugins show no windows of their own).</summary>
    private FrameworkElement PathField(SettingField field, string? value, Action<string?> changed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var text = TextField(value, field.Placeholder, t => changed(t.Length == 0 ? null : t), width: 330);
        panel.Children.Add(text);
        var browse = Button(Loc.Get("Fields_Browse"), () =>
        {
            var start = string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);
            string? chosen;
            if (field.Kind == SettingFieldKind.Folder)
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = field.Label };
                if (start is not null && System.IO.Directory.Exists(start))
                {
                    dialog.InitialDirectory = start;
                }

                chosen = dialog.ShowDialog(_owner) == true ? dialog.FolderName : null;
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = field.Label, Filter = field.FileFilter ?? Loc.Get("Fields_AllFiles") + "|*.*" };
                if (start is not null && System.IO.Path.GetDirectoryName(start) is { } dir && System.IO.Directory.Exists(dir))
                {
                    dialog.InitialDirectory = dir;
                }

                chosen = dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
            }

            if (chosen is null)
            {
                return;
            }

            value = chosen;
            if (FindTextBox(text) is { } box)
            {
                box.Text = chosen;
            }

            changed(chosen);
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        panel.Children.Add(browse);
        return panel;
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        if (root is TextBox box)
        {
            return box;
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindTextBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return root is Panel panel ? panel.Children.OfType<UIElement>().Select(FindTextBox).FirstOrDefault(b => b is not null) : null;
    }

    /// <summary>
    /// P2-4: a list of entries, each with the item fields; "Add" and "Remove". Every change saves the whole
    /// list (JSON); new entries get a stable "id".
    /// </summary>
    private FrameworkElement ListField(SettingField field, string? value, Action<string?> changed)
    {
        var items = ModuleSettingValues.ParseList(value).Select(i => new Dictionary<string, string>(i, StringComparer.Ordinal)).ToList();
        var fields = (field.ItemFields ?? []).Where(f => f.Kind is not (SettingFieldKind.List or SettingFieldKind.Password or SettingFieldKind.Button)).ToList();
        var host = new StackPanel();

        void Save() => changed(items.Count == 0 ? null : ModuleSettingValues.FormatList(items));

        void Render()
        {
            host.Children.Clear();
            foreach (var item in items)
            {
                var rows = new List<UIElement>();
                var shown = new List<(SettingField, UIElement)>();
                string? ItemValue(string key) => item.TryGetValue(key, out var value) ? value : fields.FirstOrDefault(f => f.Key == key)?.DefaultValue;
                foreach (var itemField in fields)
                {
                    var current = item.TryGetValue(itemField.Key, out var v) ? v : itemField.DefaultValue;
                    var row = BuildField(itemField, current, nv =>
                    {
                        if (nv is null)
                        {
                            item.Remove(itemField.Key);
                        }
                        else
                        {
                            item[itemField.Key] = nv;
                        }

                        Save();
                        ApplyShown(shown, ItemValue);
                    });
                    shown.Add((itemField, row));
                    rows.Add(row);
                }

                var remove = Button(Loc.Get("Fields_ListRemove"), () =>
                {
                    items.Remove(item);
                    Save();
                    Render();
                }, icon: "\uE74D");
                rows.Add(remove);
                var card = Card([.. rows]);
                card.Margin = new Thickness(0, 0, 0, 10);
                host.Children.Add(card);
                ApplyShown(shown, ItemValue);
            }

            host.Children.Add(Button(field.AddText ?? Loc.Get("Fields_ListAdd"), () =>
            {
                var item = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = Guid.NewGuid().ToString("N")[..8] };
                foreach (var itemField in fields.Where(f => f.DefaultValue is not null))
                {
                    item[itemField.Key] = itemField.DefaultValue!;
                }

                items.Add(item);
                Save();
                Render();
            }, icon: "\uE710"));
        }

        Render();
        return host;
    }

    private void Changed(SettingField field, string? value)
    {
        _pending[field.Key] = (value, field.IsSecret);
        UpdateShown();
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
