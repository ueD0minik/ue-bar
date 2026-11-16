// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Localization;

namespace UeBar.Bar.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language switching";
}

/// <summary>R1: texts of the bar logic follow the UI language (WPF-free).</summary>
[Collection(LanguageCollection.Name)]
public sealed class LanguageTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Hotkey_display_switches_between_german_and_english()
    {
        var hotkey = Hotkey.TryParse("Win+Ctrl+Shift+Space")!;
        try
        {
            UiCultureSource.Shared.Set(English);
            Assert.Equal("Win + Ctrl + Shift + Space", hotkey.DisplayText);
            Assert.Equal("Ctrl + Alt + …", HotkeyRecording.Evaluate(0x12, HotkeyModifiers.Control).Display);
            Assert.Contains(HotkeyRecording.SelectableKeys, k => k.Label == "Page Up");
            Assert.Equal("Combine with Win, Ctrl, Alt or Shift", HotkeyRecording.Evaluate('K', HotkeyModifiers.None).Display);
            Assert.Equal("Blue", AccentColors.Suggestions[0].Name);

            UiCultureSource.Shared.Set(German);
            Assert.Equal("Win + Strg + Umschalt + Leertaste", hotkey.DisplayText);
            Assert.Contains(HotkeyRecording.SelectableKeys, k => k.Label == "Bild auf");
            Assert.Equal("Blau", AccentColors.Suggestions[0].Name);
        }
        finally
        {
            UiCultureSource.Shared.Set(German);
        }
    }

    [Fact]
    public void German_key_names_still_parse_and_show_in_the_current_language()
    {
        try
        {
            UiCultureSource.Shared.Set(English);
            var hotkey = Hotkey.TryParse("Strg+Umschalt+Leertaste")!;
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hotkey.Modifiers);
            Assert.Equal("Ctrl + Shift + Space", hotkey.DisplayText);
        }
        finally
        {
            UiCultureSource.Shared.Set(German);
        }
    }

    [Fact]
    public void Registration_messages_are_localized()
    {
        try
        {
            UiCultureSource.Shared.Set(English);
            var (_, _, inUse) = HotkeyBinder.Register("Ctrl+Alt+J", (_, _) => false);
            Assert.Equal("Hotkey Ctrl + Alt + J is already in use – quick search only through the tray menu.", inUse);

            UiCultureSource.Shared.Set(German);
            var (_, _, ok) = HotkeyBinder.Register("Ctrl+Alt+J", (_, _) => true);
            Assert.Equal("Schnellsuche: Strg + Alt + J", ok);
        }
        finally
        {
            UiCultureSource.Shared.Set(German);
        }
    }

    [Fact]
    public void Language_setting_is_stored_with_the_general_settings()
    {
        var settings = new GeneralSettings { Language = "en" };
        var clone = settings.Clone();
        Assert.Equal("en", clone.Language);
        Assert.Null(new GeneralSettings().Language); // default = system language
    }
}
