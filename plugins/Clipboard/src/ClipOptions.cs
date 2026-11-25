// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;
using static UeBar.Modules.Clipboard.ClipText;

namespace UeBar.Modules.Clipboard;

/// <summary>The plugin's settings as values (immutable – read once per change).</summary>
internal sealed record ClipOptions(
    bool TakeOverWinV,
    string Hotkey,
    ClipLimits Limits,
    bool Images,
    long MaxImageBytes,
    IReadOnlyList<string> ExcludedApps,
    bool ShareContents)
{
    public const string WinVKey = "takeOverWinV";
    public const string HotkeyKey = "hotkey";
    public const string HookInfoKey = "hookInfo";
    public const string EntriesKey = "maxEntries";
    public const string DaysKey = "maxDays";
    public const string ImagesKey = "images";
    public const string ImageMbKey = "maxImageMb";
    public const string TotalMbKey = "maxTotalMb";
    public const string ExcludedKey = "excludedApps";
    public const string ShareKey = "shareContents";
    public const string PauseKey = "pause";
    public const string ClearKey = "clear";
    public const string ClearAllKey = "clearAll";
    public const string FolderKey = "folder";
    public const string DefaultHotkey = "Win+Alt+V";
    public const string WinV = "Win+V";

    /// <summary>Password managers by program name (as far as they are known; most also mark their copies).</summary>
    public static IReadOnlyList<string> DefaultExcluded { get; } =
        ["KeePass", "KeePassXC", "Bitwarden", "1Password", "Dashlane", "Enpass", "NordPass", "RoboForm", "LastPass", "Keeper", "ProtonPass"];

    public static ClipOptions Default { get; } = new(true, DefaultHotkey, ClipLimits.Default, true, 10L * 1024 * 1024, DefaultExcluded, false);

    private const long Mb = 1024 * 1024;

    public static ClipOptions From(ModuleSettingValues values)
    {
        var excluded = values.GetString(ExcludedKey) is null
            ? DefaultExcluded
            : (IReadOnlyList<string>)[.. values.GetList(ExcludedKey).Select(e => e.GetValueOrDefault("app")?.Trim()).OfType<string>().Where(a => a.Length > 0)];
        return new ClipOptions(
            values.GetBool(WinVKey, true),
            values.GetString(HotkeyKey) ?? DefaultHotkey,
            new ClipLimits(
                (int)Math.Clamp(values.GetNumber(EntriesKey, 100), 1, 1000),
                TimeSpan.FromDays(Math.Clamp(values.GetNumber(DaysKey, 7), 1, 365)),
                (long)(Math.Clamp(values.GetNumber(TotalMbKey, 200), 10, 5000) * Mb)),
            values.GetBool(ImagesKey, true),
            (long)(Math.Clamp(values.GetNumber(ImageMbKey, 10), 1, 100) * Mb),
            excluded,
            values.GetBool(ShareKey, false));
    }

    /// <summary>What the core reads for us – the rest of the clipboard is never read.</summary>
    public ClipboardWatchOptions WatchOptions => new()
    {
        Images = Images,
        MaxImageBytes = MaxImageBytes,
        MaxTextLength = 1_000_000,
        MaxRichTextLength = 512 * 1024,
        ThumbnailSize = 160,
    };

    /// <summary>Copied in an excluded app? Program name exactly, or the app's display name containing an entry.</summary>
    public bool IsExcluded(ClipboardSourceApp? source)
    {
        if (source is null || ExcludedApps.Count == 0)
        {
            return false;
        }

        foreach (var app in ExcludedApps)
        {
            var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            if (string.Equals(source.ProcessName, name, StringComparison.OrdinalIgnoreCase)
                || source.DisplayName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The fields of the settings page (with the current state of the hotkey, the pause and the store).</summary>
    public static IReadOnlyList<SettingField> Fields(string hookText, bool paused, int count, long bytes, string folder) =>
    [
        new(WinVKey, T["Clip_SettingWinV"], SettingFieldKind.Toggle, "true", T["Clip_SettingWinV_Hint"]),
        new(HookInfoKey, "", SettingFieldKind.Info, null, hookText),
        new(HotkeyKey, T["Clip_SettingHotkey"], SettingFieldKind.Hotkey, DefaultHotkey, T["Clip_SettingHotkey_Hint"]),
        new(EntriesKey, T["Clip_SettingEntries"], SettingFieldKind.Number, "100", T["Clip_SettingEntries_Hint"], Min: 1, Max: 1000),
        new(DaysKey, T["Clip_SettingDays"], SettingFieldKind.Number, "7", T["Clip_SettingDays_Hint"], Min: 1, Max: 365),
        new(ImagesKey, T["Clip_SettingImages"], SettingFieldKind.Toggle, "true"),
        new(ImageMbKey, T["Clip_SettingImageMb"], SettingFieldKind.Number, "10", null, Min: 1, Max: 100) { ShowWhen = $"{ImagesKey}=true" },
        new(TotalMbKey, T["Clip_SettingTotalMb"], SettingFieldKind.Number, "200", T["Clip_SettingTotalMb_Hint"], Min: 10, Max: 5000),
        new(ExcludedKey, T["Clip_SettingExcluded"], SettingFieldKind.List, DefaultExcludedValue, T["Clip_SettingExcluded_Hint"])
        {
            ItemFields = [new("app", T["Clip_SettingExcludedApp"], SettingFieldKind.Text) { Placeholder = "KeePass" }],
            AddText = T["Clip_SettingExcludedAdd"],
        },
        new(ShareKey, T["Clip_SettingShare"], SettingFieldKind.Toggle, "false", T["Clip_SettingShare_Hint"]),
        new(PauseKey, T["Clip_SettingPause"], SettingFieldKind.Button, null, paused ? $"{T["Clip_SettingPausedNow"]} {T["Clip_SettingPause_Hint"]}" : T["Clip_SettingPause_Hint"])
        {
            ButtonText = paused ? T["Clip_ButtonResume"] : T["Clip_ButtonPause"],
        },
        new(ClearKey, T["Clip_SettingClear"], SettingFieldKind.Button, null, T["Clip_SettingClear_Hint"]) { ButtonText = T["Clip_ButtonClear"] },
        new(ClearAllKey, T["Clip_SettingClearAll"], SettingFieldKind.Button, null, T["Clip_SettingClearAll_Hint"]) { ButtonText = T["Clip_ButtonClearAll"] },
        new(FolderKey, T.Format("Clip_SettingStored", count, Megabytes(bytes)), SettingFieldKind.Button, null, T.Format("Clip_SettingStored_Hint", folder))
        {
            ButtonText = T["Clip_ButtonFolder"],
        },
    ];

    public static string DefaultExcludedValue { get; } =
        ModuleSettingValues.FormatList(DefaultExcluded.Select((app, i) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["id"] = $"default-{i}",
            ["app"] = app,
        }));

    /// <summary>"3,4 MB" in the UI language.</summary>
    public static string Megabytes(long bytes) =>
        bytes < 100 * 1024 ? string.Format(Culture ?? CultureInfo.CurrentUICulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0))
        : string.Format(Culture ?? CultureInfo.CurrentUICulture, "{0:0.#} MB", bytes / (double)Mb);
}
