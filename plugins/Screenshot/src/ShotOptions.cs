// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;
using static UeBar.Modules.Screenshot.ShotText;

namespace UeBar.Modules.Screenshot;

/// <summary>What happens with a screenshot.</summary>
internal enum ShotAction
{
    /// <summary>Show the small bar (pin · copy · save · recognize text).</summary>
    Ask,
    Pin,
    Copy,
    Save,
    Ocr,

    /// <summary>Windows "Save as" dialog – free folder and name (P2-5-Fix 1).</summary>
    SaveAs,
}

/// <summary>What happens with recognized text.</summary>
internal enum OcrTarget
{
    Copy,
    Pin,
    Both,
}

/// <summary>The plugin's settings as values (read once per change, thread-safe as an immutable record).</summary>
internal sealed record ShotOptions(
    string Hotkey,
    ScreenAreaMode Mode,
    ShotAction After,
    string Folder,
    OcrTarget OcrResult,
    string? OcrLanguage,
    OcrTextFormat OcrFormat = OcrTextFormat.Flow)
{
    public const string HotkeyKey = "hotkey";
    public const string ModeKey = "mode";
    public const string AfterKey = "after";
    public const string FolderKey = "folder";
    public const string OcrResultKey = "ocrResult";
    public const string OcrLanguageKey = "ocrLanguage";
    public const string OcrFormatKey = "ocrFormat";
    public const string DefaultHotkey = "Win+Alt+S";

    public static ShotOptions Default { get; } = new(DefaultHotkey, ScreenAreaMode.Region, ShotAction.Ask, ShotFiles.DefaultFolder, OcrTarget.Copy, null);

    public static ShotOptions From(ModuleSettingValues values) => new(
        values.GetString(HotkeyKey) ?? DefaultHotkey,
        ParseMode(values.GetString(ModeKey)) ?? ScreenAreaMode.Region,
        ParseAction(values.GetString(AfterKey)) ?? ShotAction.Ask,
        values.GetString(FolderKey) is { Length: > 0 } folder ? Environment.ExpandEnvironmentVariables(folder.Trim()) : ShotFiles.DefaultFolder,
        values.GetString(OcrResultKey)?.ToLowerInvariant() switch
        {
            "pin" => OcrTarget.Pin,
            "both" => OcrTarget.Both,
            _ => OcrTarget.Copy,
        },
        values.GetString(OcrLanguageKey) is { Length: > 0 } tag && tag != "auto" ? tag : null,
        string.Equals(values.GetString(OcrFormatKey), "lines", StringComparison.OrdinalIgnoreCase) ? OcrTextFormat.Lines : OcrTextFormat.Flow);

    public static ScreenAreaMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "region" or "area" or "bereich" => ScreenAreaMode.Region,
        "window" or "fenster" => ScreenAreaMode.Window,
        "screen" or "monitor" or "bildschirm" or "full" or "ganz" => ScreenAreaMode.Screen,
        _ => null,
    };

    public static ShotAction? ParseAction(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "ask" => ShotAction.Ask,
        "pin" => ShotAction.Pin,
        "copy" => ShotAction.Copy,
        "save" => ShotAction.Save,
        "saveas" or "save-as" => ShotAction.SaveAs,
        "ocr" or "text" => ShotAction.Ocr,
        _ => null,
    };

    public static string Id(ScreenAreaMode mode) => mode switch
    {
        ScreenAreaMode.Window => "window",
        ScreenAreaMode.Screen => "screen",
        _ => "region",
    };

    public static string Id(ShotAction action) => action.ToString().ToLowerInvariant();

    /// <summary>The fields of the settings page; OCR languages as found on this PC.</summary>
    public static IReadOnlyList<SettingField> Fields(IReadOnlyList<(string Tag, string Name)> languages) =>
    [
        new(HotkeyKey, T["Shot_SettingHotkey"], SettingFieldKind.Hotkey, DefaultHotkey, T["Shot_SettingHotkey_Hint"]),
        new(ModeKey, T["Shot_SettingMode"], SettingFieldKind.Choice, "region", T["Shot_SettingMode_Hint"],
        [
            new("region", T["Shot_ModeRegion"]),
            new("window", T["Shot_ModeWindow"]),
            new("screen", T["Shot_ModeScreen"]),
        ]),
        new(AfterKey, T["Shot_SettingAfter"], SettingFieldKind.Choice, "ask", T["Shot_SettingAfter_Hint"],
        [
            new("ask", T["Shot_AfterAsk"]),
            new("pin", T["Shot_ActionPin"]),
            new("copy", T["Shot_ActionCopy"]),
            new("save", T["Shot_ActionSave"]),
            new("saveas", T["Shot_ActionSaveAs"]),
            new("ocr", T["Shot_ActionOcr"]),
        ]),
        new(FolderKey, T["Shot_SettingFolder"], SettingFieldKind.Folder, null, T.Format("Shot_SettingFolder_Hint", ShotFiles.DefaultFolder))
        {
            Placeholder = ShotFiles.DefaultFolder,
        },
        new(OcrResultKey, T["Shot_SettingOcrResult"], SettingFieldKind.Choice, "copy", null,
        [
            new("copy", T["Shot_OcrCopy"]),
            new("pin", T["Shot_OcrPin"]),
            new("both", T["Shot_OcrBoth"]),
        ]),
        new(OcrFormatKey, T["Shot_SettingOcrFormat"], SettingFieldKind.Choice, "flow", T["Shot_SettingOcrFormat_Hint"],
        [
            new("flow", T["Shot_OcrFormatFlow"]),
            new("lines", T["Shot_OcrFormatLines"]),
        ]),
        new(OcrLanguageKey, T["Shot_SettingOcrLanguage"], SettingFieldKind.Choice, "auto",
            languages.Count == 0 ? T["Shot_OcrNoLanguage"] : T["Shot_SettingOcrLanguage_Hint"],
            [new("auto", T["Shot_OcrAuto"]), .. languages.Select(l => new SettingChoice(l.Tag, l.Name))]),
    ];
}

/// <summary>Where screenshots are saved and how they are named.</summary>
internal static class ShotFiles
{
    /// <summary>Pictures\Screenshots\ue Bar of the user.</summary>
    public static string DefaultFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "ue Bar");

    /// <summary>"Screenshot 2026-11-20 14-03-05.png" – sorts by time, valid on every file system.</summary>
    public static string FileName(DateTimeOffset localTime) =>
        string.Create(CultureInfo.InvariantCulture, $"Screenshot {localTime:yyyy-MM-dd HH-mm-ss}.png");

    /// <summary>A free path in the folder: with " (2)", " (3)" … when the name is taken.</summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        var path = Path.Combine(folder, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; exists(path) && n < 1000; n++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}){extension}"));
        }

        return path;
    }

    /// <summary>Writes the image to the path the user chose ("Save as", may replace – the dialog asked).</summary>
    public static string? SaveTo(string path, byte[] png)
    {
        try
        {
            File.WriteAllBytes(path, png);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>Writes the image; returns the path or the reason it failed.</summary>
    public static (string? Path, string? Error) Save(string folder, byte[] png, DateTimeOffset localTime)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = UniquePath(folder, FileName(localTime), File.Exists);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(png);
            }

            return (path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, ex.Message);
        }
    }
}
