// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Localization;

/// <summary>UI texts of the core (<c>UeBar.Core/Resources/Strings*.resx</c>).</summary>
public static class CoreText
{
    public static TextCatalog T { get; } = TextCatalog.For(typeof(CoreText).Assembly, "UeBar.Core", () => UiCultureSource.Shared.Current);
}
