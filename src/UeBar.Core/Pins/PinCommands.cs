// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Core.Pins;

/// <summary>
/// The ways into the pins besides the SDK (P2-5): the quick command <c>pin &lt;text&gt;</c> (quick search and
/// Assist) and the bus action <c>pins.create_text</c> – with a permission like every action (owner
/// <see cref="PinManager.HostId"/>).
/// </summary>
public static class PinCommands
{
    public const string Prefix = "pin";

    public const string CreateTextAction = "pins.create_text";

    public static void Register(PinManager pins, QuickCommandService quickCommands, ModuleBus bus)
    {
        quickCommands.AddHostCommand(
            PinManager.HostId,
            new QuickCommandInfo(Prefix, CoreText.T["Pin_QuickCommand"], "Pin"),
            text => Task.FromResult(CreateFromCommand(pins, text)));

        var published = bus.For(PinManager.HostId).PublishAction(
            CreateTextAction,
            (call, _) => Task.FromResult(CreateFromBus(pins, call)),
            CoreText.T["Pin_BusAction"]);
        if (!published.Success)
        {
            throw new InvalidOperationException(published.Message ?? "pins.create_text not published");
        }
    }

    /// <summary>"pin Buy milk" → a text pin of the user.</summary>
    public static QuickCommandResult CreateFromCommand(PinManager pins, string text) =>
        pins.CreateText(text) is not null
            ? new QuickCommandResult(true, CoreText.T["Pin_Created"])
            : new QuickCommandResult(false, string.IsNullOrWhiteSpace(text) ? CoreText.T["Pin_NoText"] : CoreText.T.Format("Pin_TooMany", PinManager.MaxPins));

    /// <summary>
    /// <c>{ "text": "…", "title": "…" }</c> → <c>{ "id": "…" }</c>. The pin belongs to the calling plugin, so it
    /// can see and close it through its <see cref="IPinService"/>.
    /// </summary>
    public static JsonNode? CreateFromBus(PinManager pins, BusCall call)
    {
        var text = call.Arguments?["text"]?.GetValue<string>();
        var title = call.Arguments?["title"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("pins.create_text needs { \"text\": \"…\" }");
        }

        var id = pins.CreateText(text, title, owner: call.CallerModuleId)
            ?? throw new InvalidOperationException($"At most {PinManager.MaxPins} pins");
        return new JsonObject { ["id"] = id };
    }
}
