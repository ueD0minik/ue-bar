// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.RegularExpressions;

namespace UeBar.Modules.Timer;

/// <summary>Result of reading "10", "1:30", "25 Learning", "90s" …</summary>
internal sealed record TimerRequest(TimeSpan Duration, string? Name);

/// <summary>
/// Reads the text after the quick command "t": first the duration, the rest is the name.
/// <list type="bullet">
/// <item><c>10</c>, <c>2,5</c> – minutes</item>
/// <item><c>1:30</c> – hours:minutes</item>
/// <item><c>90s</c>, <c>10m</c>, <c>1h</c>, <c>1h30</c> – with unit</item>
/// </list>
/// More than 24 h or nothing at all is refused.
/// </summary>
internal static partial class TimerCommand
{
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    [GeneratedRegex(@"^(?:(?<h>\d{1,2}):(?<hm>[0-5]?\d)|(?<hh>\d{1,2})h(?<hhm>\d{1,2})?(?:m(?:in)?)?|(?<n>\d+(?:[.,]\d+)?)(?<unit>s|sek|sec|m|min|h)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    public static bool TryParse(string? text, out TimerRequest request)
    {
        request = new TimerRequest(TimeSpan.Zero, null);
        var parts = (text ?? "").Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !TryParseDuration(parts[0], out var duration))
        {
            return false;
        }

        request = new TimerRequest(duration, parts.Length > 1 ? parts[1].Trim() : null);
        return true;
    }

    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var match = DurationPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (match.Groups["h"].Success)
        {
            duration = TimeSpan.FromHours(int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture))
                + TimeSpan.FromMinutes(int.Parse(match.Groups["hm"].Value, CultureInfo.InvariantCulture));
        }
        else if (match.Groups["hh"].Success)
        {
            duration = TimeSpan.FromHours(int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture))
                + (match.Groups["hhm"].Success ? TimeSpan.FromMinutes(int.Parse(match.Groups["hhm"].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero);
        }
        else
        {
            var number = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            duration = match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "s" or "sek" or "sec" => TimeSpan.FromSeconds(number),
                "h" => TimeSpan.FromHours(number),
                _ => TimeSpan.FromMinutes(number),
            };
        }

        duration = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));
        return duration > TimeSpan.Zero && duration <= Longest;
    }

    /// <summary>Presets from the setting "5, 10, 15, 25, 45" (minutes; also "1:30", "90s").</summary>
    public static IReadOnlyList<TimeSpan> ParsePresets(string? text)
    {
        var result = new List<TimeSpan>();
        foreach (var part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseDuration(part, out var d) && !result.Contains(d))
            {
                result.Add(d);
            }
        }

        return result.Count > 0 ? result.Take(6).ToList() : [.. Defaults];
    }

    public static readonly TimeSpan[] Defaults = [.. new[] { 5, 10, 15, 25, 45 }.Select(m => TimeSpan.FromMinutes(m))];
}
