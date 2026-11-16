// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core;

/// <summary>
/// Version of the contract between core and modules (SPEC 4.8).
/// Only increase for unavoidable breaking changes and record it in the CHANGELOG.
/// </summary>
public static class CoreApiVersion
{
    public const int Current = 1;
}
