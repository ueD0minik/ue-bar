// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.Bar;

/// <summary>One-time changes of settings.json between versions (run once at start, after loading).</summary>
public static class SettingsMigration
{
    /// <summary>Version of settings.json this app writes.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// 1 → 2 (1.3.2, P2-4): the timer plugin of 1.3.1 had its own switches "also with Do not disturb" and
    /// "also in fullscreen". They became the bar settings for important notifications; a switched-off value
    /// is taken over, then the old keys are removed. Known exception: the only place where the app names a
    /// plugin's setting – there is no other owner for a setting that moved from a plugin into the bar.
    /// 2 → 3 (1.7.0, P3-1): "hide from screen captures" is on by default now; an existing installation gets it
    /// switched on once – afterwards the user's choice counts.
    /// </summary>
    public static void Run(SettingsService settings, IUeLogger log)
    {
        var version = settings.Current.Version;
        if (version >= CurrentVersion)
        {
            return;
        }

        if (!settings.FileExisted)
        {
            settings.Current.Version = CurrentVersion;
            return;
        }

        var notes = new List<string>();
        if (version < 2)
        {
            var timer = settings.GetModuleSettings(LegacyTimer.ModuleId);
            var bar = settings.GetSection<BarSettings>("bar");
            var moved = new List<string>();
            foreach (var (key, apply) in new (string Key, Action<bool> Apply)[]
            {
                (LegacyTimer.DndKey, v => bar.ImportantDuringDoNotDisturb = v),
                (LegacyTimer.FullscreenKey, v => bar.ImportantInFullscreen = v),
            })
            {
                if (timer.GetString(key) is { } value)
                {
                    apply(!bool.TryParse(value, out var b) || b);
                    settings.SetModuleValue(LegacyTimer.ModuleId, key, null, isSecret: false);
                    moved.Add($"{key}={value}");
                }
            }

            settings.SetSection("bar", bar);
            if (moved.Count > 0)
            {
                notes.Add($"timer switches moved to the bar settings ({string.Join(", ", moved)})");
            }
        }

        if (version < 3)
        {
            var general = settings.GetSection<GeneralSettings>(GeneralSettings.SectionName);
            if (!general.HideFromCapture)
            {
                general.HideFromCapture = true;
                settings.SetSection(GeneralSettings.SectionName, general);
                notes.Add("hide from screen captures switched on (new default)");
            }
        }

        settings.Current.Version = CurrentVersion;
        settings.SetSection("bar", settings.GetSection<BarSettings>("bar")); // writes the new version
        log.Info(null, notes.Count == 0
            ? $"settings.json updated to version {CurrentVersion}"
            : $"settings.json updated to version {CurrentVersion}: {string.Join("; ", notes)}");
    }

    private static class LegacyTimer
    {
        public const string ModuleId = "timer";
        public const string DndKey = "ignoreDoNotDisturb";
        public const string FullscreenKey = "showInFullscreen";
    }
}
