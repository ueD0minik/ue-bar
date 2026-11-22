// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Threading;
using UeBar.App.Infrastructure;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.App.Plugins;

/// <summary>
/// Hotkeys of plugins (P2-5, <see cref="SettingFieldKind.Hotkey"/>): while a plugin runs and handles them
/// (<see cref="IHotkeyHandler"/>), every hotkey field with a value is registered; a press goes to the plugin
/// through the guard. Stopping, switching off or a fault releases them. Generic – no plugin is known by name.
/// </summary>
internal sealed class PluginHotkeys(ModuleRegistry registry, SettingsService settings, GlobalHotkeys hotkeys, Dispatcher dispatcher, IUeLogger log)
{
    private readonly Dictionary<string, string> _bound = new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        registry.ModuleStatusChanged += (_, _) => dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public static string NameOf(string moduleId, string fieldKey) => $"plugin:{moduleId}:{fieldKey}";

    /// <summary>Current status of a plugin's hotkey field (settings page).</summary>
    public (HotkeyRegistration Result, string Message)? StatusOf(string moduleId, string fieldKey) =>
        hotkeys.Set.StatusOf(NameOf(moduleId, fieldKey));

    /// <summary>The hotkey fields of a module and their values (saved value or default).</summary>
    public IReadOnlyList<(SettingField Field, string Value)> FieldsOf(ModuleEntry module)
    {
        if (module.Module is not IModuleSettings)
        {
            return [];
        }

        var fields = registry.Invoke(module.Id, "Setting fields", m => ((IModuleSettings)m).SettingFields, reportFaults: false).Value ?? [];
        var values = settings.GetModuleSettings(module.Id);
        return [.. fields.Where(f => f.Kind == SettingFieldKind.Hotkey).Select(f => (f, values.GetString(f.Key) ?? f.DefaultValue ?? ""))];
    }

    /// <summary>(Re-)registers what should be active right now.</summary>
    public void Refresh()
    {
        var wanted = new Dictionary<string, (string ModuleId, string Key, string Text)>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in registry.Modules.Where(m => m.IsEnabled && m.IsAvailable && m.Module is IHotkeyHandler))
        {
            foreach (var (field, value) in FieldsOf(module))
            {
                wanted[NameOf(module.Id, field.Key)] = (module.Id, field.Key, value.Trim());
            }
        }

        foreach (var name in _bound.Keys.Where(n => !wanted.ContainsKey(n)).ToList())
        {
            hotkeys.Set.Remove(name);
            _bound.Remove(name);
        }

        foreach (var (name, (moduleId, key, text)) in wanted)
        {
            if (_bound.TryGetValue(name, out var current) && current == text)
            {
                continue;
            }

            hotkeys.Bind(name, text, () => Press(moduleId, key));
            _bound[name] = text;
        }
    }

    /// <summary>
    /// A new hotkey recorded on the settings page: taken (and saved) only if it can be registered; empty = off.
    /// </summary>
    public (HotkeyRegistration Result, string Message) Change(string moduleId, string fieldKey, string text)
    {
        var name = NameOf(moduleId, fieldKey);
        (HotkeyRegistration Result, string Message) result;
        if (text.Length == 0)
        {
            hotkeys.Set.Remove(name);
            result = hotkeys.Bind(name, "", () => { });
        }
        else
        {
            result = hotkeys.Set.TryChange(name, text, () => Press(moduleId, fieldKey));
            if (result.Result != HotkeyRegistration.Registered)
            {
                return result;
            }
        }

        var saved = text.Length == 0 ? "" : Hotkey.TryParse(text)?.Text ?? text;
        settings.SetModuleValues(moduleId, [(fieldKey, saved, false)]);
        _bound[name] = saved;
        log.Info(moduleId, $"Hotkey {fieldKey}: {(saved.Length == 0 ? "off" : saved)}");
        if (registry.Find(moduleId) is not { IsAvailable: true } || registry.Find(moduleId)?.Module is not IHotkeyHandler)
        {
            hotkeys.Set.Remove(name); // saved for later; the plugin does not run (or cannot take it) right now
            _bound.Remove(name);
        }

        return result;
    }

    private void Press(string moduleId, string fieldKey)
    {
        log.Info(moduleId, $"Hotkey {fieldKey} pressed");
        _ = registry.InvokeAsync(moduleId, $"Hotkey {fieldKey}", async (m, ct) =>
        {
            if (m is not IHotkeyHandler handler)
            {
                return false;
            }

            await handler.OnHotkeyAsync(fieldKey, ct).ConfigureAwait(false);
            return true;
        });
    }
}
