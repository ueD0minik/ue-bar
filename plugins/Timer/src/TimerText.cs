// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace UeBar.Modules.Timer;

/// <summary>Texts of the timer plugin (own resources). The language comes from the module context.</summary>
internal static class TimerText
{
    /// <summary>Set from <see cref="IUiLanguageInfo"/> on start; null = current UI culture.</summary>
    public static CultureInfo? Culture { get; set; }

    public static TextCatalog T { get; } = TextCatalog.For(typeof(TimerText).Assembly, "UeBar.Modules.Timer", () => Culture);

    /// <summary>The text in English and German – the search finds things by both names.</summary>
    public static IEnumerable<string> BothLanguages(string key) =>
        [T.Get(key, CultureInfo.GetCultureInfo("en-US")), T.Get(key, CultureInfo.GetCultureInfo("de-DE"))];

    /// <summary>"4:32", "1:05:00" – remaining or elapsed time (rounded up for countdowns).</summary>
    public static string Clock(TimeSpan time, bool roundUp = false)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var seconds = roundUp ? (long)Math.Ceiling(time.TotalSeconds - 0.0005) : (long)Math.Floor(time.TotalSeconds);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>"10 min", "1 h 30 min", "45 s".</summary>
    public static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 60)
        {
            return T.Format("Timer_Seconds", (int)Math.Round(time.TotalSeconds));
        }

        var minutes = (int)Math.Round(time.TotalMinutes);
        if (Math.Abs(time.TotalMinutes - minutes) > 0.01)
        {
            return T.Format("Timer_MinutesSeconds", (int)time.TotalMinutes, time.Seconds);
        }

        return minutes < 60 ? T.Format("Timer_Minutes", minutes) : minutes % 60 == 0 ? T.Format("Timer_Hours", minutes / 60) : T.Format("Timer_HoursMinutes", minutes / 60, minutes % 60);
    }
}
