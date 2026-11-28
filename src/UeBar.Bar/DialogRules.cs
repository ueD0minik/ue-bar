// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What a message of the app is (P3-1: own dialogs instead of the Windows message box).</summary>
public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>Which buttons it has.</summary>
public enum DialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

/// <summary>The answer: true = OK/Yes, false = Cancel/No (also Esc and closing the window).</summary>
public enum DialogAnswer
{
    Confirmed,
    Declined,
}

/// <summary>Keys the dialog reacts to by itself (everything else goes to the focused button).</summary>
public enum DialogKey
{
    Enter,
    Escape,
    Other,
}

/// <summary>
/// Rules of the app's own dialog (P3-1), without WPF: which buttons, which one is the default, what Enter and Esc do.
/// A question that could do harm (deleting, ending another instance) starts on "No" – Enter then declines.
/// </summary>
public static class DialogRules
{
    /// <summary>The buttons from left to right: the confirming one first, then the declining one (if any).</summary>
    public static IReadOnlyList<DialogAnswer> Buttons(DialogButtons buttons) => buttons == DialogButtons.Ok
        ? [DialogAnswer.Confirmed]
        : [DialogAnswer.Confirmed, DialogAnswer.Declined];

    /// <summary>The button that has the focus at first (Enter).</summary>
    public static DialogAnswer Default(DialogButtons buttons, bool safeDefault) =>
        buttons != DialogButtons.Ok && safeDefault ? DialogAnswer.Declined : DialogAnswer.Confirmed;

    /// <summary>Answer for a key, null = not handled here. Esc and closing always decline – with only "OK" that is the same.</summary>
    public static DialogAnswer? OnKey(DialogKey key, DialogAnswer focused, DialogButtons buttons) => key switch
    {
        DialogKey.Enter => focused,
        DialogKey.Escape => buttons == DialogButtons.Ok ? DialogAnswer.Confirmed : DialogAnswer.Declined,
        _ => null,
    };

    /// <summary>Closing the window with the X.</summary>
    public static DialogAnswer OnClose(DialogButtons buttons) => buttons == DialogButtons.Ok ? DialogAnswer.Confirmed : DialogAnswer.Declined;

    /// <summary>Icon of the kind (Segoe Fluent Icons).</summary>
    public static string Glyph(DialogKind kind) => kind switch
    {
        DialogKind.Warning => "",
        DialogKind.Error => "",
        DialogKind.Question => "",
        _ => "",
    };
}
