// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// What the host offers every module on its context (P2-5). The core keeps the pins itself; picking a
/// screen area and the clipboard need the UI – the app sets them. Without them the module gets null/false.
/// </summary>
public sealed class HostServices
{
    public PinManager? Pins { get; set; }

    public IScreenAreaPicker? ScreenAreaPicker { get; set; }

    public IClipboardAccess? Clipboard { get; set; }

    /// <summary>"Save as" dialog (P2-5-Fix 1); the module ID decides which last folder is used.</summary>
    public Func<string, SaveFileRequest, CancellationToken, Task<string?>>? SaveDialog { get; set; }

    /// <summary>IDs the host uses itself (bus owner of the pins) – no module may take them.</summary>
    public static IReadOnlySet<string> ReservedIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PinManager.HostId };
}
