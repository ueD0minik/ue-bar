// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using UeBar.Sdk;

namespace UeBar.Modules.HomeAssistant;

internal enum HaConnectionState
{
    NotConfigured,
    Connecting,
    ConnectedLocal,
    ConnectedExternal,
    Disconnected,
    AuthInvalid,
    Stopped,
}

internal sealed record HaStatus(HaConnectionState State, TimeSpan? RetryIn = null)
{
    /// <summary>Text in the current UI language (R1) – computed on every read, so a language switch shows at once.</summary>
    public string Text => State switch
    {
        HaConnectionState.NotConfigured => HaText.T["Status_NotConfigured"],
        HaConnectionState.Connecting => HaText.T["Status_Connecting"],
        HaConnectionState.ConnectedLocal => HaText.T["Status_ConnectedLocal"],
        HaConnectionState.ConnectedExternal => HaText.T["Status_ConnectedExternal"],
        HaConnectionState.Disconnected when RetryIn is { } delay => HaText.T.Format("Status_DisconnectedRetry", HaClient.Seconds(delay)),
        HaConnectionState.Disconnected => HaText.T["Status_Disconnected"],
        HaConnectionState.AuthInvalid => HaText.T["Status_TokenInvalid"],
        _ => HaText.T["Status_Stopped"],
    };
}

/// <summary>Times and limits (tests set smaller values).</summary>
internal sealed record HaClientOptions
{
    public TimeSpan LocalTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan ExternalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LocalRecheckInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan NetworkDebounce { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Clock of the connection loop: ping interval, pong time limit, network debounce, local recheck,
    /// reconnect delays (P2-1-Fix 1: tests use a fake clock instead of real waiting times).
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Loads the registries for the search when connecting.</summary>
    public bool LoadRegistries { get; init; } = true;

    /// <summary>This long a tile shows the expected state after an interaction (M4).</summary>
    public TimeSpan OptimisticTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Media players (M6-Fix 1): service call ok, but after <see cref="OptimisticTimeout"/> still no
    /// matching feedback → stay optimistic until the next state arrives, at most this long
    /// (counted from the interaction). HASS.Agent often reports play/pause/mute late.
    /// </summary>
    public TimeSpan MediaPlayerOptimisticMax { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>This long an error hint stays on the tile.</summary>
    public TimeSpan AlertDuration { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>This long a short confirmation ("Done") stays.</summary>
    public TimeSpan FlashDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Largest allowed image file (cover art).</summary>
    public int MaxImageBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Keeps the connection to Home Assistant: local first, otherwise external; reconnect with backoff;
/// heartbeat; reconnect on network change/wake-up; back to local as soon as it is reachable.
/// Subscribes only to the wanted entities. Runs entirely in <see cref="RunAsync"/>
/// (started by the module through RunBackground) – no own timers or threads.
/// </summary>
internal sealed class HaClient
{
    private readonly HaClientOptions _options;
    private readonly IModuleLogger _log;
    private readonly AsyncSignal _settingsChanged = new();
    private readonly AsyncSignal _subscriptionChanged = new();
    private readonly AsyncSignal _networkChanged = new();
    private readonly Lock _lock = new();
    private HaSettings _settings = HaSettings.Empty;
    private int _settingsVersion;
    private int _authInvalidVersion = -1;
    private IReadOnlySet<string> _wanted = new HashSet<string>();
    private IReadOnlySet<string>? _subscribedSet;
    private HaConnection? _connection;
    private bool _usingFallback;
    private HttpClient? _http;

    public HaClient(HaClientOptions options, IModuleLogger log)
    {
        _options = options;
        _log = log;
    }

    public EntityStore Store { get; } = new();

    public EntityIndex Index { get; } = new();

    public HaStatus Status { get; private set; } = new(HaConnectionState.NotConfigured);

    public Uri? ConnectedUrl => _connection?.BaseUrl;

    public string? HaVersion { get; private set; }

    public DateTimeOffset? ConnectedSince { get; private set; }

    public int SubscribedCount => Store.Count;

    public int WantedCount => _wanted.Count;

    public HaClientOptions Options => _options;

    /// <summary>Wanted (referenced in the layout) entities.</summary>
    public IReadOnlySet<string> Wanted
    {
        get
        {
            lock (_lock)
            {
                return _wanted;
            }
        }
    }

    /// <summary>The first states after the current subscription have arrived (only then is "missing" meaningful).</summary>
    public bool InitialStatesReceived => Volatile.Read(ref _initialReceived);

    /// <summary>Wanted entities HA does not know (only meaningful if <see cref="InitialStatesReceived"/>).</summary>
    public IReadOnlyList<string> MissingEntities =>
        [.. Wanted.Where(id => Store.Get(id) is null).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Was there a connection in this run already (for "Connecting …" vs. "Disconnected")?</summary>
    public bool EverConnected { get; private set; }

    public bool IsConnected => Status.State is HaConnectionState.ConnectedLocal or HaConnectionState.ConnectedExternal;

    /// <summary>Token and at least one URL entered.</summary>
    public bool IsConfigured
    {
        get
        {
            lock (_lock)
            {
                return _settings.IsConfigured;
            }
        }
    }

    /// <summary>HA user of the token (from <c>auth/current_user</c>), null = unknown.</summary>
    public string? UserName { get; private set; }

    /// <summary>Is the user an admin? null = unknown (not connected yet or query failed).</summary>
    public bool? IsAdmin { get; private set; }

    /// <summary>
    /// Awaited after every successful connect (index loaded, subscription in place) inside the
    /// connection loop – e.g. for subscriptions that depend on the connection (layout).
    /// </summary>
    public event Func<CancellationToken, Task>? SessionStarted;

    private bool _initialReceived;

    public string? LastError { get; private set; }

    /// <summary>Number of connection attempts (for tests/diagnostics).</summary>
    public int ConnectAttempts => Volatile.Read(ref _attempts);

    private int _attempts;

    public event Action<HaStatus>? StatusChanged;

    public void UpdateSettings(HaSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            _settingsVersion++;
        }

        // Matching status right away, not only once the connection loop runs.
        if (settings.IsConfigured && Status.State is HaConnectionState.NotConfigured or HaConnectionState.Stopped)
        {
            SetStatus(new HaStatus(HaConnectionState.Connecting));
        }

        _settingsChanged.Set();
    }

    public void SetWantedEntities(IReadOnlySet<string> entityIds)
    {
        lock (_lock)
        {
            if (_wanted.SetEquals(entityIds))
            {
                return;
            }

            _wanted = new HashSet<string>(entityIds, StringComparer.OrdinalIgnoreCase);
        }

        _subscriptionChanged.Set();
    }

    /// <summary>Network change/wake-up reported (debounced).</summary>
    public void NotifyNetworkChanged() => _networkChanged.Set();

    public async Task RunAsync(CancellationToken ct)
    {
        // Drop leftover signals from the last run – settings/subscription are read fresh anyway.
        _settingsChanged.Reset();
        _subscriptionChanged.Reset();
        _networkChanged.Reset();
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HaSettings settings;
                int version;
                lock (_lock)
                {
                    settings = _settings;
                    version = _settingsVersion;
                }

                if (!settings.IsConfigured)
                {
                    SetStatus(new HaStatus(HaConnectionState.NotConfigured));
                    await _settingsChanged.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (_authInvalidVersion == version)
                {
                    // No constant firing: only try again when the settings change.
                    SetStatus(new HaStatus(HaConnectionState.AuthInvalid));
                    await _settingsChanged.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                SetStatus(new HaStatus(HaConnectionState.Connecting));
                HaConnection? connection = null;
                var isLocal = false;
                try
                {
                    (connection, isLocal) = await ConnectBestAsync(settings, ct).ConfigureAwait(false);
                }
                catch (HaAuthException)
                {
                    _authInvalidVersion = version;
                    LastError = HaText.T["Status_TokenInvalid"];
                    _log.Log(LogLevel.Warning, "Login rejected: token invalid. Waiting for new settings.");
                    continue;
                }

                if (connection is null)
                {
                    var delay = _options.Backoff[Math.Min(failures, _options.Backoff.Count - 1)];
                    failures++;
                    SetStatus(new HaStatus(HaConnectionState.Disconnected, delay));
                    await WaitAnyAsync(delay, ct, _settingsChanged, _networkChanged).ConfigureAwait(false);
                    continue;
                }

                failures = 0;
                var reason = await RunSessionAsync(connection, isLocal, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (reason == SessionEnd.Lost)
                {
                    // Connection lost: wait briefly (first backoff step), then reconnect.
                    var delay = _options.Backoff[0];
                    failures = 1;
                    SetStatus(new HaStatus(HaConnectionState.Disconnected, delay));
                    await WaitAnyAsync(delay, ct, _settingsChanged, _networkChanged).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await DropConnectionAsync().ConfigureAwait(false);
            SetStatus(new HaStatus(HaConnectionState.Stopped));
        }
    }

    /// <summary>Call a service (for M4 tiles and the bus), with time limit and feedback.</summary>
    public async Task<HaResult> CallServiceAsync(string domain, string service, JsonObject? data, JsonObject? target, CancellationToken ct)
    {
        var connection = _connection;
        if (connection is null)
        {
            return HaResult.Fail("not_connected", HaText.T["Error_NotConnected"]);
        }

        var command = new JsonObject
        {
            ["type"] = "call_service",
            ["domain"] = domain,
            ["service"] = service,
        };
        if (data is not null)
        {
            command["service_data"] = data.DeepClone();
        }

        if (target is not null)
        {
            command["target"] = target.DeepClone();
        }

        var result = await connection.SendCommandAsync(command, _options.CommandTimeout, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            _log.Log(LogLevel.Warning, $"call_service {domain}.{service} failed: {result.ErrorCode} {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>Send any command over the current connection (error responses do not throw).</summary>
    public Task<HaResult> SendCommandAsync(JsonObject command, CancellationToken ct) =>
        _connection is { } connection
            ? connection.SendCommandAsync(command, _options.CommandTimeout, ct)
            : Task.FromResult(HaResult.Fail("not_connected", HaText.T["Error_NotConnected"]));

    /// <summary>
    /// HTTP POST with JSON to the active base URL (with token), e.g. the mobile app registration (M6).
    /// Returns the status (0 = not connected/error) and the response.
    /// </summary>
    public async Task<(int Status, JsonNode? Body)> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        var connection = _connection;
        string? token;
        lock (_lock)
        {
            token = _settings.Token;
        }

        if (connection is null || token is null)
        {
            return (0, null);
        }

        try
        {
            _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.BaseUrl.AbsoluteUri.TrimEnd('/') + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) : null;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return ((int)response.StatusCode, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _log.Log(LogLevel.Info, $"POST {path} fehlgeschlagen: {ex.GetType().Name}");
            return (0, null);
        }
    }

    /// <summary>Forget a subscription locally only (e.g. Assist run finished) – without a command to HA.</summary>
    public void ForgetSubscription(int subscriptionId) => _connection?.ForgetSubscription(subscriptionId);

    /// <summary>Subscription over the current connection (ends with the connection).</summary>
    public Task<(HaResult Result, int SubscriptionId)> SubscribeAsync(JsonObject command, Action<JsonNode> onEvent, CancellationToken ct) =>
        _connection is { } connection
            ? connection.SubscribeAsync(command, onEvent, _options.CommandTimeout, ct)
            : Task.FromResult((HaResult.Fail("not_connected", HaText.T["Error_NotConnected"]), -1));

    /// <summary>
    /// Briefly wait for the connection (e.g. at startup, while "Connecting …"). Returns false
    /// immediately if not set up, the token is invalid or the attempt failed.
    /// </summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            if (IsConnected && _connection is not null && Volatile.Read(ref _sessionReady))
            {
                return true;
            }

            if (Status.State is not (HaConnectionState.Connecting or HaConnectionState.ConnectedLocal or HaConnectionState.ConnectedExternal)
                || timeoutCts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(100, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private bool _sessionReady;

    /// <summary>The session loop runs (connected, everything loaded) – for tests that need a defined state.</summary>
    internal bool IsSessionReady => Volatile.Read(ref _sessionReady);

    /// <summary>
    /// A network change is being debounced right now (the debounce wait is armed) – for tests that drive the
    /// clock: they move it only once the client really waits for it.
    /// </summary>
    internal bool IsDebouncingNetworkChange => Volatile.Read(ref _debouncing);

    private bool _debouncing;

    /// <summary>State of a (not subscribed) entity on demand (REST /api/states/&lt;id&gt;).</summary>
    public async Task<HaEntityState?> FetchStateAsync(string entityId, CancellationToken ct)
    {
        if (Store.Get(entityId) is { } known)
        {
            return known;
        }

        var connection = _connection;
        string? token;
        lock (_lock)
        {
            token = _settings.Token;
        }

        if (connection is null || token is null)
        {
            return null;
        }

        try
        {
            _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUrl.AbsoluteUri.TrimEnd('/') + "/api/states/" + Uri.EscapeDataString(entityId)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return json is JsonObject obj
                ? new HaEntityState(entityId, obj["state"]?.ToString() ?? "unknown", obj["attributes"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : new JsonObject(), null)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Load an image (entity_picture) through the active base URL. The token only goes to the own
    /// HA server and only if the URL does not contain its own <c>token=</c>; foreign hosts get
    /// no header. The log only shows the path without query (the proxy token is in the query).
    /// </summary>
    public async Task<(byte[] Data, string? ContentType)?> FetchImageAsync(string picture, CancellationToken ct)
    {
        var connection = _connection;
        string? token;
        lock (_lock)
        {
            token = _settings.Token;
        }

        if (connection is null || string.IsNullOrWhiteSpace(picture))
        {
            return null;
        }

        var baseUrl = connection.BaseUrl;
        if (!Uri.TryCreate(picture, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            if (!picture.StartsWith('/') || !Uri.TryCreate(baseUrl, picture, out url))
            {
                return null;
            }
        }

        var ownServer = string.Equals(url.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) && url.Port == baseUrl.Port;
        var hasOwnToken = url.Query.Contains("token=", StringComparison.OrdinalIgnoreCase);
        try
        {
            _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (ownServer && !hasOwnToken && token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Log(LogLevel.Info, $"Image not loaded ({(int)response.StatusCode}): {SafePath(url)}");
                return null;
            }

            if (response.Content.Headers.ContentLength > _options.MaxImageBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxImageBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            if (!ct.IsCancellationRequested)
            {
                _log.Log(LogLevel.Info, $"Image not loaded ({ex.GetType().Name}): {SafePath(url)}");
            }

            return null;
        }
    }

    /// <summary>Host + path, without query/credentials – for the log.</summary>
    internal static string SafePath(Uri url) => $"{url.Host}{url.AbsolutePath}";

    /// <summary>Check settings without disturbing the running connection (for "Test connection").</summary>
    public static async Task<(bool Reachable, bool TokenValid, string? Version, TimeSpan Elapsed, string? Error)> ProbeAsync(
        Uri url, string token, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await HaConnection.ConnectAsync(url, token, timeout, ct).ConfigureAwait(false);
            return (true, true, connection.HaVersion, sw.Elapsed, null);
        }
        catch (HaAuthException)
        {
            return (true, false, null, sw.Elapsed, HaText.T["Status_TokenInvalid"]);
        }
        catch (HaConnectException ex)
        {
            return (false, false, null, sw.Elapsed, ex.Message);
        }
    }

    // ------------------------------------------------------------------ internal

    private enum SessionEnd
    {
        Lost,
        Reconnect,
    }

    private async Task<(HaConnection? Connection, bool IsLocal)> ConnectBestAsync(HaSettings settings, CancellationToken ct)
    {
        HaAuthException? authError = null;
        foreach (var (url, isLocal) in settings.Candidates())
        {
            Interlocked.Increment(ref _attempts);
            try
            {
                var timeout = isLocal ? _options.LocalTimeout : _options.ExternalTimeout;
                var connection = await HaConnection.ConnectAsync(url, settings.Token!, timeout, ct).ConfigureAwait(false);
                return (connection, isLocal);
            }
            catch (HaAuthException ex)
            {
                authError = ex;
                break; // same HA, same token – do not bother the other URL as well
            }
            catch (HaConnectException ex)
            {
                LastError = $"{(isLocal ? HaText.T["Where_Local"] : HaText.T["Where_External"])} ({HaSettings.Describe(url)}): {ex.Message}";
                _log.Log(LogLevel.Info, $"Not reachable: {LastError}");
            }
        }

        if (authError is not null)
        {
            throw authError;
        }

        return (null, false);
    }

    private async Task<SessionEnd> RunSessionAsync(HaConnection connection, bool isLocal, CancellationToken ct)
    {
        _connection = connection;
        _subscribedSet = null;
        HaVersion = connection.HaVersion;
        var where = isLocal ? "local" : "external";
        _log.Log(LogLevel.Info, $"Connected ({where}, {HaSettings.Describe(connection.BaseUrl)}), HA {connection.HaVersion}");

        try
        {
            await LoadUserAsync(connection, ct).ConfigureAwait(false);
            if (_options.LoadRegistries)
            {
                await LoadIndexAsync(connection, ct).ConfigureAwait(false);
            }

            var subscription = await SubscribeAsync(connection, null, ct).ConfigureAwait(false);
            ConnectedSince = DateTimeOffset.Now;
            EverConnected = true;
            LastError = null;
            SetStatus(new HaStatus(isLocal ? HaConnectionState.ConnectedLocal : HaConnectionState.ConnectedExternal));

            foreach (var hook in SessionStarted?.GetInvocationList().Cast<Func<CancellationToken, Task>>() ?? [])
            {
                try
                {
                    await hook(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Log(LogLevel.Warning, $"Error after connecting: {ex.Message}", ex);
                }
            }

            Volatile.Write(ref _sessionReady, true);

            var clock = _options.Time;
            var nextPing = clock.GetUtcNow() + _options.PingInterval;
            var nextLocalCheck = clock.GetUtcNow() + _options.LocalRecheckInterval;
            while (!ct.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                var until = nextPing;
                if (!isLocal && nextLocalCheck < until)
                {
                    until = nextLocalCheck;
                }

                var wait = until - now;
                var tickStart = clock.GetTimestamp();
                var woke = await WaitAnyAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, ct, connection.Completion, _subscriptionChanged, _settingsChanged, _networkChanged)
                    .ConfigureAwait(false);

                if (connection.Completion.IsCompleted)
                {
                    _log.Log(LogLevel.Warning, "Connection to Home Assistant lost");
                    return SessionEnd.Lost;
                }

                if (woke == _settingsChanged)
                {
                    _log.Log(LogLevel.Info, "Settings changed – reconnecting");
                    return SessionEnd.Reconnect;
                }

                if (woke == _subscriptionChanged)
                {
                    bool same;
                    lock (_lock)
                    {
                        same = _subscribedSet is not null && _subscribedSet.SetEquals(_wanted);
                    }

                    if (!same)
                    {
                        subscription = await SubscribeAsync(connection, subscription, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                if (woke == _networkChanged)
                {
                    if (await HandleNetworkChangeAsync(connection, isLocal, ct).ConfigureAwait(false) is { } end)
                    {
                        return end;
                    }

                    continue;
                }

                // Time reached: detect waking up from standby (waiting time far too long).
                var slept = clock.GetElapsedTime(tickStart) > wait + TimeSpan.FromSeconds(15)
                    || clock.GetUtcNow() - until > TimeSpan.FromSeconds(15);

                if (clock.GetUtcNow() >= nextPing || slept)
                {
                    nextPing = clock.GetUtcNow() + _options.PingInterval;
                    if (!await connection.PingAsync(_options.PongTimeout, ct, clock).ConfigureAwait(false))
                    {
                        _log.Log(LogLevel.Warning, $"No pong within {_options.PongTimeout.TotalSeconds:0} s – connection considered dead");
                        return SessionEnd.Lost;
                    }
                }

                if (!isLocal && clock.GetUtcNow() >= nextLocalCheck)
                {
                    nextLocalCheck = clock.GetUtcNow() + _options.LocalRecheckInterval;
                    if (await LocalReachableAsync(ct).ConfigureAwait(false))
                    {
                        _log.Log(LogLevel.Info, "Local address reachable again – switching to local");
                        return SessionEnd.Reconnect;
                    }
                }
            }

            return SessionEnd.Reconnect;
        }
        finally
        {
            await DropConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Handle network changes debounced: only reconnect if needed.</summary>
    private async Task<SessionEnd?> HandleNetworkChangeAsync(HaConnection connection, bool isLocal, CancellationToken ct)
    {
        // Combine further events within the debounce time.
        _log.Log(LogLevel.Debug, $"Network change: waiting {Seconds(_options.NetworkDebounce)} for more");
        try
        {
            while (await WaitAnyAsync(_options.NetworkDebounce, ct, _networkChanged, onArmed: () => Volatile.Write(ref _debouncing, true)).ConfigureAwait(false) == _networkChanged)
            {
            }
        }
        finally
        {
            Volatile.Write(ref _debouncing, false);
        }

        if (connection.Completion.IsCompleted || !await connection.PingAsync(_options.PongTimeout, ct, _options.Time).ConfigureAwait(false))
        {
            _log.Log(LogLevel.Info, "Network change: connection no longer responds – reconnecting");
            return SessionEnd.Lost;
        }

        if (!isLocal && await LocalReachableAsync(ct).ConfigureAwait(false))
        {
            _log.Log(LogLevel.Info, "Network change: local reachable – switching to local");
            return SessionEnd.Reconnect;
        }

        _log.Log(LogLevel.Debug, "Network change: connection still responds – staying connected");
        return null;
    }

    private async Task<bool> LocalReachableAsync(CancellationToken ct)
    {
        HaSettings settings;
        lock (_lock)
        {
            settings = _settings;
        }

        if (!settings.PreferLocal || settings.LocalUrl is null || settings.Token is null)
        {
            return false;
        }

        var probe = await ProbeAsync(settings.LocalUrl, settings.Token, _options.LocalTimeout, ct).ConfigureAwait(false);
        return probe.TokenValid;
    }

    /// <summary>Who is logged in (name, admin)? If the answer is missing, it stays "unknown" – no abort.</summary>
    private async Task LoadUserAsync(HaConnection connection, CancellationToken ct)
    {
        var user = await connection.SendCommandAsync(new JsonObject { ["type"] = "auth/current_user" }, _options.CommandTimeout, ct).ConfigureAwait(false);
        if (user is { Success: true, Result: JsonObject info })
        {
            UserName = info["name"]?.ToString();
            IsAdmin = info["is_admin"] is JsonValue v && v.TryGetValue<bool>(out var admin) ? admin : null;
            _log.Log(LogLevel.Info, $"User: {UserName ?? "?"} (admin: {IsAdmin?.ToString() ?? "unknown"})");
        }
        else
        {
            UserName = null;
            IsAdmin = null;
            _log.Log(LogLevel.Info, $"User not queryable: {user.ErrorCode} {user.ErrorMessage}");
        }
    }

    internal static string AdminText(bool? isAdmin) => isAdmin switch
    {
        true => HaText.T["User_Admin"],
        false => HaText.T["User_NoAdmin"],
        _ => HaText.T["User_RightsUnknown"],
    };

    /// <summary>
    /// Names/areas for search and first start (M3; non-admin fallbacks M5b). According to the HA source
    /// 2026.10 the lists of the entity, device and area registry are allowed for all users –
    /// still every answer is checked on its own: if the full entity registry is missing, the short form
    /// <c>list_for_display</c> follows, then only the states. Without areas everything continues, just without
    /// area names (search) or with device kinds instead of areas (first start).
    /// </summary>
    private async Task LoadIndexAsync(HaConnection connection, CancellationToken ct)
    {
        // Read large answers directly into lean records (no JsonNode tree, M5-Fix 1).
        var timeout = _options.CommandTimeout;
        var (devicesResult, devices) = await connection.SendCommandAsync(
            new JsonObject { ["type"] = "config/device_registry/list" }, EntityIndex.ParseDevices, timeout, ct).ConfigureAwait(false);
        var (areasResult, areas) = await connection.SendCommandAsync(
            new JsonObject { ["type"] = "config/area_registry/list" }, EntityIndex.ParseAreas, timeout, ct).ConfigureAwait(false);
        var (entitiesResult, entities) = await connection.SendCommandAsync(
            new JsonObject { ["type"] = "config/entity_registry/list" }, EntityIndex.ParseRegistry, timeout, ct).ConfigureAwait(false);

        if (!devicesResult.Success)
        {
            _log.Log(LogLevel.Info, $"Device registry not readable ({devicesResult.ErrorCode}) – areas only directly on the entities");
        }

        if (!areasResult.Success)
        {
            _log.Log(LogLevel.Info, $"Areas not readable ({areasResult.ErrorCode}) – search without area, first start by device kinds");
        }

        var source = EntityIndexSource.Registry;
        if (!entitiesResult.Success || entities is null)
        {
            _log.Log(LogLevel.Info, $"Entity registry not readable ({entitiesResult.ErrorCode} {entitiesResult.ErrorMessage}) – trying list_for_display");
            var (displayResult, display) = await connection.SendCommandAsync(
                new JsonObject { ["type"] = "config/entity_registry/list_for_display" }, EntityIndex.ParseDisplay, timeout, ct).ConfigureAwait(false);
            source = EntityIndexSource.Display;
            entities = display;
            if (!displayResult.Success || display is null)
            {
                _log.Log(LogLevel.Info, $"list_for_display not readable ({displayResult.ErrorCode}) – names from the states");
                var (statesResult, states) = await connection.SendCommandAsync(
                    new JsonObject { ["type"] = "get_states" }, EntityIndex.ParseStates, timeout, ct).ConfigureAwait(false);
                if (!statesResult.Success || states is null)
                {
                    _log.Log(LogLevel.Warning, $"The states are not readable either ({statesResult.ErrorCode}) – search stays empty");
                    return;
                }

                source = EntityIndexSource.States;
                entities = states;
            }
        }

        Index.Load(entities!, devicesResult.Success ? devices : null, areasResult.Success ? areas : null, source);
        _log.Log(LogLevel.Info, $"Registry loaded ({Index.Source}): {Index.Count} entities, {Index.AreaCount} areas");
    }

    internal static string IndexSourceText(EntityIndexSource source) => source switch
    {
        EntityIndexSource.Registry => HaText.T["Index_Registry"],
        EntityIndexSource.Display => HaText.T["Index_Display"],
        EntityIndexSource.States => HaText.T["Index_States"],
        _ => "–",
    };

    /// <summary>
    /// subscribe_entities with an entity_ids filter (create anew; cancel the old subscription).
    /// Fallback if the command is missing: get_states + subscribe_events(state_changed), filtered.
    /// </summary>
    private async Task<int?> SubscribeAsync(HaConnection connection, int? previous, CancellationToken ct)
    {
        IReadOnlySet<string> wanted;
        lock (_lock)
        {
            wanted = _wanted;
            _subscribedSet = wanted;
        }

        if (previous is { } old)
        {
            await connection.UnsubscribeAsync(old, _options.CommandTimeout, ct).ConfigureAwait(false);
        }

        Store.RetainOnly(wanted);
        Volatile.Write(ref _initialReceived, false);
        if (wanted.Count == 0)
        {
            Store.Clear();
            Volatile.Write(ref _initialReceived, true);
            return null;
        }

        if (!_usingFallback)
        {
            var ids = new JsonArray([.. wanted.Order(StringComparer.OrdinalIgnoreCase).Select(id => (JsonNode)JsonValue.Create(id))]);
            var (result, subscriptionId) = await connection.SubscribeAsync(
                new JsonObject { ["type"] = "subscribe_entities", ["entity_ids"] = ids },
                evt =>
                {
                    Store.ApplyCompressed(evt, DateTimeOffset.Now);
                    Volatile.Write(ref _initialReceived, true);
                },
                _options.CommandTimeout,
                ct).ConfigureAwait(false);
            if (result.Success)
            {
                _log.Log(LogLevel.Info, $"{wanted.Count} entities subscribed");
                return subscriptionId;
            }

            if (result.ErrorCode is not ("unknown_command" or "invalid_format"))
            {
                _log.Log(LogLevel.Warning, $"subscribe_entities failed: {result.ErrorCode} {result.ErrorMessage}");
                return null;
            }

            _usingFallback = true;
            _log.Log(LogLevel.Info, "subscribe_entities not supported – using get_states + state_changed");
        }

        var (eventsResult, eventsId) = await connection.SubscribeAsync(
            new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "state_changed" },
            evt =>
            {
                var data = evt["data"];
                var id = data?["entity_id"]?.ToString();
                if (id is null || !wanted.Contains(id))
                {
                    return;
                }

                if (data!["new_state"] is JsonObject newState)
                {
                    Store.ApplyFull(newState, DateTimeOffset.Now);
                }
                else
                {
                    Store.Remove(id, DateTimeOffset.Now);
                }
            },
            _options.CommandTimeout,
            ct).ConfigureAwait(false);

        var states = await connection.SendCommandAsync(new JsonObject { ["type"] = "get_states" }, _options.CommandTimeout, ct).ConfigureAwait(false);
        foreach (var state in (states.Result as JsonArray) ?? new JsonArray())
        {
            if (state is JsonObject obj && obj["entity_id"]?.ToString() is { } id && wanted.Contains(id))
            {
                Store.ApplyFull(obj, DateTimeOffset.Now);
            }
        }

        Volatile.Write(ref _initialReceived, true);
        return eventsResult.Success ? eventsId : null;
    }

    private async Task DropConnectionAsync()
    {
        Volatile.Write(ref _sessionReady, false);
        var connection = Interlocked.Exchange(ref _connection, null);
        ConnectedSince = null;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetStatus(HaStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>Waits for time, a task or one of the signals. Returns what woke it (or null = time).</summary>
    private Task<object?> WaitAnyAsync(TimeSpan delay, CancellationToken ct, params object[] wakers) => WaitAnyAsync(delay, ct, wakers, null);

    private Task<object?> WaitAnyAsync(TimeSpan delay, CancellationToken ct, object waker, Action onArmed) => WaitAnyAsync(delay, ct, [waker], onArmed);

    /// <param name="onArmed">Called once the time wait is registered with the clock.</param>
    private async Task<object?> WaitAnyAsync(TimeSpan delay, CancellationToken ct, object[] wakers, Action? onArmed)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<(object Source, Task Task)> { (null!, Task.Delay(delay, _options.Time, delayCts.Token)) };
        onArmed?.Invoke();
        foreach (var waker in wakers)
        {
            tasks.Add((waker, waker switch
            {
                AsyncSignal signal => signal.WaitAsync(delayCts.Token),
                Task task => task,
                _ => throw new ArgumentException("Unknown waker"),
            }));
        }

        var finished = await Task.WhenAny(tasks.Select(t => t.Task)).ConfigureAwait(false);
        delayCts.Cancel();
        ct.ThrowIfCancellationRequested();
        return tasks.First(t => t.Task == finished).Source;
    }

    internal static string Seconds(TimeSpan t) => t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms" : $"{t.TotalSeconds:0} s";
}

/// <summary>Simple auto-reset signal for async code.</summary>
internal sealed class AsyncSignal
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _set;

    public void Set()
    {
        lock (_lock)
        {
            _set = true;
            _tcs.TrySetResult();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _set = false;
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        Task wait;
        lock (_lock)
        {
            if (_set)
            {
                _set = false;
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return;
            }

            wait = _tcs.Task;
        }

        await wait.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            _set = false;
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
