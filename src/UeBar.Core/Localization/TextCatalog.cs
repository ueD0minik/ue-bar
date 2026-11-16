// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Resources;

namespace UeBar.Core.Localization;

/// <summary>
/// Looks up UI texts in a project's resources (<c>Resources/Strings.resx</c> = English,
/// <c>Strings.de.resx</c> = German) for the current culture. Every project (core, app and each
/// module) has its own catalog.
/// </summary>
public sealed class TextCatalog
{
    private readonly ResourceManager _resources;
    private readonly Func<CultureInfo?> _culture;

    public TextCatalog(ResourceManager resources, Func<CultureInfo?> culture)
    {
        _resources = resources;
        _culture = culture;
    }

    /// <summary>Catalog for <c>&lt;RootNamespace&gt;.Resources.Strings</c> of the given assembly.</summary>
    public static TextCatalog For(System.Reflection.Assembly assembly, string rootNamespace, Func<CultureInfo?> culture) =>
        new(new ResourceManager(rootNamespace + ".Resources.Strings", assembly), culture);

    public CultureInfo Culture => _culture() ?? CultureInfo.CurrentUICulture;

    /// <summary>Text for <paramref name="key"/>; the key itself if it is missing (tests check that none is).</summary>
    public string this[string key] => Get(key, Culture);

    public string Get(string key, CultureInfo culture) => _resources.GetString(key, culture) ?? key;

    /// <summary>
    /// Text in the UI language, arguments (<c>{0}</c>, <c>{1}</c> …) formatted with the Windows region
    /// (<see cref="CultureInfo.CurrentCulture"/>).
    /// </summary>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, Culture), args);
}
