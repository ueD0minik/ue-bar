// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core.Abstractions;

// Optional capabilities from M7 (settings, distribution). Extension only (SPEC 4.8).

/// <summary>
/// Marks a developer/test module: off by default in release builds and marked as
/// "Developer" in the module list. A saved setting always wins over the default.
/// </summary>
public interface IDeveloperModule
{
}

/// <summary>
/// Information about this device for modules, offered on the <see cref="IModuleContext"/>
/// (check with <c>context as IHostInfo</c>).
/// </summary>
public interface IHostInfo
{
    /// <summary>Device name (setting "Device name", otherwise the Windows computer name).</summary>
    string DeviceName { get; }
}
