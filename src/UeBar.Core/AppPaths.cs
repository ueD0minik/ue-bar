// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core;

/// <summary>Storage locations under %APPDATA%\ueBar\ (SPEC 3).</summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        Root = rootDirectory;
    }

    /// <summary>Environment variable for a different data folder (tests, portable use).</summary>
    public const string DataDirVariable = "UEBAR_DATA_DIR";

    /// <summary>Default: %APPDATA%\ueBar – or the folder from <see cref="DataDirVariable"/>.</summary>
    public static AppPaths Default { get; } = new(
        IsCustom
            ? Path.GetFullPath(Environment.GetEnvironmentVariable(DataDirVariable)!)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ueBar"));

    /// <summary>Is a data folder other than %APPDATA%\ueBar in use?</summary>
    public static bool IsCustom => Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 };

    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LayoutFile => Path.Combine(Root, "layout.json");
    public string DeviceFile => Path.Combine(Root, "device.json");
    public string LayoutSyncFile => Path.Combine(Root, "layout.sync.json");
    public string Backups => Path.Combine(Root, "backups");

    /// <summary>Pins (P2-5): pins.json + images, this device only.</summary>
    public string Pins => Path.Combine(Root, "pins");

    /// <summary>Plugins the user added (one folder per plugin, SPEC 4.4).</summary>
    public string Plugins => Path.Combine(Root, "plugins");

    /// <summary>Which plugins the user allowed or refused.</summary>
    public string PluginTrustFile => Path.Combine(Root, "plugin-trust.json");
}
