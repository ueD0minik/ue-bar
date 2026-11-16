// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UeBar.Modules.HomeAssistant;

/// <summary>Answer to a command.</summary>
internal sealed record HaResult(bool Success, JsonNode? Result, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static HaResult Fail(string code, string message) => new(false, null, code, message);
}

/// <summary>Token rejected (auth_invalid).</summary>
internal sealed class HaAuthException(string message) : Exception(message);

/// <summary>Server not reachable / not a valid HA WebSocket.</summary>
internal sealed class HaConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// An authenticated WebSocket connection to Home Assistant (SPEC 8.1/8.2):
/// auth_required → auth → auth_ok/auth_invalid, then commands with a running ID,
/// matching of answers, subscription events, ping/pong. The token is never logged.
/// </summary>
internal sealed class HaConnection : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<int, Action<JsonNode>> _subscriptions = new();
    private readonly ConcurrentDictionary<int, Func<JsonElement, object?>> _rawParsers = new();
    private readonly ConcurrentDictionary<int, object?> _rawValues = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task _receiveLoop = Task.CompletedTask;

    private HaConnection(ClientWebSocket socket, Uri baseUrl, string haVersion)
    {
        _socket = socket;
        BaseUrl = baseUrl;
        HaVersion = haVersion;
    }

    public Uri BaseUrl { get; }

    public string HaVersion { get; }

    /// <summary>Ends when the connection is gone (or was closed).</summary>
    public Task Completion => _receiveLoop;

    /// <summary>Connect and authenticate. Throws <see cref="HaAuthException"/> or <see cref="HaConnectException"/>.</summary>
    public static async Task<HaConnection> ConnectAsync(Uri baseUrl, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // own ping/pong at the HA level
        try
        {
            await socket.ConnectAsync(HaSettings.WebSocketUri(baseUrl), timeoutCts.Token).ConfigureAwait(false);

            var first = await ReceiveJsonAsync(socket, timeoutCts.Token).ConfigureAwait(false);
            if (first?["type"]?.GetValue<string>() != "auth_required")
            {
                throw new HaConnectException(HaText.T["Error_NoWebSocket"]);
            }

            var auth = new JsonObject { ["type"] = "auth", ["access_token"] = token };
            await SendRawAsync(socket, auth, timeoutCts.Token).ConfigureAwait(false);

            var answer = await ReceiveJsonAsync(socket, timeoutCts.Token).ConfigureAwait(false);
            switch (answer?["type"]?.GetValue<string>())
            {
                case "auth_ok":
                    var version = answer["ha_version"]?.GetValue<string>() ?? first["ha_version"]?.GetValue<string>() ?? "?";
                    var connection = new HaConnection(socket, baseUrl, version);
                    connection._receiveLoop = Task.Run(connection.ReceiveLoopAsync);
                    return connection;
                case "auth_invalid":
                    socket.Abort();
                    socket.Dispose();
                    throw new HaAuthException(HaText.T["Status_TokenInvalid"]);
                default:
                    throw new HaConnectException(HaText.T["Error_UnexpectedAuthAnswer"]);
            }
        }
        catch (HaAuthException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not HaConnectException)
        {
            socket.Abort();
            socket.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new HaConnectException(timeoutCts.IsCancellationRequested ? HaText.T["Error_Timeout"] : Short(ex), ex);
        }
        catch (HaConnectException)
        {
            socket.Abort();
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Send a command and wait for the answer (with time limit). Never throws because of error answers.</summary>
    public async Task<HaResult> SendCommandAsync(JsonObject command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(command, cancellationToken).ConfigureAwait(false);
            var message = await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return ToResult(message);
        }
        catch (TimeoutException)
        {
            return HaResult.Fail("timeout", HaText.T["Error_NoAnswer"]);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            return HaResult.Fail("disconnected", HaText.T["Error_Disconnected"]);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Command with a large answer (registry, states): the result is read directly on receipt from the
    /// <see cref="JsonElement"/> (<paramref name="parse"/>, on the receive thread, keep it fast) –
    /// without a <see cref="JsonNode"/> tree, so memory stays small (M5-Fix 1).
    /// </summary>
    public async Task<(HaResult Result, T? Value)> SendCommandAsync<T>(JsonObject command, Func<JsonElement, T> parse, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rawParsers[id] = element => parse(element);
        _pending[id] = tcs;
        try
        {
            await SendAsync(command, cancellationToken).ConfigureAwait(false);
            var result = ToResult(await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false));
            return result.Success && _rawValues.TryRemove(id, out var value) && value is T typed ? (result, typed) : (result, default);
        }
        catch (TimeoutException)
        {
            return (HaResult.Fail("timeout", HaText.T["Error_NoAnswer"]), default);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            return (HaResult.Fail("disconnected", HaText.T["Error_Disconnected"]), default);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _rawParsers.TryRemove(id, out _);
            _rawValues.TryRemove(id, out _);
        }
    }

    /// <summary>Create a subscription; events go to <paramref name="onEvent"/>. Returns the subscription ID or an error.</summary>
    public async Task<(HaResult Result, int SubscriptionId)> SubscribeAsync(JsonObject command, Action<JsonNode> onEvent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        command["id"] = id;
        _subscriptions[id] = onEvent; // before sending, so no event gets lost
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(command, cancellationToken).ConfigureAwait(false);
            var result = ToResult(await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false));
            if (!result.Success)
            {
                _subscriptions.TryRemove(id, out _);
            }

            return (result, id);
        }
        catch (Exception ex) when (ex is TimeoutException or WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            _subscriptions.TryRemove(id, out _);
            return (HaResult.Fail("timeout", HaText.T["Error_NoSubscriptionAnswer"]), id);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Remove a subscription locally only (HA ends it itself, e.g. after an Assist run).</summary>
    public void ForgetSubscription(int subscriptionId) => _subscriptions.TryRemove(subscriptionId, out _);

    public async Task UnsubscribeAsync(int subscriptionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        await SendCommandAsync(new JsonObject { ["type"] = "unsubscribe_events", ["subscription"] = subscriptionId }, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>ping → pong within the time limit?</summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await SendCommandAsync(new JsonObject { ["type"] = "ping" }, timeout, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // When closing nothing matters.
        }

        _socket.Abort();
        _socket.Dispose();
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        FailPending();
        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var received = await ReceiveRawAsync(_socket, _cts.Token).ConfigureAwait(false);
                if (received is not { } raw)
                {
                    break; // server closed
                }

                try
                {
                    var bytes = raw.Buffer.AsMemory(0, raw.Length);
                    if (_rawParsers.IsEmpty || !TryDispatchRaw(bytes))
                    {
                        if (JsonNode.Parse(bytes.Span) is JsonObject message)
                        {
                            Dispatch(message);
                        }
                    }
                }
                finally
                {
                    Release(raw.Buffer);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or ObjectDisposedException or InvalidOperationException)
        {
            // Connection gone – completion ends, the client reconnects.
        }
        finally
        {
            FailPending();
        }
    }

    /// <summary>Answer to a command with its own reader? Then read directly from the JsonDocument.</summary>
    private bool TryDispatchRaw(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)
            || !_rawParsers.TryGetValue(id, out var parse)
            || !root.TryGetProperty("type", out var type) || type.ValueEquals("result") is false)
        {
            return false;
        }

        if (!_pending.TryRemove(id, out var tcs))
        {
            return true;
        }

        var success = root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        if (!success)
        {
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            tcs.TrySetResult(new JsonObject
            {
                ["type"] = "result",
                ["success"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.ToString() : "unknown",
                    ["message"] = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.ToString() : HaText.T["Assist_UnknownError"],
                },
            });
            return true;
        }

        try
        {
            _rawValues[id] = parse(root.TryGetProperty("result", out var result) ? result : default);
            tcs.TrySetResult(new JsonObject { ["type"] = "result", ["success"] = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            tcs.TrySetResult(new JsonObject
            {
                ["type"] = "result",
                ["success"] = false,
                ["error"] = new JsonObject { ["code"] = "parse_error", ["message"] = ex.Message },
            });
        }

        return true;
    }

    private void Dispatch(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        var id = message["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var i) ? i : -1;
        switch (type)
        {
            case "result" or "pong" when id >= 0:
                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(message);
                }

                break;
            case "event" when id >= 0:
                if (_subscriptions.TryGetValue(id, out var handler) && message["event"] is { } evt)
                {
                    handler(evt);
                }

                break;
        }
    }

    private void FailPending()
    {
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetResult(new JsonObject
                {
                    ["type"] = "result",
                    ["success"] = false,
                    ["error"] = new JsonObject { ["code"] = "disconnected", ["message"] = HaText.T["Error_Disconnected"] },
                });
            }
        }
    }

    private static HaResult ToResult(JsonObject message)
    {
        if (message["type"]?.GetValue<string>() == "pong")
        {
            return new HaResult(true, null);
        }

        var success = message["success"]?.GetValue<bool>() == true;
        if (success)
        {
            // Take the result out instead of copying – the message is not needed afterwards.
            var result = message["result"];
            message.Remove("result");
            return new HaResult(true, result);
        }

        var error = message["error"];
        return HaResult.Fail(
            error?["code"]?.GetValue<string>() ?? "unknown",
            error?["message"]?.GetValue<string>() ?? HaText.T["Assist_UnknownError"]);
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendRawAsync(_socket, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static Task SendRawAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>Read a (possibly fragmented) text message and return it as a JsonObject. null = connection closed.</summary>
    private static async Task<JsonObject?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        if (await ReceiveRawAsync(socket, cancellationToken).ConfigureAwait(false) is not { } raw)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw.Buffer.AsSpan(0, raw.Length)) as JsonObject;
        }
        finally
        {
            Release(raw.Buffer);
        }
    }

    /// <summary>Up to this size buffers come from the shared pool; larger ones (registry) are single pieces.</summary>
    private const int PooledBufferLimit = 64 * 1024;

    /// <summary>Small buffers back into the pool; large ones not – otherwise the pool keeps them in the Large Object Heap for good.</summary>
    private static void Release(byte[] buffer)
    {
        if (buffer.Length <= PooledBufferLimit)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Read a (possibly fragmented) text message into a rented buffer (the caller returns
    /// it). No growing MemoryStream: large answers do not land in the Large
    /// Object Heap anew every time (M5-Fix 1). null = connection closed.
    /// </summary>
    private static async Task<(byte[] Buffer, int Length)?> ReceiveRawAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= MaxMessageBytes)
                    {
                        throw new InvalidOperationException("Message too large");
                    }

                    var size = buffer.Length * 2;
                    var bigger = size <= PooledBufferLimit ? ArrayPool<byte>.Shared.Rent(size) : GC.AllocateUninitializedArray<byte>(size);
                    buffer.AsSpan(0, length).CopyTo(bigger);
                    Release(buffer);
                    buffer = bigger;
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Release(buffer);
                    return null;
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    return (buffer, length);
                }
            }
        }
        catch
        {
            Release(buffer);
            throw;
        }
    }

    private static string Short(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return inner.Message;
    }
}
