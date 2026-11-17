// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// Minimal Home Assistant WebSocket server for tests (TcpListener + own HTTP upgrade +
/// WebSocket.CreateFromStream). Supports auth, ping, subscribe_entities (incl. deltas),
/// subscribe_events, get_states, registries (incl. list_for_display), call_service,
/// auth/current_user, frontend/*_user_data (per token = per user) and targeted disturbances –
/// also a non-admin who is refused single commands with "unauthorized" (M5b).
/// </summary>
internal sealed class FakeHaServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Session> _sessions = [];
    private readonly Lock _lock = new();

    public FakeHaServer(string validToken = "good-token", string version = "2026.10.1")
    {
        ValidToken = validToken;
        Version = version;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public string ValidToken { get; set; }

    public string Version { get; }

    public Uri BaseUrl => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    /// <summary>false = close TCP connections right away (server "gone").</summary>
    public bool AcceptConnections { get; set; } = true;

    public bool RespondToPing { get; set; } = true;

    public bool SupportsSubscribeEntities { get; set; } = true;

    public bool SupportsUserDataSubscribe { get; set; } = true;

    public string UserName { get; set; } = "Alex";

    public bool IsAdmin { get; set; } = true;

    /// <summary>Commands refused with "unauthorized" (like an HA with a restricted user).</summary>
    public HashSet<string> Unauthorized { get; } = new(StringComparer.Ordinal);

    /// <summary>Areas of the area registry (default: one room).</summary>
    public List<JsonObject> Areas { get; } = [new JsonObject { ["area_id"] = "zimmer", ["name"] = "Office" }];

    /// <summary>Fields that override the generated entity registry entry (area_id, entity_category, hidden_by …).</summary>
    public ConcurrentDictionary<string, JsonObject> RegistryOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>frontend/user_data per token (= per HA user): key → value.</summary>
    public ConcurrentDictionary<string, ConcurrentDictionary<string, JsonNode?>> UserData { get; } = new();

    // ------------------------------------------------------------------ Mobile app (M6)

    /// <summary>Registrations: webhook ID → (the user's token, request).</summary>
    public ConcurrentDictionary<string, (string Token, JsonObject Body)> MobileRegistrations { get; } = new();

    public int RegistrationRequests => Volatile.Read(ref _registrationRequests);

    private int _registrationRequests;

    /// <summary>Confirmed confirm_ids.</summary>
    public ConcurrentQueue<string> Confirmed { get; } = new();

    /// <summary>Registration "deleted in HA".</summary>
    public void DeleteRegistration(string webhookId) => MobileRegistrations.TryRemove(webhookId, out _);

    /// <summary>Notification to the push channel (like notify.mobile_app_…).</summary>
    public Task PushNotificationAsync(string webhookId, string title, string message, JsonObject? data = null) =>
        ForEachSession(s => s.PushMobile(webhookId, new JsonObject
        {
            ["message"] = message,
            ["title"] = title,
            ["data"] = data?.DeepClone(),
            ["hass_confirm_id"] = Guid.NewGuid().ToString("N"),
        }));

    // ------------------------------------------------------------------ Assist (M6)

    public enum AssistBehavior
    {
        Reply,
        Error,
        Silent,
        Unknown,
    }

    public AssistBehavior Assist { get; set; } = AssistBehavior.Reply;

    /// <summary>Answer of <c>assist_pipeline/pipeline/list</c> (R1); null = command unknown like an old HA.</summary>
    public JsonObject? Pipelines { get; set; }

    /// <summary>Answer to a question (default: "Antwort auf …").</summary>
    public Func<string, string> AssistReply { get; set; } = text => $"Antwort auf {text}";

    /// <summary>Value from frontend/user_data of the default token (or null).</summary>
    public JsonNode? GetUserData(string key, string? token = null) =>
        UserData.TryGetValue(token ?? ValidToken, out var data) && data.TryGetValue(key, out var value) ? value?.DeepClone() : null;

    /// <summary>Set a value as if from another device/session (incl. notifying the subscriptions).</summary>
    public Task SetUserDataAsync(string key, JsonNode? value, string? token = null)
    {
        token ??= ValidToken;
        UserData.GetOrAdd(token, _ => new())[key] = value?.DeepClone();
        return ForEachSession(s => s.PushUserData(token, key, value));
    }

    public int Connections => Volatile.Read(ref _connections);

    public int AuthAttempts => Volatile.Read(ref _authAttempts);

    /// <summary>Complete states in the normal HA format, key = entity_id.</summary>
    public ConcurrentDictionary<string, JsonObject> States { get; } = new();

    public ConcurrentQueue<JsonObject> Received { get; } = new();

    public Func<JsonObject, JsonObject?>? CallServiceHandler { get; set; }

    /// <summary>Simple HTTP GETs (images, REST): path+query → (status, content, content type).</summary>
    public Func<string, (int Status, byte[] Body, string ContentType)>? HttpHandler { get; set; }

    /// <summary>Received HTTP GETs: path+query and Authorization header (or null).</summary>
    public ConcurrentQueue<(string PathAndQuery, string? Authorization)> HttpRequests { get; } = new();

    private int _connections;
    private int _authAttempts;

    public IEnumerable<JsonObject> ReceivedOfType(string type) => Received.Where(m => m["type"]?.ToString() == type);

    public void AddState(string entityId, string state, string? friendlyName = null, JsonObject? attributes = null)
    {
        var attrs = attributes ?? [];
        if (friendlyName is not null)
        {
            attrs["friendly_name"] = friendlyName;
        }

        States[entityId] = new JsonObject
        {
            ["entity_id"] = entityId,
            ["state"] = state,
            ["attributes"] = attrs,
            ["last_changed"] = "2026-11-05T12:00:00+00:00",
        };
    }

    /// <summary>Change a state and send it as a compact delta (state + all attributes) to the subscriptions – like HA.</summary>
    public Task SetStateAsync(string entityId, string state, JsonObject? attributes = null)
    {
        var attrs = attributes ?? (States.TryGetValue(entityId, out var old) ? (JsonObject)old["attributes"]!.DeepClone() : []);
        States[entityId] = new JsonObject
        {
            ["entity_id"] = entityId,
            ["state"] = state,
            ["attributes"] = attrs.DeepClone(),
            ["last_changed"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        return PushEntitiesEventAsync(new JsonObject
        {
            ["c"] = new JsonObject { [entityId] = new JsonObject { ["+"] = new JsonObject { ["s"] = state, ["a"] = attrs.DeepClone() } } },
        });
    }

    /// <summary>Send a compact event to all subscribe_entities subscriptions.</summary>
    public Task PushEntitiesEventAsync(JsonObject compressedEvent) => ForEachSession(s => s.PushToSubscriptions("subscribe_entities", compressedEvent));

    /// <summary>Send state_changed to all subscribe_events subscriptions.</summary>
    public Task PushStateChangedAsync(string entityId, string newState)
    {
        var data = new JsonObject
        {
            ["event_type"] = "state_changed",
            ["data"] = new JsonObject
            {
                ["entity_id"] = entityId,
                ["new_state"] = new JsonObject { ["entity_id"] = entityId, ["state"] = newState, ["attributes"] = new JsonObject() },
            },
        };
        return ForEachSession(s => s.PushToSubscriptions("subscribe_events", data));
    }

    /// <summary>Hard abort all open connections (like an HA restart/network gone).</summary>
    public void DropAll()
    {
        lock (_lock)
        {
            foreach (var s in _sessions)
            {
                s.Abort();
            }

            _sessions.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropAll();
        _cts.Dispose();
    }

    /// <summary>Entity registry from the states (area "zimmer" for everything with "desk") + overrides.</summary>
    private IEnumerable<JsonObject> RegistryEntries()
    {
        foreach (var s in States.Values)
        {
            var entityId = s["entity_id"]!.ToString();
            var entry = new JsonObject
            {
                ["entity_id"] = entityId,
                ["name"] = null,
                ["original_name"] = s["attributes"]?["friendly_name"]?.DeepClone(),
                ["area_id"] = entityId.Contains("desk", StringComparison.Ordinal) ? "zimmer" : null,
                ["device_id"] = null,
                ["disabled_by"] = null,
                ["entity_category"] = null,
                ["hidden_by"] = null,
            };
            if (RegistryOverrides.TryGetValue(entityId, out var extra))
            {
                foreach (var (k, v) in extra)
                {
                    entry[k] = v?.DeepClone();
                }
            }

            yield return entry;
        }
    }

    private async Task ForEachSession(Func<Session, Task> action)
    {
        Session[] sessions;
        lock (_lock)
        {
            sessions = [.. _sessions];
        }

        foreach (var s in sessions)
        {
            await action(s);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            if (!AcceptConnections)
            {
                client.Close();
                continue;
            }

            Interlocked.Increment(ref _connections);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var (head, rest) = await ReadHeadAsync(stream);
            var lines = head.Split("\r\n");
            var keyLine = lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            if (keyLine is null)
            {
                await ServeHttpAsync(stream, lines, rest);
                client.Close();
                return;
            }

            var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            var session = new Session(this, socket, client);
            await session.RunAsync(_cts.Token);
        }
        catch (Exception)
        {
            client.Close();
        }
    }

    private async Task ServeHttpAsync(NetworkStream stream, string[] lines, byte[] rest)
    {
        var method = lines[0].Split(' ')[0];
        var path = lines[0].Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
        var auth = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) is { } a
            ? a[(a.IndexOf(':') + 1)..].Trim()
            : null;
        HttpRequests.Enqueue((path, auth));

        (int Status, byte[] Body, string ContentType) reply;
        if (method == "POST")
        {
            var length = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } cl
                ? int.Parse(cl[(cl.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var buffer = new byte[length];
            rest.AsSpan(0, Math.Min(rest.Length, length)).CopyTo(buffer);
            var read = Math.Min(rest.Length, length);
            while (read < length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), _cts.Token);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            reply = HandlePost(path, auth, JsonNode.Parse(buffer) as JsonObject);
        }
        else
        {
            reply = HttpHandler?.Invoke(path) ?? (404, Encoding.UTF8.GetBytes("not found"), "text/plain");
        }

        var (status, body, type) = reply;
        var header = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
        await stream.WriteAsync(body, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    private (int Status, byte[] Body, string ContentType) HandlePost(string path, string? auth, JsonObject? body)
    {
        var token = auth?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? auth[7..] : null;
        if (token != ValidToken)
        {
            return (401, Encoding.UTF8.GetBytes("{\"message\":\"Unauthorized\"}"), "application/json");
        }

        if (path == "/api/mobile_app/registrations" && body is not null)
        {
            Interlocked.Increment(ref _registrationRequests);
            var webhook = $"wh{Guid.NewGuid():N}";
            MobileRegistrations[webhook] = (token, body);
            return (201, Encoding.UTF8.GetBytes(new JsonObject { ["webhook_id"] = webhook, ["secret"] = null, ["cloudhook_url"] = null }.ToJsonString()), "application/json");
        }

        // Webhook of the mobile app (M7: update_registration renames the device)
        if (path.StartsWith("/api/webhook/", StringComparison.Ordinal) && body is not null)
        {
            var hook = path["/api/webhook/".Length..];
            if (!MobileRegistrations.TryGetValue(hook, out var registration))
            {
                return (410, Encoding.UTF8.GetBytes("{}"), "application/json");
            }

            if (body["type"]?.ToString() == "update_registration" && body["data"] is JsonObject data)
            {
                var updated = (JsonObject)registration.Body.DeepClone();
                foreach (var (key, value) in data)
                {
                    updated[key] = value?.DeepClone();
                }

                MobileRegistrations[hook] = (registration.Token, updated);
                return (200, Encoding.UTF8.GetBytes(updated.ToJsonString()), "application/json");
            }

            return (200, Encoding.UTF8.GetBytes("{}"), "application/json");
        }

        return (404, Encoding.UTF8.GetBytes("not found"), "text/plain");
    }

    private static async Task<(string Head, byte[] Remainder)> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                throw new IOException("Connection closed during the handshake");
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                return (text[..(end - 4)], buffer[end..total]);
            }
        }
    }

    private sealed class Session(FakeHaServer server, WebSocket socket, TcpClient client)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly ConcurrentDictionary<int, (string Type, HashSet<string>? Filter)> _subscriptions = new();
        private string _token = "";

        public async Task PushMobile(string webhook, JsonObject notification)
        {
            foreach (var (id, _) in _subscriptions.Where(s => s.Value.Type == "push" && s.Value.Filter!.Contains(webhook)))
            {
                await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = notification.DeepClone() });
            }
        }

        public async Task PushUserData(string token, string key, JsonNode? value)
        {
            if (token != _token)
            {
                return; // another user
            }

            foreach (var (id, _) in _subscriptions.Where(s => s.Value.Type == "user_data" && s.Value.Filter!.Contains(key)))
            {
                await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["value"] = value?.DeepClone() } });
            }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            await SendAsync(new JsonObject { ["type"] = "auth_required", ["ha_version"] = server.Version });
            var auth = await ReceiveAsync(ct);
            Interlocked.Increment(ref server._authAttempts);
            if (auth?["access_token"]?.ToString() != server.ValidToken)
            {
                await SendAsync(new JsonObject { ["type"] = "auth_invalid", ["message"] = "Invalid access token or password" });
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth", ct);
                return;
            }

            _token = auth!["access_token"]!.ToString();
            await SendAsync(new JsonObject { ["type"] = "auth_ok", ["ha_version"] = server.Version });
            lock (server._lock)
            {
                server._sessions.Add(this);
            }

            while (!ct.IsCancellationRequested)
            {
                var message = await ReceiveAsync(ct);
                if (message is null)
                {
                    return;
                }

                server.Received.Enqueue(message);
                await HandleAsync(message);
            }
        }

        public async Task PushToSubscriptions(string type, JsonObject evt)
        {
            foreach (var (id, sub) in _subscriptions.Where(s => s.Value.Type == type))
            {
                await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = evt.DeepClone() });
            }
        }

        public void Abort()
        {
            socket.Abort();
            client.Close();
        }

        private async Task HandleAsync(JsonObject message)
        {
            var id = message["id"]!.GetValue<int>();
            var type = message["type"]?.ToString() ?? "";
            if (server.Unauthorized.Contains(type))
            {
                await Error(id, "unauthorized", "Unauthorized");
                return;
            }

            switch (type)
            {
                case "ping":
                    if (server.RespondToPing)
                    {
                        await SendAsync(new JsonObject { ["id"] = id, ["type"] = "pong" });
                    }

                    break;

                case "subscribe_entities":
                    if (!server.SupportsSubscribeEntities)
                    {
                        await Error(id, "unknown_command", "Unknown command.");
                        break;
                    }

                    var filter = (message["entity_ids"] as JsonArray)?.Select(n => n!.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _subscriptions[id] = ("subscribe_entities", filter);
                    await Result(id, null);
                    var added = new JsonObject();
                    foreach (var (entityId, state) in server.States)
                    {
                        if (filter is null || filter.Contains(entityId))
                        {
                            added[entityId] = new JsonObject
                            {
                                ["s"] = state["state"]!.DeepClone(),
                                ["a"] = state["attributes"]!.DeepClone(),
                                ["lc"] = 1762344000.0,
                            };
                        }
                    }

                    await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["a"] = added } });
                    break;

                case "subscribe_events":
                    // Like HA: non-admins only for permitted events (e.g. state_changed).
                    if (!server.IsAdmin && message["event_type"]?.ToString() is not ("state_changed" or "area_registry_updated" or "entity_registry_updated"))
                    {
                        await Error(id, "unauthorized", "Unauthorized");
                        break;
                    }

                    _subscriptions[id] = ("subscribe_events", null);
                    await Result(id, null);
                    break;

                case "unsubscribe_events":
                    _subscriptions.TryRemove(message["subscription"]!.GetValue<int>(), out _);
                    await Result(id, null);
                    break;

                case "get_states":
                    await Result(id, new JsonArray([.. server.States.Values.Select(s => (JsonNode)s.DeepClone())]));
                    break;

                case "config/entity_registry/list":
                    await Result(id, new JsonArray([.. server.RegistryEntries().Select(e => (JsonNode)e)]));
                    break;

                case "config/entity_registry/list_for_display":
                    // Short form like HA 2026.10 (disabled ones missing, category as an index).
                    var display = new JsonArray();
                    foreach (var e in server.RegistryEntries().Where(e => e["disabled_by"] is null))
                    {
                        var row = new JsonObject { ["ei"] = e["entity_id"]!.DeepClone(), ["pl"] = "test" };
                        if (e["area_id"] is { } ai)
                        {
                            row["ai"] = ai.DeepClone();
                        }

                        if (e["original_name"] is { } en)
                        {
                            row["en"] = en.DeepClone();
                        }

                        if (e["entity_category"]?.ToString() is { } category)
                        {
                            row["ec"] = category == "config" ? 0 : 1;
                        }

                        if (e["hidden_by"] is not null)
                        {
                            row["hb"] = true;
                        }

                        display.Add(row);
                    }

                    await Result(id, new JsonObject
                    {
                        ["entity_categories"] = new JsonObject { ["0"] = "config", ["1"] = "diagnostic" },
                        ["entities"] = display,
                    });
                    break;

                case "config/area_registry/list":
                    await Result(id, new JsonArray([.. server.Areas.Select(a => (JsonNode)a.DeepClone())]));
                    break;

                case "auth/current_user":
                    await Result(id, new JsonObject { ["id"] = "u1", ["name"] = server.UserName, ["is_owner"] = server.IsAdmin, ["is_admin"] = server.IsAdmin });
                    break;

                case "frontend/get_user_data":
                    var key = message["key"]!.ToString();
                    await Result(id, new JsonObject { ["value"] = server.GetUserData(key, _token) });
                    break;

                case "frontend/set_user_data":
                    await server.SetUserDataAsync(message["key"]!.ToString(), message["value"], _token);
                    await Result(id, null);
                    break;

                case "mobile_app/push_notification_channel":
                    var hook = message["webhook_id"]!.ToString();
                    if (!server.MobileRegistrations.TryGetValue(hook, out var registration))
                    {
                        await Error(id, "not_found", "Webhook ID not found");
                        break;
                    }

                    if (registration.Token != _token)
                    {
                        await Error(id, "unauthorized", "User not linked to this webhook ID");
                        break;
                    }

                    _subscriptions[id] = ("push", new HashSet<string> { hook });
                    await Result(id, null);
                    break;

                case "mobile_app/push_notification_confirm":
                    server.Confirmed.Enqueue(message["confirm_id"]!.ToString());
                    await Result(id, null);
                    break;

                case "assist_pipeline/pipeline/list" when server.Pipelines is not null:
                    await Result(id, server.Pipelines.DeepClone());
                    break;

                case "assist_pipeline/run":
                    var text = message["input"]?["text"]?.ToString() ?? "";
                    if (server.Assist == AssistBehavior.Unknown)
                    {
                        await Error(id, "unknown_command", "Unknown command.");
                        break;
                    }

                    await Result(id, null);
                    if (server.Assist == AssistBehavior.Silent)
                    {
                        break;
                    }

                    await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["type"] = "run-start", ["data"] = new JsonObject() } });
                    if (server.Assist == AssistBehavior.Error)
                    {
                        await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["type"] = "error", ["data"] = new JsonObject { ["code"] = "intent-failed", ["message"] = "LLM nicht erreichbar" } } });
                    }
                    else
                    {
                        await SendAsync(new JsonObject
                        {
                            ["id"] = id,
                            ["type"] = "event",
                            ["event"] = new JsonObject
                            {
                                ["type"] = "intent-end",
                                ["data"] = new JsonObject
                                {
                                    ["intent_output"] = new JsonObject
                                    {
                                        ["response"] = new JsonObject
                                        {
                                            ["speech"] = new JsonObject { ["plain"] = new JsonObject { ["speech"] = server.AssistReply(text) } },
                                            ["response_type"] = "action_done",
                                        },
                                        ["conversation_id"] = "conv-1",
                                    },
                                },
                            },
                        });
                    }

                    await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["type"] = "run-end", ["data"] = null } });
                    break;

                case "conversation/process":
                    await Result(id, new JsonObject
                    {
                        ["response"] = new JsonObject
                        {
                            ["speech"] = new JsonObject { ["plain"] = new JsonObject { ["speech"] = server.AssistReply(message["text"]?.ToString() ?? "") } },
                            ["response_type"] = "action_done",
                        },
                        ["conversation_id"] = "conv-2",
                    });
                    break;

                case "frontend/subscribe_user_data":
                    if (!server.SupportsUserDataSubscribe)
                    {
                        await Error(id, "unknown_command", "Unknown command.");
                        break;
                    }

                    // Like HA: first the event with the current value, then the result.
                    var subKey = message["key"]!.ToString();
                    _subscriptions[id] = ("user_data", new HashSet<string> { subKey });
                    await SendAsync(new JsonObject { ["id"] = id, ["type"] = "event", ["event"] = new JsonObject { ["value"] = server.GetUserData(subKey, _token) } });
                    await Result(id, null);
                    break;

                case "config/device_registry/list":
                    await Result(id, new JsonArray());
                    break;

                case "call_service":
                    var reply = server.CallServiceHandler?.Invoke(message);
                    if (reply?["error"] is JsonObject err)
                    {
                        await Error(id, err["code"]!.ToString(), err["message"]!.ToString());
                    }
                    else
                    {
                        await Result(id, reply ?? new JsonObject { ["context"] = new JsonObject { ["id"] = "ctx" } });
                    }

                    break;

                default:
                    await Error(id, "unknown_command", "Unknown command.");
                    break;
            }
        }

        private Task Result(int id, JsonNode? result) =>
            SendAsync(new JsonObject { ["id"] = id, ["type"] = "result", ["success"] = true, ["result"] = result });

        private Task Error(int id, string code, string message) =>
            SendAsync(new JsonObject
            {
                ["id"] = id,
                ["type"] = "result",
                ["success"] = false,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        private async Task SendAsync(JsonObject message)
        {
            await _send.WaitAsync();
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception)
            {
                // connection gone – does not matter in the test
            }
            finally
            {
                _send.Release();
            }
        }

        private async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                }
                catch (Exception)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return JsonNode.Parse(ms.ToArray()) as JsonObject;
                }
            }
        }
    }
}

/// <summary>Accepts TCP but never answers (hanging server).</summary>
internal sealed class BlackholeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = [];

    public BlackholeServer()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public Uri BaseUrl => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public void Dispose()
    {
        _listener.Stop();
        foreach (var c in _clients)
        {
            c.Close();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                _clients.Add(await _listener.AcceptTcpClientAsync());
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>A port nobody listens on for sure (the connection is refused).</summary>
    public static Uri ClosedPortUrl()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }
}
