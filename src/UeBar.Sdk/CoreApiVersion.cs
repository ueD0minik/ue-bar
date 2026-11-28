// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

/// <summary>
/// Version of the contract between ue Bar and its plugins (SPEC 4.8). A plugin states the version
/// it needs in <c>plugin.json</c> (<c>sdkVersion</c>, "major.minor").
/// <list type="bullet">
/// <item><see cref="Major"/> changes only for unavoidable breaking changes – plugins built for another major version are not loaded.</item>
/// <item><see cref="Minor"/> grows with every extension (new optional interfaces, new members with defaults) – a plugin needs a host with the same or a higher minor version.</item>
/// </list>
/// Every change is recorded in the CHANGELOG.
/// </summary>
public static class CoreApiVersion
{
    public const int Major = 1;

    public const int Minor = 8;

    /// <summary>The major version (kept from the time when the contract had a single number).</summary>
    public const int Current = Major;

    /// <summary>Contract version of this SDK as "major.minor".</summary>
    public const string Text = "1.8";

    /// <summary>Contract version of this SDK.</summary>
    public static Version Version { get; } = new(Major, Minor);

    /// <summary>Parses "major.minor" (also "major" or "major.minor.patch" – the patch is ignored).</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 4
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        version = new Version(major, minor);
        return true;
    }

    /// <summary>Can a host with this SDK load a plugin that needs <paramref name="required"/>?</summary>
    public static bool Supports(Version required) => Supports(required, Version);

    /// <summary>Same major version and a minor version that is not newer than the host's.</summary>
    public static bool Supports(Version required, Version host)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(host);
        return required.Major == host.Major && required.Minor <= host.Minor;
    }
}
