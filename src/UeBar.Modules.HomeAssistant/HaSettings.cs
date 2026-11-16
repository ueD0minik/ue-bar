// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Abstractions;

namespace UeBar.Modules.HomeAssistant;

/// <summary>Settings of the HA module (SPEC 8.1). The token is decrypted in memory only.</summary>
internal sealed record HaSettings(Uri? LocalUrl, Uri? ExternalUrl, string? Token, bool PreferLocal)
{
    public const string KeyLocalUrl = "localUrl";
    public const string KeyExternalUrl = "externalUrl";
    public const string KeyToken = "token";
    public const string KeyPreferLocal = "preferLocal";
    public const string KeyNowPlayingPlayers = "nowPlayingPlayers";
    public const string KeyTodoList = "todoList";
    public const string DefaultTodoList = "todo.einkaufsliste";

    // M7: no default addresses any more (distribution) – only placeholders as examples.
    public const string LocalUrlPlaceholder = "http://homeassistant.local:8123";
    public const string ExternalUrlPlaceholder = "https://…";

    public static HaSettings Empty { get; } = new(null, null, null, true);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && (LocalUrl is not null || ExternalUrl is not null);

    public static IReadOnlyList<SettingField> Fields { get; } = BuildFields([], []);

    /// <summary>
    /// Fields; media players and to-do lists as a choice as soon as HA has delivered them (M7) – otherwise
    /// free input. Labels in the UI language (R1).
    /// </summary>
    public static IReadOnlyList<SettingField> BuildFields(IReadOnlyList<SettingChoice> players, IReadOnlyList<SettingChoice> todoLists) =>
    [
        new(KeyLocalUrl, HaText.T["Setting_LocalUrl"], SettingFieldKind.Text, null, HaText.T["Setting_LocalUrl_Hint"])
        {
            Placeholder = LocalUrlPlaceholder,
            Essential = true,
        },
        new(KeyExternalUrl, HaText.T["Setting_ExternalUrl"], SettingFieldKind.Text, null, HaText.T["Setting_ExternalUrl_Hint"])
        {
            Placeholder = ExternalUrlPlaceholder,
            Essential = true,
        },
        new(KeyToken, HaText.T["Setting_Token"], SettingFieldKind.Password, null,
            HaText.T["Setting_Token_Hint"])
        {
            Essential = true,
        },
        new(KeyPreferLocal, HaText.T["Setting_PreferLocal"], SettingFieldKind.Toggle, "true", HaText.T["Setting_PreferLocal_Hint"]),
        players.Count > 0
            ? new(KeyNowPlayingPlayers, HaText.T["Setting_NowPlaying"], SettingFieldKind.MultiChoice, null,
                HaText.T["Setting_NowPlaying_HintChoice"], players)
            : new(KeyNowPlayingPlayers, HaText.T["Setting_NowPlaying"], SettingFieldKind.Text, null,
                HaText.T["Setting_NowPlaying_HintText"]),
        todoLists.Count > 0
            ? new(KeyTodoList, HaText.T["Setting_TodoList"], SettingFieldKind.Choice, DefaultTodoList, HaText.T["Setting_TodoList_HintChoice"], todoLists)
            : new(KeyTodoList, HaText.T["Setting_TodoList"], SettingFieldKind.Text, DefaultTodoList, HaText.T["Setting_TodoList_HintText"]),
    ];

    /// <summary>Configured Now Playing players (empty = those in the layout).</summary>
    public static IReadOnlyList<string> NowPlayingPlayers(ModuleSettingValues values) =>
        [.. (values.GetString(KeyNowPlayingPlayers) ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase))];

    public static string TodoList(ModuleSettingValues values) => values.GetString(KeyTodoList) is { Length: > 0 } list ? list.Trim() : DefaultTodoList;

    public static HaSettings From(ModuleSettingValues values) => new(
        ParseUrl(values.GetString(KeyLocalUrl)),
        ParseUrl(values.GetString(KeyExternalUrl)),
        values.GetString(KeyToken)?.Trim(),
        values.GetBool(KeyPreferLocal, fallback: true));

    /// <summary>Order of attempts: (URL, is local).</summary>
    public IEnumerable<(Uri Url, bool IsLocal)> Candidates()
    {
        if (PreferLocal)
        {
            if (LocalUrl is not null)
            {
                yield return (LocalUrl, true);
            }

            if (ExternalUrl is not null)
            {
                yield return (ExternalUrl, false);
            }
        }
        else
        {
            if (ExternalUrl is not null)
            {
                yield return (ExternalUrl, false);
            }

            if (LocalUrl is not null)
            {
                yield return (LocalUrl, true);
            }
        }
    }

    /// <summary>"192.168.1.10:8123" → http://192.168.1.10:8123; only http/https; without credentials/trailing slash.</summary>
    public static Uri? ParseUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        builder.Path = builder.Path.TrimEnd('/');
        return builder.Uri;
    }

    /// <summary>http(s)://host/path → ws(s)://host/path/api/websocket</summary>
    public static Uri WebSocketUri(Uri baseUrl)
    {
        var builder = new UriBuilder(baseUrl)
        {
            Scheme = baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/api/websocket";
        return builder.Uri;
    }

    /// <summary>For log/diagnostics: only host and port, never more.</summary>
    public static string Describe(Uri? url) => url is null ? "–" : $"{url.Host}:{url.Port}";
}
