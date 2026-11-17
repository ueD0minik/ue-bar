// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger


namespace UeBar.Sdk;

// Optional capabilities from M5b. Extension only, nothing in the contract broken (SPEC 4.8).

/// <summary>
/// A module's layout store (<see cref="ILayoutStoreProvider"/>) is currently unreachable
/// (offline, not connected). This is an expected state, not a module fault: the core keeps working
/// with its local copy and reconciles later.
/// </summary>
public sealed class LayoutStoreUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A choice at first start (e.g. an area "Living room").</summary>
public sealed record LayoutSuggestionChoice(
    string Id,
    string Name,
    string? Icon = null,
    string? Detail = null,
    bool Selected = false);

/// <summary>What a module offers for selection at first start.</summary>
public sealed record LayoutSuggestionOptions(
    string Question,
    IReadOnlyList<LayoutSuggestionChoice> Choices,
    string? Hint = null);

/// <summary>
/// Suggested layout with choices (SPEC 9, M5b): the module offers options at first start
/// (e.g. areas); the app shows them generically and builds a layout from the selection.
/// Both must be fast (memory reads only).
/// </summary>
public interface ILayoutSuggestionOptions
{
    /// <summary>Offer for the first start, or null if nothing can be offered right now (e.g. not connected).</summary>
    LayoutSuggestionOptions? GetSuggestionOptions();

    /// <summary>Layout from the chosen options (IDs from <see cref="LayoutSuggestionOptions.Choices"/>).</summary>
    LayoutDocument? CreateSuggestedLayout(IReadOnlyCollection<string> selectedChoiceIds);
}
