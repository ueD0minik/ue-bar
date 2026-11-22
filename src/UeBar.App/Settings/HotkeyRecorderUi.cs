// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Logging;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>What a recording field changes (P2-5: the quick search, the pins, a plugin's hotkey field).</summary>
/// <param name="Current">The saved hotkey text ("" = none).</param>
/// <param name="Status">Whether it is registered right now, with a readable text.</param>
/// <param name="Change">Registers and saves a new text ("" = off) – only if it works; returns the result.</param>
/// <param name="DefaultText">What Delete/Backspace sets ("" = off).</param>
internal sealed record HotkeyBinding(
    Func<string> Current,
    Func<(HotkeyRegistration Result, string Message)> Status,
    Func<string, (HotkeyRegistration Result, string Message)> Change,
    string DefaultText)
{
    /// <summary>Offer "No hotkey" (not for the quick search).</summary>
    public bool AllowOff { get; init; }
}

/// <summary>The recording field for global hotkeys (M7-Fix 1, shared since P2-5).</summary>
internal static class HotkeyRecorderUi
{
    private static string DefaultText(HotkeyBinding binding) =>
        Hotkey.TryParse(binding.DefaultText)?.DisplayText ?? (binding.DefaultText.Length > 0 ? binding.DefaultText : Loc.Get("Hotkey_None"));

    /// <summary>
    /// Recording field for the hotkey (M7-Fix 1). A button instead of a focusable border:
    /// the border did get the focus on click, but the surrounding ScrollViewer took it back in the
    /// same click – recording ended right away, no key arrived. The button handles the
    /// click itself. Alt comes as <see cref="Key.System"/> (→ <c>SystemKey</c>), Win is counted by itself,
    /// the own global hotkey is unregistered during recording. Next to it "Manual" as a fallback.
    /// </summary>
    public static FrameworkElement Create(Window owner, HotkeyBinding binding, Action<bool> suspend, IUeLogger log)
    {
        var panel = new StackPanel();
        var status = Hint("");
        var text = new TextBlock { FontSize = 14, Width = 270, TextTrimming = TextTrimming.CharacterEllipsis };
        var box = new Button
        {
            Style = StyleOf("BarButton"),
            Content = text,
            Height = 38,
            Width = 300,
            Padding = new Thickness(14, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Res("ControlBrush"),
            BorderThickness = new Thickness(0),
            Focusable = true,
            ToolTip = Loc.F("Hotkey_Tip", DefaultText(binding)),
        };
        KeyboardNavigation.SetIsTabStop(box, true);
        var recording = false;

        void ShowCurrent()
        {
            var current = Hotkey.TryParse(binding.Current());
            text.Text = current?.DisplayText ?? (binding.Current() is { Length: > 0 } raw ? raw : Loc.Get("Hotkey_None"));
            text.Foreground = Res("TextBrush");
            var (result, message) = binding.Status();
            status.Text = result == HotkeyRegistration.Registered ? Loc.Get("Hotkey_Active") : binding.Current().Length == 0 ? "" : message;
            status.Foreground = Res("SubTextBrush");
        }

        void StopRecording(string reason)
        {
            if (!recording)
            {
                return;
            }

            recording = false;
            box.Background = Res("ControlBrush");
            suspend(false); // register the old hotkeys again
            log.Info(null, $"Hotkey recording ended ({reason})");
            ShowCurrent();
        }

        void Apply(Hotkey? hotkey, string how)
        {
            recording = false;
            box.Background = Res("ControlBrush");
            var (result, _) = binding.Change(hotkey?.Text ?? "");
            suspend(false);
            if (hotkey is null)
            {
                log.Info(null, $"Hotkey recording: {how} → off");
                ShowCurrent();
                return;
            }

            log.Info(null, $"Hotkey recording: {how} {hotkey.Text} → {result}");
            ShowCurrent();
            if (result != HotkeyRegistration.Registered)
            {
                status.Text = result == HotkeyRegistration.InUse
                    ? Loc.F("Hotkey_InUse", hotkey.DisplayText)
                    : Loc.F("Hotkey_Invalid", hotkey.DisplayText);
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60));
            }
            else
            {
                status.Text = Loc.F("Hotkey_ActiveWith", hotkey.DisplayText);
            }
        }

        box.Click += (_, _) =>
        {
            if (recording)
            {
                return;
            }

            recording = true;
            box.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            text.Text = Loc.Get("Hotkey_PressNow");
            text.Foreground = Res("OnAccentTextBrush");
            status.Text = Loc.Get("Hotkey_RecordingHint");
            status.Foreground = Res("SubTextBrush");
            suspend(true); // otherwise Windows catches the old hotkey before it arrives here
            box.Focus();
            Keyboard.Focus(box);
            log.Info(null, $"Hotkey recording started (focus in field: {box.IsKeyboardFocused}, window active: {owner.IsActive})");
        };
        box.GotKeyboardFocus += (_, _) => log.Debug(null, "Hotkey recording: field has the keyboard focus");
        box.LostKeyboardFocus += (_, e) =>
        {
            log.Info(null, $"Hotkey recording: focus lost to {e.NewFocus?.GetType().Name ?? "nobody"}");
            StopRecording("focus lost");
        };
        owner.Deactivated += (_, _) => StopRecording("window no longer active");

        box.PreviewKeyDown += (_, e) =>
        {
            if (!recording)
            {
                return;
            }

            e.Handled = true; // also Alt/F10 (otherwise window menu) and Space/Enter (otherwise "click")
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = HotkeyModifiers.None;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            {
                modifiers |= HotkeyModifiers.Win; // Keyboard.Modifiers does not report Win reliably
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                modifiers |= HotkeyModifiers.Control;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                modifiers |= HotkeyModifiers.Alt;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                modifiers |= HotkeyModifiers.Shift;
            }

            var vk = KeyInterop.VirtualKeyFromKey(key);
            var result = HotkeyRecording.Evaluate(vk, modifiers);
            log.Info(null, $"Hotkey recording: key {e.Key}, SystemKey {e.SystemKey}, modifiers {Keyboard.Modifiers}, Win {modifiers.HasFlag(HotkeyModifiers.Win)} → {result.Outcome} {result.Hotkey?.Text}");
            switch (result.Outcome)
            {
                case HotkeyRecordOutcome.Cancel:
                    StopRecording("Esc");
                    break;
                case HotkeyRecordOutcome.ResetToDefault:
                    Apply(Hotkey.TryParse(binding.DefaultText), "default");
                    break;
                case HotkeyRecordOutcome.Complete:
                    text.Text = result.Display;
                    Apply(result.Hotkey!, "new");
                    break;
                default:
                    text.Text = result.Display;
                    break;
            }
        };
        box.PreviewKeyUp += (_, e) =>
        {
            if (recording)
            {
                e.Handled = true; // releasing Alt must not open the window menu
            }
        };

        ShowCurrent();
        panel.Children.Add(box);
        status.Margin = new Thickness(2, 6, 0, 0);
        panel.Children.Add(status);
        if (binding.AllowOff)
        {
            var off = Button(Loc.Get("Hotkey_TurnOff"), () => Apply(null, "button"), icon: "");
            off.Margin = new Thickness(0, 8, 0, 0);
            off.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(off);
        }

        panel.Children.Add(ManualHotkey(binding, hotkey => Apply(hotkey, "manual")));
        return panel;
    }

    /// <summary>
    /// Fallback (M7-Fix 1): some combinations Windows does not let through to the app at all (e.g.
    /// Win+L, or another program already has them). Then pick checkboxes + key.
    /// </summary>
    private static FrameworkElement ManualHotkey(HotkeyBinding binding, Action<Hotkey> apply)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var content = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var toggle = Button(Loc.Get("Hotkey_Manual"), () =>
            content.Visibility = content.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, icon: "");
        outer.Children.Add(toggle);

        var current = Hotkey.TryParse(binding.Current());
        var mods = current?.Modifiers ?? HotkeyModifiers.None;
        var vk = current?.VirtualKey ?? 0x20;
        var row = new WrapPanel();
        foreach (var (label, flag) in new[] { ("Win", HotkeyModifiers.Win), (Loc.KeyName("Ctrl"), HotkeyModifiers.Control), ("Alt", HotkeyModifiers.Alt), (Loc.KeyName("Shift"), HotkeyModifiers.Shift) })
        {
            var box = new CheckBox
            {
                Style = StyleOf("OneUiSwitch"),
                Content = new TextBlock { Text = label, FontSize = 13 },
                IsChecked = mods.HasFlag(flag),
                Margin = new Thickness(0, 0, 18, 8),
                Focusable = true,
            };
            box.Checked += (_, _) => mods |= flag;
            box.Unchecked += (_, _) => mods &= ~flag;
            row.Children.Add(box);
        }

        content.Children.Add(row);
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        var keys = HotkeyRecording.SelectableKeys;
        keyRow.Children.Add(Combo(keys.Select(k => (k.Label, k.VirtualKey)), vk, v => vk = v, width: 180));
        var result = Hint("");
        var take = Button(Loc.Get("Common_Apply"), () =>
        {
            if (Hotkey.FromKeys(mods, vk) is { } hotkey)
            {
                apply(hotkey);
                result.Text = "";
            }
            else
            {
                result.Text = Loc.F("Hotkey_NeedModifier", Loc.KeyName("Ctrl"), Loc.KeyName("Shift"));
            }
        }, accent: true);
        take.Margin = new Thickness(10, 0, 0, 0);
        keyRow.Children.Add(take);
        content.Children.Add(keyRow);
        result.Margin = new Thickness(2, 6, 0, 0);
        content.Children.Add(result);
        outer.Children.Add(content);
        return outer;
    }
}
