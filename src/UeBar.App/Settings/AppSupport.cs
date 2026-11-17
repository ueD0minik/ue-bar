// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using UeBar.Sdk;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.App.Settings;

/// <summary>Version and build (M7: one place – <c>Directory.Build.props</c>).</summary>
internal static class AppInfo
{
    /// <summary>"1.0.0" (without the git suffix).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

#if DEBUG
    public const string BuildVariant = "Debug";
#else
    public const string BuildVariant = "Release";
#endif

    /// <summary>Author and copyright holder (GPL-3.0-or-later).</summary>
    public const string Copyright = "Copyright (C) 2026 Dominik Poschinger";

    /// <summary>SPDX identifier of the license.</summary>
    public const string License = "GPL-3.0-or-later";

    /// <summary>Public source code repository.</summary>
    public const string RepositoryUrl = "https://github.com/ueD0minik/ue-bar";

    /// <summary>Full license text.</summary>
    public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    /// <summary>Portable single exe (self-contained) instead of an installation?</summary>
    public static bool IsPortable { get; } = !System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "ueBar.dll"));
}

/// <summary>Autostart through <c>HKCU\…\Run</c> (SPEC 3). The installer sets the same value.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ueBar";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    /// <summary>Does the entry point to another .exe (e.g. an old portable version)?</summary>
    public static bool PointsElsewhere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled, IUeLogger log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            log.Info(null, $"Autostart {(enabled ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            log.Error(null, "Autostart could not be changed", ex);
            throw;
        }
    }
}

/// <summary>Appearance at runtime (M7): accent color.</summary>
internal static class Appearance
{
    /// <summary>
    /// Sets <c>AccentColor</c>/<c>AccentBrush</c> at app level. Everything using DynamicResource changes color
    /// right away; tiles get the color on the next build.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        var (r, g, b) = AccentColors.Rgb(hex);
        var color = Color.FromRgb(r, g, b);
        var resources = Application.Current.Resources;
        resources["AccentColor"] = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources["AccentBrush"] = brush;
    }
}

/// <summary>Read/change the section "general" in settings.json; reports changes.</summary>
internal sealed class GeneralSettingsStore(SettingsService settings)
{
    private GeneralSettings _current = settings.GetSection<GeneralSettings>(GeneralSettings.SectionName);

    public event EventHandler? Changed;

    public GeneralSettings Current => _current.Clone();

    public void Update(Action<GeneralSettings> change)
    {
        var next = _current.Clone();
        change(next);
        _current = next;
        settings.SetSection(GeneralSettings.SectionName, next);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
