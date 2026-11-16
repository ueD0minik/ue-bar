// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Json;
using UeBar.Core.Layout;
using UeBar.Core.Logging;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Base layout in Home Assistant (SPEC 9, level 1; M5b): <c>frontend/get_user_data</c> /
/// <c>frontend/set_user_data</c> with the key <c>ue_bar_layout</c> – HA separates this automatically
/// per user, both are allowed without admin rights. Live through <c>frontend/subscribe_user_data</c>
/// (HA 2026.10 has it); without a subscription the store reports a possible change on every
/// (re)connect. Not connected → <see cref="LayoutStoreUnavailableException"/> (the core keeps working with
/// its local copy). No own timer/thread: the subscription hangs on the connection loop.
/// </summary>
internal sealed class HaLayoutStore : ILayoutStore
{
    public const string Key = "ue_bar_layout";

    /// <summary>From this size on, warn in the log (the layout should stay small: no states, no images).</summary>
    public const int WarnBytes = 64 * 1024;

    /// <summary>This long loading at startup waits for the connection.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(6);

    private readonly HaClient _client;
    private readonly Func<IModuleLogger> _log;
    private readonly Lock _lock = new();
    private string? _lastKnownHash;

    public HaLayoutStore(HaClient client, Func<IModuleLogger> log)
    {
        _client = client;
        _log = log;
        _client.SessionStarted += OnSessionStartedAsync;
    }

    public string StoreId => "homeassistant";

    public string StoreDisplayName => "Home Assistant";

    /// <summary>Last successfully written to HA.</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    /// <summary>Last successfully read from HA.</summary>
    public DateTimeOffset? LastLoadedAt { get; private set; }

    /// <summary>Stamp (updatedAt/updatedBy) of the last known version in HA.</summary>
    public string? LastKnownStamp { get; private set; }

    /// <summary>Live subscription active (null = not tried yet).</summary>
    public bool? LiveSubscription { get; private set; }

    public int LastSizeBytes { get; private set; }

    public event EventHandler? Changed;

    public async Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var result = await _client.SendCommandAsync(new JsonObject { ["type"] = "frontend/get_user_data", ["key"] = Key }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new LayoutStoreUnavailableException($"Layout from HA not readable: {result.ErrorCode} {result.ErrorMessage}");
        }

        var value = result.Result?["value"];
        LastLoadedAt = DateTimeOffset.Now;
        var layout = Parse(value);
        Remember(value, layout);
        return layout;
    }

    public async Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var json = UeBarJson.Serialize(layout);
        var bytes = Encoding.UTF8.GetByteCount(json);
        LastSizeBytes = bytes;
        if (bytes > WarnBytes)
        {
            _log().Log(LogLevel.Warning, $"Layout is large: {bytes / 1024} KB (guideline 64 KB) – does it contain states or images?");
        }

        var value = JsonNode.Parse(json);
        lock (_lock)
        {
            _lastKnownHash = Hash(value); // do not report the own echo from the subscription as a change
        }

        var result = await _client.SendCommandAsync(
            new JsonObject { ["type"] = "frontend/set_user_data", ["key"] = Key, ["value"] = value },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            lock (_lock)
            {
                _lastKnownHash = null;
            }

            throw new LayoutStoreUnavailableException($"Layout not saved in HA: {result.ErrorCode} {result.ErrorMessage}");
        }

        LastSavedAt = DateTimeOffset.Now;
        LastKnownStamp = Stamp(layout);
        _log().Log(LogLevel.Info, $"Layout saved in HA ({bytes / 1024.0:0.0} KB, {layout.Tabs.Count} tabs)");
    }

    /// <summary>After connecting: create the live subscription (sends the current state right away) and trigger a reconcile.</summary>
    private async Task OnSessionStartedAsync(CancellationToken ct)
    {
        var (result, _) = await _client.SubscribeAsync(
            new JsonObject { ["type"] = "frontend/subscribe_user_data", ["key"] = Key },
            OnUserDataEvent,
            ct).ConfigureAwait(false);
        LiveSubscription = result.Success;
        if (!result.Success)
        {
            _log().Log(LogLevel.Info, $"No live subscription for the layout ({result.ErrorCode}) – reconcile only when connecting");
        }

        // Reconnected: upload pending changes or fetch the new state.
        RaiseChanged();
    }

    private void OnUserDataEvent(JsonNode evt)
    {
        var value = evt["value"];
        var hash = Hash(value);
        bool changed;
        lock (_lock)
        {
            changed = _lastKnownHash is not null && hash != _lastKnownHash;
            _lastKnownHash = hash;
        }

        if (changed)
        {
            LastKnownStamp = value is JsonObject ? Stamp(TryParse(value)) : null;
            _log().Log(LogLevel.Info, "Layout in HA was changed (another device/session)");
            RaiseChanged();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConfigured)
        {
            throw new LayoutStoreUnavailableException("Home Assistant is not set up");
        }

        if (!await _client.WaitForConnectionAsync(ConnectWait, ct).ConfigureAwait(false))
        {
            throw new LayoutStoreUnavailableException($"Home Assistant not connected ({_client.Status.State})");
        }
    }

    private void Remember(JsonNode? value, LayoutDocument? layout)
    {
        lock (_lock)
        {
            _lastKnownHash = Hash(value);
        }

        LastKnownStamp = Stamp(layout);
        LastSizeBytes = value is null ? 0 : Encoding.UTF8.GetByteCount(value.ToJsonString());
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log().Log(LogLevel.Warning, $"Error reporting a layout change: {ex.Message}", ex);
        }
    }

    /// <summary>null = no layout in HA yet. Nonsense is reported, not silently replaced.</summary>
    private static LayoutDocument? Parse(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value is JsonObject
                ? UeBarJson.Deserialize<LayoutDocument>(value.ToJsonString())
                : throw new InvalidDataException("ue_bar_layout in HA is not a layout object");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"ue_bar_layout in HA is not a valid layout: {ex.Message}", ex);
        }
    }

    private static LayoutDocument? TryParse(JsonNode? value)
    {
        try
        {
            return Parse(value);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string Hash(JsonNode? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null")));

    private static string? Stamp(LayoutDocument? layout) =>
        layout?.UpdatedAt is { } at ? $"{at.ToLocalTime():dd.MM. HH:mm:ss}{(layout.UpdatedBy is { } by ? $" ({by})" : "")}" : null;
}
