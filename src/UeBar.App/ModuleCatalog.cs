// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Modules.Dummy;
using UeBar.Modules.HomeAssistant;

namespace UeBar.App;

/// <summary>
/// The ONLY place where modules are registered (SPEC 4.4).
/// New module = new project + one line here.
/// </summary>
internal static class ModuleCatalog
{
    public static void RegisterAll(ModuleRegistry registry)
    {
        // order = order of the suggested tabs
        registry.Register(new HomeAssistantModule());
        registry.Register(new DummyModule());
    }
}
