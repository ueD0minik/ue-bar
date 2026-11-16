// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.Core.Abstractions;

// Optional capabilities from R1 (two languages). Extension only (SPEC 4.8).

/// <summary>
/// Current UI language for modules, offered on the <see cref="IModuleContext"/>
/// (check with <c>context as IUiLanguageInfo</c>). Modules bring their own texts; numbers and
/// dates follow the Windows region (<see cref="CultureInfo.CurrentCulture"/>), not the UI language.
/// </summary>
public interface IUiLanguageInfo
{
    /// <summary>Culture for texts (de-DE or en-US).</summary>
    CultureInfo UiCulture { get; }

    /// <summary>
    /// Raised (through the module guard) when the user switches the language. Modules should
    /// refresh their tile texts and status.
    /// </summary>
    event EventHandler<CultureInfo>? UiCultureChanged;
}
