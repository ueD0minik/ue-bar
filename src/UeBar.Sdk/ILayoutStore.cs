// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger


namespace UeBar.Sdk;

/// <summary>
/// Where the (synchronized) base layout lives (SPEC 4.1 / 9). The core brings
/// <c>LocalFileLayoutStore</c>, modules can provide more through <see cref="ILayoutStoreProvider"/>.
/// </summary>
public interface ILayoutStore
{
    string StoreId { get; }

    string StoreDisplayName { get; }

    /// <summary>Loads the layout; null if none exists yet.</summary>
    Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken);

    /// <summary>
    /// The layout was changed from outside (file edited by hand, another device …) – or a
    /// remote store is reachable (again) and a reconcile makes sense (M5b).
    /// </summary>
    event EventHandler? Changed;
}
