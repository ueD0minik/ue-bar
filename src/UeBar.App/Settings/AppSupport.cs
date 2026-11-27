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

    /// <summary>P2-6-Fix 2: started by Windows at logon – the app waits a moment (see <see cref="Delay"/>).</summary>
    public const string Argument = "--autostart";

    /// <summary>Right after logging in Windows is busy for a while; starting a little later is faster and calmer.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    private static string OldCommand => $"\"{Environment.ProcessPath}\"";

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
            return key?.GetValue(ValueName) is string value && !string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value.Trim(), OldCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>An entry of 1.5.1 or older (this exe without the argument) gets the argument – nothing else is touched.</summary>
    public static void Upgrade(IUeLogger log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && string.Equals(value.Trim(), OldCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command);
                log.Info(null, $"Autostart entry now starts with {Argument} (delayed start after logging in)");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            log.Warn(null, $"Autostart entry could not be updated: {ex.Message}");
        }
    }

    /// <summary>Started at logon: wait until the process is <see cref="Delay"/> old (Windows is still busy logging in).</summary>
    public static async Task WaitAfterLogonAsync(string[] args, IUeLogger log)
    {
        if (!args.Contains(Argument, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        TimeSpan age;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            age = DateTime.Now - process.StartTime;
        }
        catch (InvalidOperationException)
        {
            age = TimeSpan.Zero;
        }

        var wait = Delay - age;
        if (wait > TimeSpan.Zero)
        {
            log.Info(null, $"Started with Windows: waiting {wait.TotalSeconds:0.0} s until logging in has settled");
            await Task.Delay(wait);
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
