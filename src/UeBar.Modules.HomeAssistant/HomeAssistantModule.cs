// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Tiles;
using UeBar.Modules.HomeAssistant.Tiles;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Home Assistant module (SPEC 8). Connection (local first, reconnect, heartbeat), states of the
/// layout entities live, search through the registries, call_service, bus announcements, settings
/// + connection test, diagnostics (M3); tile types from SPEC 7.1 with immediate feedback, cover
/// images, status line (M4); layout per HA user, suggestion from areas, non-admin (M5b); two languages (R1).
/// </summary>
public sealed class HomeAssistantModule :
    IModule,
    IModuleSettings,
    IModuleSettingsTest,
    ILayoutAware,
    ILayoutSuggestionProvider,
    IEntitySource,
    IStatusProvider,
    IModuleDiagnostics,
    IImageSource,
    ILocationProvider,
    ITileSlotAssistant,
    ILayoutStoreProvider,
    ILayoutSuggestionOptions,
    INotificationSource,
    IAssistProvider,
    IAssistConversationProvider,
    INowPlayingSource,
    IQuickCommandProvider
{
    public const string ModuleId = "homeassistant";

    public const string BusCallService = "ha.call_service";
    public const string BusStateChanged = "ha.state_changed";
    public const string BusConnection = "ha.connection";

    private readonly HaClient _client;
    private readonly HaTileHost _host;
    private readonly HaSlotAssistant _slots;
    private readonly HaLayoutStore _layoutStore;
    private readonly HaMobileApp _mobileApp;
    private readonly HaAssist _assist;
    private IReadOnlySet<string> _layoutEntities = new HashSet<string>();
    private IReadOnlyList<string> _layoutPlayers = [];
    private IReadOnlyList<string> _configuredPlayers = [];
    private string _todoList = HaSettings.DefaultTodoList;
    private NowPlayingInfo? _nowPlaying;
    private DeviceLocation _location = DeviceLocation.Unknown;
    private readonly IReadOnlyList<ITileType> _tileTypes;
    private readonly Dictionary<string, StatusValue> _statusValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private IModuleContext? _context;
    private IModuleLogger _log = new NullModuleLogger();

    public HomeAssistantModule()
        : this(new HaClientOptions())
    {
    }

    internal HomeAssistantModule(HaClientOptions options)
    {
        _client = new HaClient(options, new ForwardingLogger(this));
        _client.StatusChanged += OnClientStatus;
        _client.Store.Changed += OnStateChanged;
        _host = new HaTileHost(_client, () => _context, () => _log);
        _tileTypes = HaTileType.All(_host);
        _slots = new HaSlotAssistant(_client.Index, _client.Store, id => _tileTypes.FirstOrDefault(t => t.Id == id));
        _layoutStore = new HaLayoutStore(_client, () => _log);
        _mobileApp = new HaMobileApp(_client, () => _context, () => _log, n => NotificationReceived?.Invoke(this, n));
        _assist = new HaAssist(_client, () => _log);
    }

    public string Id => ModuleId;

    public string DisplayName => "Home Assistant";

    public string Icon => "Home";

    public ModuleStatus Status { get; private set; } = ModuleStatus.NotConfigured;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    internal HaClient Client => _client;

    internal HaTileHost TileHost => _host;

    internal HaLayoutStore LayoutStore => _layoutStore;

    internal HaMobileApp MobileApp => _mobileApp;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<EntityInfo>? EntityChanged;

    public event EventHandler<DeviceLocation>? LocationChanged;

    public event EventHandler<UeNotification>? NotificationReceived;

    public event EventHandler? NowPlayingChanged;

    /// <summary>Connected locally = home, connected externally or disconnected = away (SPEC 9).</summary>
    public DeviceLocation CurrentLocation => _location;

    // ------------------------------------------------------------------ Lifecycle

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _log = context.Log;

        var bus = context.Bus;
        bus.PublishAction(BusCallService, OnBusCallServiceAsync, "Call a Home Assistant service: { domain, service, data?, target? }");
        bus.PublishEvent(BusStateChanged, "State of a subscribed entity changed");
        bus.PublishValue(BusConnection, ConnectionValue(), "Connection state to Home Assistant");

        if (context is IUiLanguageInfo language)
        {
            HaText.Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        context.RunBackground("Home Assistant connection", ct => _client.RunAsync(ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _context = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Language switched (R1): status text, tiles and status values in the new language. The app
    /// recreates the tiles itself, so element labels follow as well.
    /// </summary>
    private void OnUiCultureChanged(object? sender, CultureInfo culture)
    {
        HaText.Culture = culture;
        OnClientStatus(_client.Status);
        StatusValue[] values;
        lock (_lock)
        {
            values = [.. _statusValues.Values];
        }

        foreach (var value in values)
        {
            UpdateStatusValue(value, _client.Store.Get(value.Key));
        }
    }

    // ------------------------------------------------------------------ Settings

    public IReadOnlyList<SettingField> SettingFields
    {
        get
        {
            // Choice from HA (M7): all media players or to-do lists HA currently knows.
            var all = _client.Index.All;
            SettingChoice Choice(HaEntityEntry e) => new(e.EntityId, $"{DisplayNameFor(e.EntityId)} ({e.EntityId})");
            var players = all.Where(e => e.Domain == "media_player").OrderBy(e => e.EntityId, StringComparer.Ordinal).Select(Choice).ToList();
            var todos = all.Where(e => e.Domain == "todo").OrderBy(e => e.EntityId, StringComparer.Ordinal).Select(Choice).ToList();
            return HaSettings.BuildFields(players, todos);
        }
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        _configuredPlayers = HaSettings.NowPlayingPlayers(values);
        _todoList = HaSettings.TodoList(values);
        _client.UpdateSettings(HaSettings.From(values));
        UpdateWanted();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SettingsTestLine>> TestSettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var settings = HaSettings.From(values);
        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            return [new SettingsTestLine(HaText.T["Setting_Token"], false, HaText.T["Test_Missing"])];
        }

        var lines = new List<SettingsTestLine>();
        foreach (var (url, isLocal) in new[] { (settings.LocalUrl, true), (settings.ExternalUrl, false) })
        {
            var title = $"{(isLocal ? HaText.T["Test_Local"] : HaText.T["Test_External"])} ({HaSettings.Describe(url)})";
            if (url is null)
            {
                // Empty = not used (M7: both addresses optional, one is enough) – only nonsense is an error.
                var raw = values.GetString(isLocal ? HaSettings.KeyLocalUrl : HaSettings.KeyExternalUrl);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    lines.Add(new SettingsTestLine(title, false, HaText.T["Test_InvalidUrl"]));
                }

                continue;
            }

            var probe = await HaClient.ProbeAsync(url, settings.Token, TimeSpan.FromSeconds(isLocal ? 3 : 8), cancellationToken).ConfigureAwait(false);
            var ms = $"{probe.Elapsed.TotalMilliseconds:0} ms";
            lines.Add(probe switch
            {
                { TokenValid: true } => new SettingsTestLine(title, true, HaText.T.Format("Test_TokenValid", probe.Version, ms)),
                { Reachable: true } => new SettingsTestLine(title, false, HaText.T.Format("Test_TokenInvalid", ms)),
                _ => new SettingsTestLine(title, false, HaText.T.Format("Test_Unreachable", probe.Error, ms)),
            });
        }

        if (lines.Count == 0)
        {
            lines.Add(new SettingsTestLine(HaText.T["Test_Address"], false, HaText.T["Test_NoAddress"]));
        }

        return lines;
    }

    // ------------------------------------------------------------------ Layout

    public void OnLayoutChanged(ModuleLayoutInfo layout)
    {
        _layoutEntities = layout.ReferencedEntities;
        _layoutPlayers = [.. layout.Tiles.SelectMany(t => t.Entities.Values).Where(e => e.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
        UpdateWanted();
    }

    /// <summary>Keep live: everything from the layout + the Now Playing players.</summary>
    private void UpdateWanted()
    {
        var wanted = new HashSet<string>(_layoutEntities, StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(_configuredPlayers);
        _client.SetWantedEntities(wanted);
        RefreshNowPlaying();
    }

    // ------------------------------------------------------------------ Extras (M6)

    private IReadOnlyList<string> NowPlayingCandidates => _configuredPlayers.Count > 0 ? _configuredPlayers : _layoutPlayers;

    public NowPlayingInfo? CurrentNowPlaying => _nowPlaying;

    public async Task ExecuteNowPlayingAsync(NowPlayingCommand command, CancellationToken cancellationToken)
    {
        if (_nowPlaying is not { } current)
        {
            return;
        }

        var service = command switch
        {
            NowPlayingCommand.Next => "media_next_track",
            NowPlayingCommand.Previous => "media_previous_track",
            _ => "media_play_pause",
        };
        var result = await _client.CallServiceAsync("media_player", service, null, new JsonObject { ["entity_id"] = current.SourceId }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            _log.Log(LogLevel.Warning, $"Now Playing {service} failed: {result.ErrorMessage}");
        }
    }

    private void RefreshNowPlaying()
    {
        var next = HaNowPlaying.Choose(NowPlayingCandidates, _client.Store.Get, DisplayNameFor);
        if (Equals(next, _nowPlaying))
        {
            return;
        }

        _nowPlaying = next;
        NowPlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    public string AssistId => ModuleId;

    public string AssistDisplayName => "Home Assistant";

    public Task<AssistResponse> AskAsync(string text, CancellationToken cancellationToken) => _assist.AskAsync(text, cancellationToken);

    /// <summary>HA's conversation timeout (from the HA source, M6-Fix 2).</summary>
    public TimeSpan ConversationTimeout => HaAssist.ConversationTimeout;

    public Task<AssistResponse> AskAsync(string text, string? conversationId, CancellationToken cancellationToken) =>
        _assist.AskAsync(text, conversationId, cancellationToken);

    public IReadOnlyList<QuickCommandInfo> QuickCommands => [new QuickCommandInfo("+", HaText.T["Quick_AddToList"], "Add")];

    public async Task<QuickCommandResult> ExecuteQuickCommandAsync(string prefix, string argument, CancellationToken cancellationToken)
    {
        if (prefix != "+")
        {
            return new QuickCommandResult(false, HaText.T.Format("Quick_Unknown", prefix));
        }

        var list = ResolveTodoList();
        if (list is null)
        {
            return new QuickCommandResult(false, HaText.T["Quick_NoList"]);
        }

        var result = await _client.CallServiceAsync("todo", "add_item", new JsonObject { ["item"] = argument }, new JsonObject { ["entity_id"] = list }, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? new QuickCommandResult(true, HaText.T.Format("Quick_Added", argument, DisplayNameFor(list)))
            : new QuickCommandResult(false, HaText.T.Format("Quick_NotAdded", result.ErrorMessage));
    }

    /// <summary>Configured list, otherwise the first existing <c>todo.*</c>.</summary>
    internal string? ResolveTodoList()
    {
        if (_client.Index.Get(_todoList) is not null || _client.Index.Count == 0)
        {
            return _todoList;
        }

        return _client.Index.All.Where(e => e.Domain == "todo").OrderBy(e => e.EntityId, StringComparer.Ordinal).FirstOrDefault()?.EntityId;
    }

    /// <summary>Layout per HA user (<c>frontend/user_data</c>) – only if HA is set up.</summary>
    public IReadOnlyList<ILayoutStore> LayoutStores => _client.IsConfigured ? [_layoutStore] : [];

    /// <summary>General suggestion: the preselected options (routines); null as long as nothing is known.</summary>
    public LayoutDocument? GetSuggestedLayout() =>
        HaLayoutSuggestion.Options(_client.Index) is { } options
            ? HaLayoutSuggestion.Create(_client.Index, [.. options.Choices.Where(c => c.Selected).Select(c => c.Id)])
            : null;

    public LayoutSuggestionOptions? GetSuggestionOptions() => HaLayoutSuggestion.Options(_client.Index);

    public LayoutDocument? CreateSuggestedLayout(IReadOnlyCollection<string> selectedChoiceIds) =>
        HaLayoutSuggestion.Create(_client.Index, selectedChoiceIds);

    // ------------------------------------------------------------------ Search

    public async Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var hits = _client.Index.Search(query ?? "", maxResults);
        var results = new EntityInfo[hits.Count];

        // State on demand: subscribed ones from memory, the first 10 others through REST.
        var fetches = hits.Take(10).Select(h => _client.FetchStateAsync(h.EntityId, cancellationToken)).ToArray();
        var states = await Task.WhenAll(fetches).ConfigureAwait(false);
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var state = i < states.Length ? states[i] : _client.Store.Get(hit.EntityId);
            results[i] = new EntityInfo(hit.EntityId, hit.Name, HaIcons.For(hit.EntityId, state, hit), hit.Area,
                state is null ? null : StateFormatter.Format(state, hit), hit.Domain);
        }

        return results;
    }

    public async Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        var domain = entityId.Split('.', 2)[0];
        var (serviceDomain, service) = domain switch
        {
            "script" => ("script", "turn_on"),
            "scene" => ("scene", "turn_on"),
            "button" => ("button", "press"),
            "media_player" => ("media_player", "media_play_pause"),
            _ => ("homeassistant", "toggle"),
        };
        var result = await _client.CallServiceAsync(serviceDomain, service, null, new JsonObject { ["entity_id"] = entityId }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            _log.Log(LogLevel.Warning, $"Default action for {entityId} failed: {result.ErrorMessage}");
        }
    }

    public TileConfig? SuggestTile(EntityInfo entity)
    {
        var suggestion = HaTileType.ForDomain(entity.Id.Split('.', 2)[0]);
        return new TileConfig
        {
            Id = "ha-" + Guid.NewGuid().ToString("N")[..8],
            Module = ModuleId,
            Type = suggestion.Type,
            Size = suggestion.Size,
            Entities = new Dictionary<string, string> { ["main"] = entity.Id },
        };
    }

    // ------------------------------------------------------------------ Status line

    public IReadOnlyList<StatusValueInfo> AvailableStatusValues => [];

    public StatusValue? GetStatusValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_statusValues.TryGetValue(key, out var value))
            {
                value = new StatusValue { Key = key, Icon = HaIcons.ForDomain(key.Split('.', 2)[0]) };
                _statusValues[key] = value;
            }

            UpdateStatusValue(value, _client.Store.Get(key));
            return value;
        }
    }

    // ------------------------------------------------------------------ Diagnostics

    public IReadOnlyList<string> GetDiagnostics()
    {
        var lines = new List<string>
        {
            HaText.T.Format("Diag_Status", _client.Status.Text),
            HaText.T.Format("Diag_Server", _client.ConnectedUrl is { } url ? HaSettings.Describe(url) + " (" + (_client.Status.State == HaConnectionState.ConnectedLocal ? HaText.T["Where_Local"] : HaText.T["Where_External"]) + ")" : "–"),
            HaText.T.Format("Diag_Version", _client.HaVersion ?? "–"),
            HaText.T.Format("Diag_User", _client.UserName is null && _client.IsAdmin is null ? "–" : $"{_client.UserName ?? "?"} ({HaClient.AdminText(_client.IsAdmin)})"),
            HaText.T.Format("Diag_ConnectedSince", _client.ConnectedSince is { } since ? since.ToString("T", HaText.FormatCulture) : "–"),
            HaText.T.Format("Diag_Entities", _client.WantedCount, _client.InitialStatesReceived ? _client.SubscribedCount.ToString(HaText.FormatCulture) : "–"),
            HaText.T.Format("Diag_Registry", _client.Index.Count, _client.Index.AreaCount, HaClient.IndexSourceText(_client.Index.Source)),
            HaText.T.Format("Diag_Layout", LayoutLine()),
            HaText.T.Format("Diag_Notifications", _mobileApp.State, _mobileApp.ServiceName, _mobileApp.Received),
            HaText.T.Format("Diag_NowPlaying", _nowPlaying is { } np ? $"{np.Title} ({np.SourceName}, {(np.IsPlaying ? HaText.T["Diag_Playing"] : HaText.T["Diag_Paused"])})" : "–", NowPlayingCandidates.Count == 0 ? HaText.T["Diag_None"] : string.Join(", ", NowPlayingCandidates)),
        };

        if (_client.InitialStatesReceived && _client.MissingEntities is { Count: > 0 } missing)
        {
            lines.Add(HaText.T.Format("Diag_Missing", missing.Count, string.Join(", ", missing)));
        }

        lines.Add(HaText.T.Format("Diag_Tiles", _host.Tiles.Count));

        if (_client.LastError is { } error)
        {
            lines.Add(HaText.T.Format("Diag_LastError", error));
        }

        var recent = _client.Store.Recent(5);
        lines.Add(recent.Count == 0 ? HaText.T["Diag_RecentNone"] : HaText.T["Diag_Recent"]);
        foreach (var change in recent)
        {
            lines.Add($"  {change.At.ToString("T", HaText.FormatCulture)}  {change.EntityId}: {change.OldState?.State ?? "–"} → {change.NewState?.State ?? HaText.T["Diag_Removed"]}");
        }

        return lines;
    }

    private string LayoutLine()
    {
        var store = _layoutStore;
        var parts = new List<string>();
        parts.Add(store.LastSavedAt is { } saved ? HaText.T.Format("Diag_LayoutSaved", saved.ToString("T", HaText.FormatCulture)) : HaText.T["Diag_LayoutNotSaved"]);
        if (store.LastLoadedAt is { } loaded)
        {
            parts.Add(HaText.T.Format("Diag_LayoutLoaded", loaded.ToString("T", HaText.FormatCulture)));
        }

        if (store.LastKnownStamp is { } stamp)
        {
            parts.Add(HaText.T.Format("Diag_LayoutStamp", stamp));
        }

        if (store.LastSizeBytes > 0)
        {
            parts.Add((store.LastSizeBytes / 1024.0).ToString("0.0", HaText.FormatCulture) + " KB");
        }

        parts.Add(store.LiveSubscription switch { true => HaText.T["Diag_Live"], false => HaText.T["Diag_NotLive"], _ => "–" });
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ Edit mode

    public Task<IReadOnlyList<EntityInfo>> SuggestForSlotAsync(TileConfig draft, string slot, string query, int maxResults, CancellationToken cancellationToken) =>
        Task.FromResult(_slots.Suggest(draft, slot, query, maxResults));

    public TileConfig CompleteSlots(TileConfig draft) => _slots.Complete(draft);

    // ------------------------------------------------------------------ Location

    private void UpdateLocation(HaConnectionState state)
    {
        var location = state switch
        {
            HaConnectionState.ConnectedLocal => DeviceLocation.Home,
            HaConnectionState.ConnectedExternal or HaConnectionState.Disconnected => DeviceLocation.Away,
            HaConnectionState.Connecting => _location, // keep the last location while reconnecting
            _ => DeviceLocation.Unknown,
        };

        if (location == _location)
        {
            return;
        }

        _location = location;
        LocationChanged?.Invoke(this, location);
    }

    // ------------------------------------------------------------------ Images

    public async Task<ModuleImage?> LoadImageAsync(string imageId, CancellationToken cancellationToken)
    {
        if (HaImages.EntityFor(imageId) is not { } entityId
            || _client.Store.Get(entityId)?.Attribute<string>("entity_picture") is not { } picture)
        {
            return null;
        }

        return await _client.FetchImageAsync(picture, cancellationToken).ConfigureAwait(false) is { } image
            ? new ModuleImage(image.Data, image.ContentType)
            : null;
    }

    /// <summary>Display name: registry name, otherwise friendly_name, otherwise entity ID.</summary>
    internal string DisplayNameFor(string? entityId)
    {
        if (entityId is null)
        {
            return "?";
        }

        return _client.Index.Get(entityId)?.Name ?? _client.Store.Get(entityId)?.FriendlyName ?? entityId;
    }

    // ------------------------------------------------------------------ internal

    private async Task<JsonNode?> OnBusCallServiceAsync(BusCall call, CancellationToken ct)
    {
        var args = call.Arguments as JsonObject;
        var domain = args?["domain"]?.ToString();
        var service = args?["service"]?.ToString();
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
        {
            return new JsonObject { ["success"] = false, ["error"] = "domain and service are required" };
        }

        var result = await _client.CallServiceAsync(domain, service, args!["data"] as JsonObject, args["target"] as JsonObject, ct).ConfigureAwait(false);
        return result.Success
            ? new JsonObject { ["success"] = true, ["result"] = result.Result?.DeepClone() }
            : new JsonObject { ["success"] = false, ["error"] = result.ErrorMessage, ["code"] = result.ErrorCode };
    }

    private void OnClientStatus(HaStatus status)
    {
        var moduleStatus = status.State switch
        {
            HaConnectionState.NotConfigured => ModuleStatus.NotConfigured,
            HaConnectionState.ConnectedLocal or HaConnectionState.ConnectedExternal => new ModuleStatus(ModuleState.Connected, status.Text),
            HaConnectionState.AuthInvalid => ModuleStatus.Error(status.Text),
            HaConnectionState.Stopped => ModuleStatus.Stopped,
            _ => new ModuleStatus(ModuleState.Disconnected, status.Text),
        };

        Status = moduleStatus;
        UpdateLocation(status.State);
        _host.RenderAll();
        StatusChanged?.Invoke(this, moduleStatus);
        _context?.Bus.SetValue(BusConnection, ConnectionValue());
    }

    private void OnStateChanged(HaStateChange change)
    {
        _host.OnStateChanged(change);
        if (NowPlayingCandidates.Contains(change.EntityId, StringComparer.OrdinalIgnoreCase))
        {
            RefreshNowPlaying();
        }

        StatusValue? status;
        lock (_lock)
        {
            _statusValues.TryGetValue(change.EntityId, out status);
        }

        if (status is not null)
        {
            UpdateStatusValue(status, change.NewState);
        }

        _context?.Bus.RaiseEvent(BusStateChanged, new JsonObject
        {
            ["entity_id"] = change.EntityId,
            ["state"] = change.NewState?.State,
            ["old_state"] = change.OldState?.State,
        });

        var entry = _client.Index.Get(change.EntityId);
        EntityChanged?.Invoke(this, new EntityInfo(
            change.EntityId,
            entry?.Name ?? change.NewState?.FriendlyName ?? change.EntityId,
            HaIcons.For(change.EntityId, change.NewState, entry),
            entry?.Area,
            StateFormatter.Format(change.NewState, entry),
            change.EntityId.Split('.', 2)[0]));
    }

    /// <summary>
    /// Status line (M4): weather = icon by state + temperature, temperature sensors with 1
    /// decimal, otherwise the formatted state. Keep it short – this also shows in the pill.
    /// </summary>
    private void UpdateStatusValue(StatusValue value, HaEntityState? state)
    {
        var entry = _client.Index.Get(value.Key);
        var name = DisplayNameFor(value.Key);
        if (state is null || state.IsUnavailable || state.IsUnknown)
        {
            value.Icon = HaIcons.ForDomain(value.Key.Split('.', 2)[0]);
            value.Text = "–";
            value.Tooltip = $"{name}: {(state is null ? HaText.T["Status_NoData"] : StateFormatter.Format(state, entry))}";
            return;
        }

        if (state.Domain == "weather")
        {
            var condition = StateFormatter.WeatherCondition(state.State);
            var temperature = StateFormatter.Temperature(state);
            value.Icon = HaIcons.ForWeather(state.State);
            value.Text = temperature ?? condition;
            value.Tooltip = temperature is null ? $"{name}: {condition}" : $"{name}: {condition}, {temperature}";
            return;
        }

        value.Icon = HaIcons.For(state.EntityId, state, entry);
        if (state.Attribute<string>("device_class") == "temperature" && state.StateNumber is { } celsius)
        {
            value.Text = $"{StateFormatter.FixedNumber(celsius, 1)} {state.Attribute<string>("unit_of_measurement") ?? "°C"}";
        }
        else
        {
            value.Text = StateFormatter.Format(state, entry);
        }

        value.Tooltip = $"{name}: {value.Text}";
    }

    private JsonObject ConnectionValue() => new()
    {
        ["state"] = _client.Status.State.ToString(),
        ["text"] = _client.Status.Text,
        ["haVersion"] = _client.HaVersion,
    };

    private void OnNetworkChanged(object? sender, EventArgs e) => _client.NotifyNetworkChanged();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _client.NotifyNetworkChanged();

    /// <summary>Forwards to the logger of the current context (the client is created before the start).</summary>
    private sealed class ForwardingLogger(HomeAssistantModule module) : IModuleLogger
    {
        public void Log(LogLevel level, string message, Exception? exception = null) => module._log.Log(level, message, exception);
    }

    private sealed class NullModuleLogger : IModuleLogger
    {
        public void Log(LogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
