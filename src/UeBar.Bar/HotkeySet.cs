// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// Several global hotkeys under names (P2-5: show/hide all pins, hotkeys of plugins), WPF-free – the
/// Win32 registration comes from outside. A hotkey that is in use or invalid gets a status with a
/// readable text, the others keep working; while recording in the settings all of them pause.
/// </summary>
public sealed class HotkeySet
{
    private readonly Func<int, int, int, bool> _register;
    private readonly Action<int> _unregister;
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;
    private bool _suspended;

    /// <param name="register">(id, modifiers incl. MOD_NOREPEAT, virtual key) → registered?</param>
    /// <param name="unregister">id → release.</param>
    /// <param name="firstId">First Win32 ID (the bar's own hotkey uses 1).</param>
    public HotkeySet(Func<int, int, int, bool> register, Action<int> unregister, int firstId = 100)
    {
        _register = register;
        _unregister = unregister;
        _nextId = firstId;
    }

    public IReadOnlyCollection<string> Names => [.. _bindings.Keys];

    /// <summary>
    /// Sets the hotkey of a name (empty text = none). If the new one cannot be registered the name has no
    /// active hotkey – the status says why.
    /// </summary>
    public (HotkeyRegistration Result, string Message) Set(string name, string? text, Action pressed)
    {
        Remove(name);
        var binding = new Binding(_nextId++, text?.Trim() ?? "", pressed);
        _bindings[name] = binding;
        Activate(binding);
        return binding.Status;
    }

    /// <summary>
    /// Changes the hotkey of a name only if the new one works (recording in the settings): otherwise the
    /// old one stays registered. Returns the result for the new text.
    /// </summary>
    public (HotkeyRegistration Result, string Message) TryChange(string name, string text, Action pressed)
    {
        _bindings.TryGetValue(name, out var old);
        if (old is not null)
        {
            Release(old);
        }

        // Also while suspended (recording): registering is the only way to know whether it is free.
        var binding = new Binding(_nextId++, text.Trim(), pressed);
        Register(binding);
        if (binding.Status.Result == HotkeyRegistration.Registered)
        {
            _bindings[name] = binding;
            if (_suspended)
            {
                Release(binding);
            }

            return binding.Status;
        }

        if (old is not null)
        {
            Activate(old);
        }

        return binding.Status;
    }

    public void Remove(string name)
    {
        if (_bindings.Remove(name, out var binding))
        {
            Release(binding);
        }
    }

    /// <summary>Status of a name; null = unknown name.</summary>
    public (HotkeyRegistration Result, string Message)? StatusOf(string name) =>
        _bindings.TryGetValue(name, out var b) ? b.Status : null;

    /// <summary>Active text of a name ("" = none).</summary>
    public string? TextOf(string name) => _bindings.TryGetValue(name, out var b) ? b.Text : null;

    /// <summary>WM_HOTKEY arrived: runs the action of the ID; false = not ours.</summary>
    public bool Press(int id)
    {
        var binding = _bindings.Values.FirstOrDefault(b => b.Id == id && b.IsRegistered);
        if (binding is null)
        {
            return false;
        }

        binding.Pressed();
        return true;
    }

    /// <summary>Pause all (recording a hotkey) or register them again.</summary>
    public void Suspend(bool suspend)
    {
        if (suspend == _suspended)
        {
            return;
        }

        _suspended = suspend;
        foreach (var binding in _bindings.Values)
        {
            if (suspend)
            {
                Release(binding);
            }
            else
            {
                Register(binding);
            }
        }
    }

    public void Clear()
    {
        foreach (var binding in _bindings.Values)
        {
            Release(binding);
        }

        _bindings.Clear();
    }

    private void Activate(Binding binding)
    {
        if (_suspended)
        {
            binding.Status = Validate(binding.Text);
            return;
        }

        Register(binding);
    }

    private void Register(Binding binding)
    {
        if (binding.Text.Length == 0)
        {
            binding.Status = (HotkeyRegistration.Invalid, BarText.T["Hotkey_Off"]);
            return;
        }

        var (result, hotkey, message) = HotkeyBinder.Register(binding.Text, (mods, vk) => _register(binding.Id, mods, vk));
        binding.Status = (result, result switch
        {
            // The binder's texts speak of the quick search – these hotkeys are for other things.
            HotkeyRegistration.Registered => BarText.T.Format("Hotkey_Active", hotkey!.DisplayText),
            HotkeyRegistration.InUse => BarText.T.Format("Hotkey_Taken", hotkey!.DisplayText),
            _ => message,
        });
        binding.IsRegistered = result == HotkeyRegistration.Registered;
    }

    private void Release(Binding binding)
    {
        if (binding.IsRegistered)
        {
            _unregister(binding.Id);
            binding.IsRegistered = false;
        }
    }

    private static (HotkeyRegistration, string) Validate(string text) =>
        text.Length == 0 ? (HotkeyRegistration.Invalid, BarText.T["Hotkey_Off"])
        : Hotkey.TryParse(text) is { } hotkey ? (HotkeyRegistration.Registered, BarText.T.Format("Hotkey_Active", hotkey.DisplayText))
        : (HotkeyRegistration.Invalid, BarText.T.Format("Hotkey_Invalid", text));

    private sealed class Binding(int id, string text, Action pressed)
    {
        public int Id { get; } = id;

        public string Text { get; } = text;

        public Action Pressed { get; } = pressed;

        public bool IsRegistered { get; set; }

        public (HotkeyRegistration Result, string Message) Status { get; set; } = (HotkeyRegistration.Invalid, "");
    }
}
