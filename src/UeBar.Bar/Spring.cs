// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// Damped spring curve for morphing pill → panel (One UI style). Overshoots just under 3 %
/// (requirement: at most 5 %) and ends exactly at 1.
/// </summary>
public static class Spring
{
    /// <summary>Damping ratio (1 = no overshoot).</summary>
    public const double Damping = 0.75;

    /// <summary>Natural frequency relative to the animation duration.</summary>
    public const double Omega = 8.5;

    public static double Evaluate(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        var damped = Omega * Math.Sqrt(1 - (Damping * Damping));
        var decay = Math.Exp(-Damping * Omega * t);
        return 1 - (decay * (Math.Cos(damped * t) + (Damping * Omega / damped * Math.Sin(damped * t))));
    }

    /// <summary>Highest value of the curve (for window reserve).</summary>
    public static double MaxOvershoot { get; } = Enumerable.Range(0, 1001).Max(i => Evaluate(i / 1000.0)) - 1;
}
