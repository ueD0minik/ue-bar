// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Notifications through the mobile app registration (SPEC 8.3, M6). The ue Bar registers with HA
/// like the phone app (<c>POST /api/mobile_app/registrations</c>); HA then creates the service
/// <c>notify.mobile_app_&lt;device&gt;</c>. Delivery through the WebSocket subscription
/// <c>mobile_app/push_notification_channel</c> with confirmation. According to the HA source 2026.10 neither
/// needs admin – HA only checks that the webhook ID belongs to the user of the connection.
/// The webhook ID is stored encrypted in the core's secret store.
/// </summary>
internal sealed class HaMobileApp
{
    public const string SecretWebhook = "mobileAppWebhookId";
    public const string SecretDeviceId = "mobileAppDeviceId";

    /// <summary>Name under which the device was last listed in HA (M7: detect renames).</summary>
    public const string SecretDeviceName = "mobileAppDeviceName";
    public const string AppId = "uebar.windows";

    private readonly HaClient _client;
    private readonly Func<IModuleContext?> _context;
    private readonly Func<IModuleLogger> _log;
    private readonly Action<UeNotification> _notify;
    private readonly string? _fixedDeviceName;

    public HaMobileApp(HaClient client, Func<IModuleContext?> context, Func<IModuleLogger> log, Action<UeNotification> notify, string? deviceName = null)
    {
        _client = client;
        _context = context;
        _log = log;
        _notify = notify;
        _fixedDeviceName = deviceName;
        _client.SessionStarted += OnSessionStartedAsync;
    }

    /// <summary>Device name in HA ("ue Bar – MY-PC"); name from the app settings (M7), otherwise the computer name.</summary>
    public string DeviceName =>
        $"ue Bar – {_fixedDeviceName ?? (_context() as IHostInfo)?.DeviceName ?? Environment.MachineName}";

    /// <summary>Name of the notification service HA makes from it.</summary>
    public string ServiceName => "notify.mobile_app_" + Slugify(DeviceName);

    /// <summary>Short state for the diagnostics (UI language).</summary>
    public string State { get; private set; } = "–";

    public int Received { get; private set; }

    /// <summary>Waiting time until HA has created the integration after the registration (tests: shorter).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Like HA <c>slugify</c>: simplify umlauts, lower case, everything else → "_".</summary>
    public static string Slugify(string text)
    {
        var normalized = text.Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var underscore = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(lower);
                underscore = false;
            }
            else if (!underscore && builder.Length > 0)
            {
                builder.Append('_');
                underscore = true;
            }
        }

        return builder.ToString().TrimEnd('_');
    }

    private async Task OnSessionStartedAsync(CancellationToken ct)
    {
        if (_context() is not IModuleSecrets secrets)
        {
            State = HaText.T["Notify_NoSecretStore"];
            return;
        }

        for (var attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            var webhook = secrets.GetSecret(SecretWebhook);
            var fresh = false;
            if (webhook is null)
            {
                webhook = await RegisterAsync(secrets, ct).ConfigureAwait(false);
                if (webhook is null)
                {
                    return;
                }

                fresh = true;
            }

            var (result, _) = await _client.SubscribeAsync(
                new JsonObject { ["type"] = "mobile_app/push_notification_channel", ["webhook_id"] = webhook, ["support_confirm"] = true },
                evt => OnPush(webhook, evt),
                ct).ConfigureAwait(false);
            if (result.Success)
            {
                if (!fresh)
                {
                    await RenameIfNeededAsync(secrets, webhook, ct).ConfigureAwait(false);
                }
                else
                {
                    secrets.SetSecret(SecretDeviceName, DeviceName);
                }

                State = HaText.T.Format("Notify_ConnectedAs", DeviceName);
                _log().Log(LogLevel.Info, $"Notifications: push channel open ({ServiceName})");
                return;
            }

            if (result.ErrorCode is "not_found" or "unauthorized")
            {
                if (fresh)
                {
                    // HA creates the integration asynchronously after the registration – wait briefly.
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }

                // Deleted in HA or another user: register again cleanly.
                _log().Log(LogLevel.Info, $"Mobile app registration in HA no longer valid ({result.ErrorCode}) – registering again");
                secrets.SetSecret(SecretWebhook, null);
                continue;
            }

            State = result.ErrorCode == "unknown_command"
                ? HaText.T["Notify_NoIntegration"]
                : HaText.T.Format("Notify_NoPushChannel", result.ErrorCode);
            _log().Log(LogLevel.Warning, $"Notifications: push channel not possible ({result.ErrorCode})");
            return;
        }
    }

    /// <summary>
    /// Device name changed in the settings (M7): rename it in HA through <c>update_registration</c> –
    /// HA then loads the notification service under the new name. Errors are not a problem,
    /// the old name keeps applying.
    /// </summary>
    private async Task RenameIfNeededAsync(IModuleSecrets secrets, string webhook, CancellationToken ct)
    {
        var name = DeviceName;
        var registered = secrets.GetSecret(SecretDeviceName);
        if (registered == name)
        {
            return;
        }

        var body = new JsonObject
        {
            ["type"] = "update_registration",
            ["data"] = new JsonObject
            {
                ["app_version"] = AppVersion,
                ["device_name"] = name,
                ["manufacturer"] = "ue Bar",
                ["model"] = "Windows PC",
                ["os_version"] = Environment.OSVersion.Version.ToString(),
            },
        };
        var (status, _) = await _client.PostJsonAsync($"/api/webhook/{webhook}", body, ct).ConfigureAwait(false);
        if (status == 200)
        {
            secrets.SetSecret(SecretDeviceName, name);
            if (registered is not null)
            {
                _log().Log(LogLevel.Info, $"Device renamed in HA: \"{registered}\" → \"{name}\" – service now {ServiceName}");
            }
        }
        else
        {
            _log().Log(LogLevel.Warning, $"Device could not be renamed in HA (HTTP {status}) – name stays \"{registered ?? "unknown"}\"");
        }
    }

    private static string AppVersion => typeof(HaMobileApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private async Task<string?> RegisterAsync(IModuleSecrets secrets, CancellationToken ct)
    {
        var deviceId = secrets.GetSecret(SecretDeviceId);
        if (deviceId is null)
        {
            deviceId = Guid.NewGuid().ToString("N");
            secrets.SetSecret(SecretDeviceId, deviceId);
        }

        var body = new JsonObject
        {
            ["device_id"] = deviceId,
            ["app_id"] = AppId,
            ["app_name"] = "ue Bar",
            ["app_version"] = AppVersion,
            ["device_name"] = DeviceName,
            ["manufacturer"] = "ue Bar",
            ["model"] = "Windows PC",
            ["os_name"] = "Windows",
            ["os_version"] = Environment.OSVersion.Version.ToString(),
            ["supports_encryption"] = false,
            ["app_data"] = new JsonObject { ["push_websocket_channel"] = true },
        };

        var (status, response) = await _client.PostJsonAsync("/api/mobile_app/registrations", body, ct).ConfigureAwait(false);
        if (status is not (200 or 201) || response?["webhook_id"]?.ToString() is not { Length: > 0 } webhook)
        {
            State = status switch
            {
                0 => HaText.T["Notify_RegisterNoConnection"],
                404 => HaText.T["Notify_NoIntegration"],
                401 or 403 => HaText.T["Notify_RegisterDenied"],
                _ => HaText.T.Format("Notify_RegisterFailed", status),
            };
            _log().Log(LogLevel.Warning, $"Notifications: registration failed (HTTP {status})");
            return null;
        }

        secrets.SetSecret(SecretWebhook, webhook);
        _log().Log(LogLevel.Info, $"Registered with HA as mobile app \"{DeviceName}\" – service {ServiceName}");
        return webhook;
    }

    private void OnPush(string webhook, JsonNode evt)
    {
        var message = evt["message"]?.ToString() ?? "";
        var title = evt["title"]?.ToString();
        var data = evt["data"] as JsonObject;
        var seconds = data?["duration"] is JsonValue d && d.TryGetValue<double>(out var s) && s is > 0 and <= 120 ? s : (double?)null;
        var icon = data?["icon"]?.ToString() is { Length: > 0 } i ? HaIcons.FromMdi(i) ?? HaIcons.FromMdi("mdi:" + i) ?? "Bell" : "Bell";
        Received++;
        _notify(new UeNotification(
            string.IsNullOrWhiteSpace(title) ? "Home Assistant" : title,
            message,
            icon,
            seconds is { } sec ? TimeSpan.FromSeconds(sec) : null));

        if (evt["hass_confirm_id"]?.ToString() is { Length: > 0 } confirmId && _context() is { } context)
        {
            // Confirm, otherwise HA tears down the channel after 10 s. Do not wait on the receive thread.
            context.RunBackground("Confirm notification", async ct =>
            {
                var confirm = await _client.SendCommandAsync(
                    new JsonObject { ["type"] = "mobile_app/push_notification_confirm", ["webhook_id"] = webhook, ["confirm_id"] = confirmId },
                    ct).ConfigureAwait(false);
                if (!confirm.Success)
                {
                    _log().Log(LogLevel.Info, $"Confirmation rejected: {confirm.ErrorCode}");
                }
            });
        }
    }
}
