// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Core.Privacy;

/// <summary>"Hide from screen captures" (P2-7) as the commands see it – the app does the work on its UI thread.</summary>
public interface ICaptureHiding
{
    /// <summary>False if this Windows cannot hide windows from captures at all.</summary>
    bool IsAvailable { get; }

    bool IsHidden { get; }

    /// <summary>Switch (saved, applied to every window at once); <paramref name="source"/> is for the log. Returns the new state.</summary>
    Task<bool> SetHiddenAsync(bool hidden, string source);
}

/// <summary>
/// The ways to switch "hide from screen captures" besides settings and tray (P2-7): the quick command <c>privat</c>
/// (alone it switches over, <c>privat an</c>/<c>privat aus</c>) and the bus action <c>privacy.capture_hidden</c> with
/// <c>{ "state": "on" | "off" | "toggle" }</c> – with a permission like every action (owner <see cref="HostId"/>), so for
/// example an automation of another plugin or a later "if OBS runs" can switch it.
/// </summary>
public static class CapturePrivacyCommands
{
    public const string HostId = "privacy";

    public const string Prefix = "privat";

    public const string ActionId = "privacy.capture_hidden";

    public static void Register(ICaptureHiding hiding, QuickCommandService quickCommands, ModuleBus bus)
    {
        ArgumentNullException.ThrowIfNull(hiding);
        ArgumentNullException.ThrowIfNull(quickCommands);
        ArgumentNullException.ThrowIfNull(bus);
        quickCommands.AddHostCommand(
            HostId,
            new QuickCommandInfo(Prefix, CoreText.T["Privacy_QuickCommand"], "Hide") { ArgumentOptional = true },
            text => RunCommandAsync(hiding, text));

        var published = bus.For(HostId).PublishAction(ActionId, (call, _) => RunBusAsync(hiding, call), CoreText.T["Privacy_BusAction"]);
        if (!published.Success)
        {
            throw new InvalidOperationException(published.Message ?? $"{ActionId} not published");
        }
    }

    /// <summary>"an"/"on"/"ein" → true, "aus"/"off" → false, empty/"umschalten"/"toggle" → the opposite; else null.</summary>
    public static bool? Wanted(string? text, bool current) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "toggle" or "umschalten" or "wechseln" => !current,
        "on" or "an" or "ein" or "true" or "1" => true,
        "off" or "aus" or "false" or "0" => false,
        _ => null,
    };

    public static async Task<QuickCommandResult> RunCommandAsync(ICaptureHiding hiding, string text)
    {
        if (!hiding.IsAvailable)
        {
            return new QuickCommandResult(false, CoreText.T["Privacy_NotAvailable"]);
        }

        if (Wanted(text, hiding.IsHidden) is not { } wanted)
        {
            return new QuickCommandResult(false, CoreText.T["Privacy_Usage"]);
        }

        var now = await hiding.SetHiddenAsync(wanted, "quick command").ConfigureAwait(false);
        return new QuickCommandResult(now == wanted, CoreText.T[now ? "Privacy_NowOn" : "Privacy_NowOff"]);
    }

    /// <summary><c>{ "state": "on" | "off" | "toggle" }</c> (or a bool; missing = toggle) → <c>{ "hidden": true|false }</c>.</summary>
    public static async Task<JsonNode?> RunBusAsync(ICaptureHiding hiding, BusCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!hiding.IsAvailable)
        {
            throw new InvalidOperationException("Hiding from screen captures is not possible on this Windows");
        }

        var state = call.Arguments?["state"] switch
        {
            null => "",
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "on" : "off",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => "?",
        };
        var wanted = Wanted(state, hiding.IsHidden)
            ?? throw new ArgumentException($"{ActionId} needs {{ \"state\": \"on\" | \"off\" | \"toggle\" }}");
        var now = await hiding.SetHiddenAsync(wanted, $"bus ({call.CallerModuleId})").ConfigureAwait(false);
        return new JsonObject { ["hidden"] = now };
    }
}
