// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Contract 1.8 (P3-1): content actions – a plugin offers things to do with a text, an image or a file list
// ("Summarize", "Translate" …), other places (screenshot, clipboard history, pins, the marked text) offer them,
// and the core shows the answer in its own window. Everything new is optional – plugins built for an older
// contract run unchanged.

/// <summary>Kinds of content an action works with (<see cref="ContentActionInfo.Kinds"/>).</summary>
[Flags]
public enum ContentKinds
{
    None = 0,
    Text = 1,
    Image = 2,
    Files = 4,
    All = Text | Image | Files,
}

/// <summary>
/// The content a content action works on. Only what the user chose – the core hands it to exactly the one action
/// the user started, never to anyone else.
/// </summary>
public sealed record ContentItem
{
    public static ContentItem Empty { get; } = new();

    public string? Text { get; init; }

    /// <summary>An image as PNG.</summary>
    public byte[]? ImagePng { get; init; }

    /// <summary>Full paths of files and folders – only the paths, never the files.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Where it came from for people, e.g. the window title or "Clipboard" – may be shown, never needed.</summary>
    public string? SourceTitle { get; init; }

    /// <summary>The kinds this content has.</summary>
    public ContentKinds Kinds =>
        (string.IsNullOrWhiteSpace(Text) ? ContentKinds.None : ContentKinds.Text)
        | (ImagePng is { Length: > 0 } ? ContentKinds.Image : ContentKinds.None)
        | (Files is { Count: > 0 } ? ContentKinds.Files : ContentKinds.None);

    public bool IsEmpty => Kinds == ContentKinds.None;

    public static ContentItem FromText(string text) => new() { Text = text };

    public static ContentItem FromImage(byte[] png) => new() { ImagePng = png };
}

/// <summary>Where the user started a content action.</summary>
public enum ContentOrigin
{
    Other,

    /// <summary>The bar after a screenshot.</summary>
    Screenshot,

    /// <summary>An entry of the clipboard history.</summary>
    Clipboard,

    /// <summary>The menu of a pin.</summary>
    Pin,

    /// <summary>The text marked in the window in front (hotkey "Send marked text to …").</summary>
    Selection,

    /// <summary>A chat or question of the plugin itself (quick command, tile) – without other content.</summary>
    Chat,
}

/// <summary>A content action a module offers (<see cref="IContentActionProvider"/>).</summary>
/// <param name="Id">Unique within the module; comes back in <see cref="ContentActionRequest.ActionId"/>.</param>
/// <param name="Label">Name in the lists, e.g. "Summarize" – short, in the UI language.</param>
/// <param name="Kinds">The content it works with; it is offered when the content has one of these kinds.</param>
public sealed record ContentActionInfo(string Id, string Label, ContentKinds Kinds)
{
    /// <summary>Icon name (Segoe Fluent Icons, "U+E8C8").</summary>
    public string? Icon { get; init; }

    /// <summary>A second, smaller line in the list (what the action does).</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The user types an own question first ("Ask …"): the answer window opens with the question field and
    /// the action starts with <see cref="ContentActionRequest.Question"/>.
    /// </summary>
    public bool AsksQuestion { get; init; }

    /// <summary>
    /// The answer is a replacement for the content (a corrected or translated text): "Paste" and "Copy" are the
    /// main buttons of the answer window.
    /// </summary>
    public bool ReplacesContent { get; init; }
}

/// <summary>One finished turn of the conversation in an answer window (memory only, gone when it closes).</summary>
/// <param name="Question">What the user asked (null for the first turn of an action without a question).</param>
/// <param name="Answer">The whole answer as Markdown.</param>
public sealed record ContentTurn(string? Question, string Answer);

/// <summary>What the core asks a provider to do.</summary>
/// <param name="ActionId">The action (<see cref="ContentActionInfo.Id"/>).</param>
/// <param name="Content">The content the user chose (may be empty for <see cref="ContentOrigin.Chat"/>).</param>
public sealed record ContentActionRequest(string ActionId, ContentItem Content)
{
    /// <summary>The user's own text: the question of "Ask …", a follow-up, or the chat message; null = none.</summary>
    public string? Question { get; init; }

    /// <summary>Earlier turns in the same answer window, oldest first – empty for the first turn.</summary>
    public IReadOnlyList<ContentTurn> History { get; init; } = [];

    /// <summary>The option the user picked in the answer window (<see cref="IContentResponse.SetOptions"/>); null = the provider's default.</summary>
    public string? OptionId { get; init; }

    public ContentOrigin Origin { get; init; }

    /// <summary>UI language ("de", "en") – a hint for the language of the answer.</summary>
    public string? UiLanguage { get; init; }
}

/// <summary>A choice in the small switcher of the answer window, e.g. a model.</summary>
public sealed record ResponseOption(string Id, string Label);

/// <summary>
/// Where a provider writes its answer – the core's answer window (contract 1.8). Calls may come from any thread and
/// are cheap; after the action has finished (or was cancelled) they are ignored.
/// </summary>
public interface IContentResponse
{
    /// <summary>More of the answer (Markdown) – appended, shown at once (streaming).</summary>
    void Append(string markdown);

    /// <summary>Replaces the whole answer so far.</summary>
    void SetText(string markdown);

    /// <summary>A short line while working ("Thinking …"); null removes it.</summary>
    void SetStatus(string? text);

    /// <summary>
    /// The switcher in the window (e.g. models): picking another one runs the last turn again with that
    /// <see cref="ContentActionRequest.OptionId"/>. Empty list = no switcher.
    /// </summary>
    void SetOptions(IReadOnlyList<ResponseOption> options, string? selectedId);

    /// <summary>
    /// What "Copy", "Paste" and "Pin" use instead of the whole answer, e.g. only the corrected text without
    /// explanations; null = the whole answer.
    /// </summary>
    void SetResultText(string? text);
}

/// <summary>
/// The module offers content actions (contract 1.8). The list is read each time a list of actions opens.
/// <see cref="RunContentActionAsync"/> is called through the module guard only when the user starts the action
/// (or asks a follow-up, or picks another option) – a provider never sees content otherwise. The conversation lives
/// in the core's answer window; it ends when the window closes, on "New chat" or after
/// <see cref="ConversationIdleTimeout"/> without use. Throwing shows the message as an error in the window.
/// </summary>
public interface IContentActionProvider
{
    IReadOnlyList<ContentActionInfo> ContentActions { get; }

    Task RunContentActionAsync(ContentActionRequest request, IContentResponse response, CancellationToken cancellationToken);

    /// <summary>After this long without use the answer window forgets the conversation and closes.</summary>
    TimeSpan ConversationIdleTimeout => TimeSpan.FromMinutes(15);
}

/// <summary>An action of a running module as other places see it (no content involved).</summary>
public sealed record ContentActionEntry(string ModuleId, string ModuleName, ContentActionInfo Action);

/// <summary>How the core starts an action (<see cref="IContentActions"/>).</summary>
public sealed record ContentActionStart
{
    public ContentOrigin Origin { get; init; }

    /// <summary>The window "Paste" goes to (e.g. <see cref="ChoicePopupResult.Target"/>); null = no "Paste".</summary>
    public PasteTarget? PasteTarget { get; init; }

    /// <summary>Where the list of actions appears (<see cref="IContentActions.ChooseContentActionAsync"/>).</summary>
    public ChoicePlacement Placement { get; init; } = ChoicePlacement.Mouse;

    /// <summary>A first question that is sent at once (e.g. the text after a quick command).</summary>
    public string? Question { get; init; }
}

/// <summary>
/// Content actions of all running modules, offered on the <see cref="IModuleContext"/> (<c>context as IContentActions</c>,
/// contract 1.8). A place that has content (a screenshot, a history entry) lists the actions and starts the one the
/// user chose; the core then opens its answer window and calls the provider. Only metadata goes back – no module is
/// told about content it was not given by the user.
/// </summary>
public interface IContentActions
{
    /// <summary>Actions that work with at least one of <paramref name="kinds"/>, in the order of the modules and their lists.</summary>
    IReadOnlyList<ContentActionEntry> GetContentActions(ContentKinds kinds);

    /// <summary>Starts one action with the content: opens the answer window. False = no such action (any more) or nothing to work on.</summary>
    Task<bool> RunContentActionAsync(string moduleId, string actionId, ContentItem content, ContentActionStart? start, CancellationToken cancellationToken);

    /// <summary>Shows the list of actions for the content (the core's choice popup) and starts the chosen one. False = none chosen or none there.</summary>
    Task<bool> ChooseContentActionAsync(ContentItem content, ContentActionStart? start, CancellationToken cancellationToken);
}
