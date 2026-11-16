// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json.Nodes;
using UeBar.Core.Layout;
using UeBar.Core.Tiles;

namespace UeBar.Modules.HomeAssistant.Tiles;

/// <summary>
/// An HA tile: builds its <see cref="TileModel"/> only from elements (SPEC 4.2), redraws it from
/// the states and maps interactions to <c>call_service</c>. Thread-safe (states come
/// from the receive thread, interactions from the thread pool).
/// </summary>
internal abstract class HaTile : ITileInteractionHandler
{
    private readonly Lock _gate = new();
    private (int Token, string Text, DateTimeOffset Until)? _alert;
    private (int Token, string Text)? _flash;
    private int _nextToken;

    protected HaTile(HaTileHost host, TileConfig config, string fallbackIcon)
    {
        Host = host;
        Config = config;
        FallbackIcon = fallbackIcon;
        Model.Handler = this;
    }

    public TileModel Model { get; } = new();

    public TileConfig Config { get; }

    protected HaTileHost Host { get; }

    protected string FallbackIcon { get; }

    /// <summary>Entity that decides name, icon and availability.</summary>
    protected virtual string? MainEntity => Config.GetEntity("main") ?? Config.Entities.Values.FirstOrDefault();

    /// <summary>2x2 and larger show all elements (SPEC decision M2).</summary>
    protected bool IsLarge => Config.Size.Rows >= 2;

    public bool Uses(string entityId) => Config.Entities.Values.Any(e => string.Equals(e, entityId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Redraw the model from the current (possibly optimistic) states.</summary>
    public void Render()
    {
        lock (_gate)
        {
            var mainId = MainEntity;
            var main = Host.Get(mainId);
            var entry = Host.Entry(mainId);
            Model.Title = !string.IsNullOrWhiteSpace(Config.Label) ? Config.Label!
                : entry?.Name ?? main?.FriendlyName ?? mainId ?? "?";
            Model.Icon = !string.IsNullOrWhiteSpace(Config.Icon) ? Config.Icon
                : mainId is null ? FallbackIcon : PickIcon(mainId, main, entry);

            var problem = Host.ConnectionProblem()
                ?? (main is null ? (Host.Client.InitialStatesReceived ? HaText.T["Tile_NotFound"] : HaText.T["Tile_Loading"])
                    : IsUnavailable(main) ? HaText.T["State_Unavailable"] : null);

            if (problem is not null)
            {
                Model.IsUnavailable = true;
                Model.IsActive = false;
                Model.StateText = problem;
                foreach (var element in Model.Elements)
                {
                    element.IsEnabled = false;
                }
            }
            else
            {
                foreach (var element in Model.Elements)
                {
                    element.IsEnabled = true;
                }

                RenderCore(main!);
                if (_flash is { } flash)
                {
                    Model.StateText = flash.Text;
                }

                // Only report operable again once state and elements are in place.
                Model.IsUnavailable = false;
            }

            if (_alert is { } alert && alert.Until <= DateTimeOffset.UtcNow)
            {
                _alert = null;
            }

            Model.Alert = _alert?.Text;
        }
    }

    public Task HandleAsync(TileModel tile, TileInteraction interaction, CancellationToken cancellationToken)
    {
        HaAction? action;
        lock (_gate)
        {
            if (Model.IsUnavailable)
            {
                return Task.CompletedTask;
            }

            action = Map(interaction);
        }

        if (action is not null)
        {
            Host.Execute(this, action);
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Alerts

    public int SetAlert(string text, DateTimeOffset? until = null)
    {
        int token;
        lock (_gate)
        {
            token = ++_nextToken;
            _alert = (token, text, until ?? DateTimeOffset.MaxValue);
        }

        Render();
        return token;
    }

    public void ClearAlert(int token)
    {
        lock (_gate)
        {
            if (_alert?.Token != token)
            {
                return;
            }

            _alert = null;
        }

        Render();
    }

    public int SetFlash(string text)
    {
        int token;
        lock (_gate)
        {
            token = ++_nextToken;
            _flash = (token, text);
        }

        Render();
        return token;
    }

    public void ClearFlash(int token)
    {
        lock (_gate)
        {
            if (_flash?.Token != token)
            {
                return;
            }

            _flash = null;
        }

        Render();
    }

    // ------------------------------------------------------------------ for the tile types

    /// <summary>Set states (StateText, IsActive, elements). Only called if the main entity is available.</summary>
    protected abstract void RenderCore(HaEntityState main);

    /// <summary>Interaction → service call (or null = nothing to do).</summary>
    protected abstract HaAction? Map(TileInteraction interaction);

    protected virtual bool IsUnavailable(HaEntityState main) => main.IsUnavailable || main.IsUnknown;

    protected virtual string PickIcon(string entityId, HaEntityState? state, HaEntityEntry? entry)
    {
        var icon = HaIcons.For(entityId, state, entry);
        return icon == "Home" ? FallbackIcon : icon;
    }

    protected HaEntityState? State(string? entityId) => Host.Get(entityId);

    /// <summary>Only replace elements if the composition changes (otherwise only set values).</summary>
    protected void SetElements(params TileElement?[] elements)
    {
        var wanted = elements.OfType<TileElement>().ToList();
        if (Model.Elements.Count == wanted.Count && Model.Elements.Zip(wanted).All(p => ReferenceEquals(p.First, p.Second)))
        {
            return;
        }

        Model.Elements.Clear();
        foreach (var element in wanted)
        {
            element.IsEnabled = true;
            Model.Elements.Add(element);
        }
    }

    /// <summary>Only replace the buttons of a row if the composition changes.</summary>
    protected static void SetButtons(ButtonRowElement row, IReadOnlyList<TileButton> buttons)
    {
        if (row.Buttons.Count == buttons.Count && row.Buttons.Zip(buttons).All(p => ReferenceEquals(p.First, p.Second)))
        {
            return;
        }

        row.Buttons.Clear();
        foreach (var button in buttons)
        {
            row.Buttons.Add(button);
        }
    }

    /// <summary>Only replace the options of a choice if they change.</summary>
    protected static void SetOptions(ChoiceElement choice, IReadOnlyList<(string Id, string Label)> options)
    {
        if (choice.Options.Count == options.Count && choice.Options.Zip(options).All(p => p.First.Id == p.Second.Id && p.First.Label == p.Second.Label))
        {
            return;
        }

        choice.Options.Clear();
        foreach (var (id, label) in options)
        {
            choice.Options.Add(new ChoiceOption { Id = id, Label = label });
        }
    }

    protected static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    protected static JsonValue Json(double value) => JsonValue.Create(value);

    protected static JsonValue Json(string value) => JsonValue.Create(value);

    protected static JsonValue Json(bool value) => JsonValue.Create(value);

    /// <summary>Opposite of on/off.</summary>
    protected static string Flip(string state) => state == "on" ? "off" : "on";

    /// <summary>Optimistic: the state becomes <paramref name="expected"/>.</summary>
    protected static HaOptimistic ExpectState(HaEntityState current, string expected) =>
        new(current.EntityId, current.With(expected), s => s.State == expected);
}
