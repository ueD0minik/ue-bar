// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.Core.Localization;

/// <summary>Values of the "language" setting (R1).</summary>
public static class UiLanguages
{
    /// <summary>Follow the Windows display language: German if Windows is German, otherwise English.</summary>
    public const string System = "system";

    public const string German = "de";

    public const string English = "en";

    public static IReadOnlyList<string> All { get; } = [System, German, English];

    /// <summary>
    /// Resolves a language setting to the culture used for texts: de-DE or en-US. Numbers, dates and
    /// times do not follow it – they use the Windows region (<see cref="CultureInfo.CurrentCulture"/>).
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture)
    {
        var language = setting?.Trim().ToLowerInvariant() switch
        {
            German => German,
            English => English,
            _ => systemUiCulture.TwoLetterISOLanguageName == German ? German : English,
        };

        return CultureInfo.GetCultureInfo(language == German ? "de-DE" : "en-US");
    }

    /// <summary>Normalizes a stored setting (unknown values count as "system").</summary>
    public static string Normalize(string? setting)
    {
        var value = setting?.Trim().ToLowerInvariant();
        return value is German or English ? value : System;
    }
}

/// <summary>
/// Current UI culture (texts only) with change notification (R1). WPF-free; the app sets it, modules read it
/// through <see cref="Abstractions.IUiLanguageInfo"/> on their context.
/// </summary>
public sealed class UiCultureSource
{
    private CultureInfo _current;

    public UiCultureSource(CultureInfo? initial = null)
    {
        _current = initial ?? UiLanguages.Resolve(UiLanguages.System, CultureInfo.CurrentUICulture);
    }

    /// <summary>Shared instance used by the running app.</summary>
    public static UiCultureSource Shared { get; } = new();

    public CultureInfo Current => Volatile.Read(ref _current);

    /// <summary>Raised after the culture changed (on the thread that called <see cref="Set"/>).</summary>
    public event EventHandler<CultureInfo>? Changed;

    /// <summary>Sets the culture; returns true if it changed.</summary>
    public bool Set(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var previous = Interlocked.Exchange(ref _current, culture);
        if (Equals(previous, culture))
        {
            return false;
        }

        Changed?.Invoke(this, culture);
        return true;
    }
}
