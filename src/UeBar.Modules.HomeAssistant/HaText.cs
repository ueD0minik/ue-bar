// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Localization;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Texts of the Home Assistant module (own resources, R1). The language comes from the module
/// context (<see cref="UeBar.Core.Abstractions.IUiLanguageInfo"/>); before the start the current UI culture applies.
/// </summary>
internal static class HaText
{
    /// <summary>Set by the module on start and on every language switch.</summary>
    public static CultureInfo? Culture { get; set; }

    /// <summary>Culture for numbers, dates and times: the Windows region, independent of the UI language.</summary>
    public static CultureInfo FormatCulture => CultureInfo.CurrentCulture;

    /// <summary>Two-letter language for Home Assistant (Assist pipeline), "de" or "en".</summary>
    public static string Language => (Culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "de" ? "de" : "en";

    public static TextCatalog T { get; } = TextCatalog.For(typeof(HaText).Assembly, "UeBar.Modules.HomeAssistant", () => Culture);
}
