// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using UeBar.App.Localization;
using UeBar.Bar;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>Settings → Pins (P2-5): dimming (OLED), pixel shift, fullscreen, hotkey, font size.</summary>
internal sealed partial class SettingsWindow
{
    private void BuildPins(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Pins")));
        if (_host.Pins is not { } pins)
        {
            page.Children.Add(Hint(Loc.Get("Pins_NotAvailable")));
            return;
        }

        page.Children.Add(Hint(Loc.Get("Pins_SettingsIntro"), top: 0));
        var o = pins.Options;
        int[] seconds = [0, 15, 30, 60, 120, 300, 600];
        string SecondsText(int s) => s == 0 ? Loc.Get("Oled_Never") : s < 60 ? Loc.F("Pins_Seconds", s) : s == 60 ? Loc.Get("Oled_OneMinute") : Loc.F("Oled_Minutes", s / 60);
        var dimChoices = seconds.Contains(o.DimAfterSeconds) ? seconds : [.. seconds.Append(o.DimAfterSeconds).Order()];
        page.Children.Add(Card(
            Row(Loc.Get("Pins_DimAfter"), Loc.Get("Pins_DimAfter_Hint"),
                Combo(dimChoices.Select(s => (SecondsText(s), s)), o.DimAfterSeconds, s => pins.UpdateOptions(p => p.DimAfterSeconds = s), width: 160)),
            Row(Loc.Get("Pins_DimLevel"), Loc.Get("Pins_DimLevel_Hint"),
                Slider(10, 80, 5, Math.Round(o.EffectiveDimLevel * 100), v => string.Create(CultureInfo.CurrentCulture, $"{v:0} %"), v => pins.UpdateOptions(p => p.DimLevel = v / 100))),
            SwitchRow(Loc.Get("Pins_PixelShift"), Loc.Get("Pins_PixelShift_Hint"), o.PixelShift, v => pins.UpdateOptions(p => p.PixelShift = v)),
            SwitchRow(Loc.Get("Pins_HideFullscreen"), Loc.Get("Pins_HideFullscreen_Hint"), o.HideInFullscreen, v => pins.UpdateOptions(p => p.HideInFullscreen = v))));

        var binding = new HotkeyBinding(() => pins.Options.Hotkey, () => pins.HotkeyStatus, pins.ChangeHotkey, PinSettings.DefaultHotkey) { AllowOff = true };
        page.Children.Add(Card(
            Row(Loc.Get("Pins_Hotkey"), Loc.F("Pins_Hotkey_Hint", Hotkey.TryParse(PinSettings.DefaultHotkey)?.DisplayText ?? PinSettings.DefaultHotkey),
                HotkeyRecorderUi.Create(this, binding, SuspendAllHotkeys, _host.Log), controlBelow: true)));

        page.Children.Add(Card(
            Row(Loc.Get("Pins_FontSize"), Loc.Get("Pins_FontSize_Hint"),
                Slider(10, 32, 1, pins.FontSizeOf(new UeBar.Core.Pins.PinRecord()), v => string.Create(CultureInfo.CurrentCulture, $"{v:0} px"), v => pins.UpdateOptions(p => p.FontSize = v))),
            Row(StoredText(pins.Manager), Loc.Get("Pins_Folder_Hint"),
                Button(Loc.Get("Pins_OpenFolder"), () => OpenFolder(pins.Manager.Directory), icon: "\uE838"))));
    }

    /// <summary>"Stored pins: 3 (1.2 MB)" - what lies in the pins folder right now (P2-5-Fix 1).</summary>
    private static string StoredText(UeBar.Core.Pins.PinManager manager)
    {
        var (count, bytes) = manager.StorageUse();
        return Loc.F("Pins_Stored", count, SizeText(bytes));
    }

    private static string SizeText(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0 / 1024.0:0.0} MB"),
    };
}
