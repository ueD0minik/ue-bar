// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace UeBar.Modules.Dummy;

/// <summary>Texts of the test module (own resources, R1). The language comes from the module context.</summary>
internal static class DummyText
{
    /// <summary>Set from <see cref="UeBar.Sdk.IUiLanguageInfo"/> on start; null = current UI culture.</summary>
    public static CultureInfo? Culture { get; set; }

    public static TextCatalog T { get; } = TextCatalog.For(typeof(DummyText).Assembly, "UeBar.Modules.Dummy", () => Culture);
}
