// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.TestPlugin.Dependency;

/// <summary>Something a plugin brings along itself.</summary>
public static class Helper
{
    public static string Greeting => "hello from the dependency";

    /// <summary>Name of the load context this assembly lives in.</summary>
    public static string LoadContextName =>
        System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Helper).Assembly)?.Name ?? "";
}
