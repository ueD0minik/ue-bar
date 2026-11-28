// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.Bar;

/// <summary>General settings (section "general" in settings.json, M7).</summary>
public sealed class GeneralSettings
{
    public const string SectionName = "general";

    public const int MinColumns = 3;

    public const int MaxColumns = 6;

    public const int DefaultColumns = 4;

    /// <summary>Device name (notifications, device layout). Empty = Windows computer name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Accent color as #RRGGBB. Empty = One UI blue.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Columns in the tile grid (3–6).</summary>
    public int Columns { get; set; } = DefaultColumns;

    /// <summary>
    /// Layout storage location: empty = automatic (first module with a store), "local" = local only,
    /// otherwise a module ID.
    /// </summary>
    public string? LayoutStore { get; set; }

    /// <summary>Welcome assistant done or skipped.</summary>
    public bool WelcomeDone { get; set; }

    /// <summary>UI language (R1): "system" (default; German if Windows is German, otherwise English), "de" or "en".</summary>
    public string? Language { get; set; }

    /// <summary>
    /// P2-7: every window of the app (bar, pins, popups, dialogs …) is left out of screen captures. Default on since 1.7.0
    /// (P3-1; existing installations are switched on once by <see cref="SettingsMigration"/>).
    /// </summary>
    public bool HideFromCapture { get; set; } = true;

    public const string DefaultSelectionHotkey = "Win+Alt+C";

    /// <summary>P3-1: hotkey "Send marked text to …" (copies the marking of the window in front, then the list of content actions). Empty = none.</summary>
    public string SelectionHotkey { get; set; } = DefaultSelectionHotkey;

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveColumns => Math.Clamp(Columns, MinColumns, MaxColumns);

    public string EffectiveDeviceName(string machineName) =>
        string.IsNullOrWhiteSpace(DeviceName) ? machineName : DeviceName.Trim();

    public GeneralSettings Clone() => (GeneralSettings)MemberwiseClone();
}

/// <summary>Accent colors (M7): suggestions and an own hex value.</summary>
public static class AccentColors
{
    public const string Default = "#3E91FF";

    /// <summary>Suggestions in One UI style (all readable with white text). Names in the current UI language.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Suggestions =>
    [
        (BarText.T["Color_Blue"], Default),
        (BarText.T["Color_Violet"], "#8B6CFF"),
        (BarText.T["Color_Turquoise"], "#14A8B8"),
        (BarText.T["Color_Green"], "#2FA463"),
        (BarText.T["Color_Orange"], "#F08A24"),
        (BarText.T["Color_Red"], "#E5484D"),
        (BarText.T["Color_Pink"], "#E0559C"),
        (BarText.T["Color_Grey"], "#6E7681"),
    ];

    /// <summary>"#3e91ff", "3E91FF", "#39F" → "#3E91FF"; otherwise null.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? "#" + hex.ToUpperInvariant()
            : null;
    }

    /// <summary>RGB values of a (normalized) color; invalid → default.</summary>
    public static (byte R, byte G, byte B) Rgb(string? text)
    {
        var hex = (Normalize(text) ?? Default)[1..];
        var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>
/// OLED protection (SPEC 5.5, M7) – pure logic: if the bar is pinned and there was no interaction for
/// <see cref="DimAfter"/>, it is dimmed. Optionally the panel moves by 1–2 px every
/// <see cref="ShiftInterval"/> (pixel shift) while it is pinned. Time comes from outside.
/// </summary>
public sealed class OledGuard
{
    /// <summary>Small path around the origin (at most 2 px), then from the start.</summary>
    private static readonly PixelPoint[] ShiftPath = [new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-2, 0), new(-1, -1), new(0, -1), new(2, 1)];

    private DateTimeOffset _lastInteraction;
    private DateTimeOffset _shiftSince;
    private int _shiftIndex;
    private bool _pinned;

    public OledGuard(DateTimeOffset now)
    {
        _lastInteraction = now;
        _shiftSince = now;
    }

    public static readonly TimeSpan ShiftInterval = TimeSpan.FromMinutes(3);

    /// <summary>0 = dimming off.</summary>
    public TimeSpan DimAfter { get; set; } = TimeSpan.FromMinutes(5);

    public bool PixelShift { get; set; } = true;

    public bool IsDimmed { get; private set; }

    /// <summary>Current offset in physical pixels (0,0 without pixel shift or when not pinned).</summary>
    public PixelPoint Offset => _pinned && PixelShift ? ShiftPath[_shiftIndex] : new PixelPoint(0, 0);

    /// <summary>Mouse over the panel, click, key … – lifts the dimming immediately.</summary>
    public bool Interaction(DateTimeOffset now)
    {
        _lastInteraction = now;
        if (!IsDimmed)
        {
            return false;
        }

        IsDimmed = false;
        return true;
    }

    /// <summary>Pinned/unpinned. Pinning restarts the waiting time.</summary>
    public void SetPinned(bool pinned, DateTimeOffset now)
    {
        if (_pinned == pinned)
        {
            return;
        }

        _pinned = pinned;
        _lastInteraction = now;
        _shiftSince = now;
        _shiftIndex = 0;
        IsDimmed = false;
    }

    /// <summary>Advance time. Returns true if dimming or offset changed.</summary>
    public bool Update(DateTimeOffset now)
    {
        var changed = false;
        var dim = _pinned && DimAfter > TimeSpan.Zero && now - _lastInteraction >= DimAfter;
        if (dim != IsDimmed)
        {
            IsDimmed = dim;
            changed = true;
        }

        if (_pinned && PixelShift && now - _shiftSince >= ShiftInterval)
        {
            _shiftIndex = (_shiftIndex + 1) % ShiftPath.Length;
            _shiftSince = now;
            changed = true;
        }

        return changed;
    }
}

/// <summary>When the welcome assistant appears (M7).</summary>
public static class WelcomeRules
{
    /// <summary>
    /// Only at the first start without settings.json – or as long as it was never finished/skipped
    /// and a module still needs to be set up. Existing installations (file present,
    /// everything set up) never see it.
    /// </summary>
    public static bool ShouldShow(bool settingsFileExisted, bool welcomeDone, bool anyModuleNeedsSetup) =>
        !welcomeDone && (!settingsFileExisted || anyModuleNeedsSetup);
}

/// <summary>Steps of the welcome assistant (M7).</summary>
public enum WelcomeStep
{
    /// <summary>Enter address(es).</summary>
    Address,

    /// <summary>Enter access key (token).</summary>
    Token,

    /// <summary>Test the connection.</summary>
    Test,

    /// <summary>First-start layout (M5b) – shows the panel itself afterwards.</summary>
    Layout,

    /// <summary>Done or skipped.</summary>
    Done,
}

/// <summary>
/// Flow of the welcome assistant (M7), WPF-free: address → token → test → layout. Next only
/// with input; after a failed test "Continue anyway" is possible. "Skip" at any time.
/// </summary>
public sealed class WelcomeFlow
{
    public WelcomeStep Step { get; private set; } = WelcomeStep.Address;

    public bool Skipped { get; private set; }

    /// <summary>Result of the last test (null = not tested yet).</summary>
    public bool? TestPassed { get; private set; }

    public bool CanGoBack => Step is WelcomeStep.Token or WelcomeStep.Test;

    public bool CanGoNext(bool hasAddress, bool hasToken) => Step switch
    {
        WelcomeStep.Address => hasAddress,
        WelcomeStep.Token => hasToken,
        WelcomeStep.Test => TestPassed == true,
        _ => false,
    };

    public bool Next(bool hasAddress, bool hasToken)
    {
        if (!CanGoNext(hasAddress, hasToken))
        {
            return false;
        }

        Step++;
        if (Step == WelcomeStep.Test)
        {
            TestPassed = null;
        }

        return true;
    }

    public void Back()
    {
        if (CanGoBack)
        {
            Step--;
        }
    }

    public void TestFinished(bool passed) => TestPassed = Step == WelcomeStep.Test ? passed : TestPassed;

    /// <summary>Test failed, but the user wants to continue (e.g. server off right now).</summary>
    public void ContinueAnyway()
    {
        if (Step == WelcomeStep.Test && TestPassed == false)
        {
            Step = WelcomeStep.Layout;
        }
    }

    /// <summary>Layout chosen or none needed.</summary>
    public void LayoutDone()
    {
        if (Step == WelcomeStep.Layout)
        {
            Step = WelcomeStep.Done;
        }
    }

    public void Skip()
    {
        Skipped = true;
        Step = WelcomeStep.Done;
    }
}
