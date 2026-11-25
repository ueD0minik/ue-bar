// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace UeBar.Modules.Clipboard;

internal static class ClipText
{
    /// <summary>Set from <see cref="IUiLanguageInfo"/> on start; null = current UI culture.</summary>
    public static CultureInfo? Culture { get; set; }

    public static TextCatalog T { get; } = TextCatalog.For(typeof(ClipText).Assembly, "UeBar.Modules.Clipboard", () => Culture);

    /// <summary>The text in English and German – the search finds things by both names.</summary>
    public static IEnumerable<string> BothLanguages(string key) =>
        [T.Get(key, CultureInfo.GetCultureInfo("en-US")), T.Get(key, CultureInfo.GetCultureInfo("de-DE"))];
}
