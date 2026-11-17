// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices.WindowsRuntime;
using global::Windows.Media.Control;
using global::Windows.Storage.Streams;

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Everything Windows knows as playback (Spotify, browsers, YouTube, apps) through
/// <c>GlobalSystemMediaTransportControlsSessionManager</c> – the source of the media controls in the
/// volume flyout. Event driven; the events only raise <see cref="Changed"/>.
/// </summary>
internal sealed class MediaPlatform : IMediaPlatform
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _subscribed = [];

    /// <summary>Title/artist/cover per session – read again only after MediaPropertiesChanged (cross-process call).</summary>
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, GlobalSystemMediaTransportControlsSessionMediaProperties?> _properties = [];
    private string? _currentId;
    private bool _disposed;

    private MediaPlatform(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentChanged;
        Sync();
    }

    public static async Task<MediaPlatform> CreateAsync() =>
        new(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());

    public event EventHandler? Changed;

    public event EventHandler? TimelineChanged;

    public string? CurrentSessionId
    {
        get
        {
            lock (_lock)
            {
                return _currentId;
            }
        }
    }

    public async Task<IReadOnlyList<MediaSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, GlobalSystemMediaTransportControlsSession>> sessions;
        lock (_lock)
        {
            Sync();
            sessions = [.. _sessions];
        }

        var result = new List<MediaSessionInfo>();
        foreach (var (id, session) in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.Add(await DescribeAsync(id, session, await PropertiesAsync(session).ConfigureAwait(false)).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Session closed while reading – it disappears with the next change.
            }
        }

        return result;
    }

    public async Task SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken)
    {
        var session = Find(sessionId);
        if (session is null)
        {
            return;
        }

        _ = command switch
        {
            MediaCommand.Next => await session.TrySkipNextAsync(),
            MediaCommand.Previous => await session.TrySkipPreviousAsync(),
            _ => await session.TryTogglePlayPauseAsync(),
        };
    }

    public async Task<byte[]?> LoadThumbnailAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = Find(sessionId);
        if (session is null)
        {
            return null;
        }

        var properties = await session.TryGetMediaPropertiesAsync();
        if (properties?.Thumbnail is not { } reference)
        {
            return null;
        }

        using var stream = await reference.OpenReadAsync();
        if (stream.Size is 0 or > 8 * 1024 * 1024)
        {
            return null;
        }

        var buffer = new global::Windows.Storage.Streams.Buffer((uint)stream.Size);
        var read = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
        return read.ToArray();
    }

    private async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> PropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        lock (_lock)
        {
            if (_properties.TryGetValue(session, out var cached))
            {
                return cached;
            }
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
        try
        {
            media = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Some apps refuse while switching tracks – try again next time.
            return null;
        }

        lock (_lock)
        {
            _properties[session] = media;
        }

        return media;
    }

    private static Task<MediaSessionInfo> DescribeAsync(string id, GlobalSystemMediaTransportControlsSession session, GlobalSystemMediaTransportControlsSessionMediaProperties? media)
    {
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var controls = playback?.Controls;
        double? duration = timeline is null ? null : (timeline.EndTime - timeline.StartTime).TotalSeconds;
        double? position = timeline is null ? null : (timeline.Position - timeline.StartTime).TotalSeconds;
        if (duration is <= 0)
        {
            duration = null;
            position = null;
        }

        var title = Clean(media?.Title);
        var artist = Clean(media?.Artist) ?? Clean(media?.AlbumArtist);
        return Task.FromResult(new MediaSessionInfo(
            id,
            session.SourceAppUserModelId ?? "",
            AppNames.FromAppId(session.SourceAppUserModelId),
            title,
            artist,
            Clean(media?.AlbumTitle),
            playback?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlayback.Changing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlayback.Opened,
                _ => MediaPlayback.Closed,
            },
            position,
            duration,
            duration is null ? null : timeline!.LastUpdatedTime,
            controls?.IsPreviousEnabled ?? false,
            controls?.IsNextEnabled ?? false,
            controls?.IsPlayPauseToggleEnabled ?? true,
            media?.Thumbnail is null || title is null ? null : $"{title}|{artist}|{Clean(media.AlbumTitle)}".GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private GlobalSystemMediaTransportControlsSession? Find(string id)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    /// <summary>Session list → IDs (app ID, "#2" … for further sessions of the same app) and events.</summary>
    private void Sync()
    {
        if (_disposed)
        {
            return;
        }

        var sessions = _manager.GetSessions();
        _sessions.Clear();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var app = session.SourceAppUserModelId ?? "";
            var n = counts[app] = counts.GetValueOrDefault(app) + 1;
            _sessions[n == 1 ? app : $"{app}#{n}"] = session;
            if (_subscribed.Add(session))
            {
                session.MediaPropertiesChanged += OnPropertiesChanged;
                session.PlaybackInfoChanged += OnSessionChanged;
                session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }

        foreach (var gone in _subscribed.Where(s => !_sessions.ContainsValue(s)).ToList())
        {
            Unsubscribe(gone);
            _subscribed.Remove(gone);
            _properties.Remove(gone);
        }

        var current = _manager.GetCurrentSession();
        _currentId = current is null ? null : _sessions.FirstOrDefault(p => ReferenceEquals(p.Value, current) || p.Value.SourceAppUserModelId == current.SourceAppUserModelId).Key;
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged -= OnPropertiesChanged;
        session.PlaybackInfoChanged -= OnSessionChanged;
        session.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnCurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        lock (_lock)
        {
            _properties.Remove(sender);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => TimelineChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentChanged;
            foreach (var session in _subscribed)
            {
                Unsubscribe(session);
            }

            _subscribed.Clear();
            _sessions.Clear();
            _properties.Clear();
        }
    }
}

/// <summary>Readable app name from an app user model ID ("Spotify.exe" → "Spotify", "MSEdge" → "Edge").</summary>
internal static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
        ["spotify"] = "Spotify",
        ["vlc"] = "VLC",
        ["msteams"] = "Teams",
        ["ms-teams"] = "Teams",
        ["discord"] = "Discord",
        ["zunemusic"] = "Media Player",
        ["microsoft.media.player"] = "Media Player",
    };

    public static string FromAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "?";
        }

        // Packaged apps: "Publisher.App_hash!App" → "App"
        var name = appId;
        var bang = name.IndexOf('!', StringComparison.Ordinal);
        if (bang > 0)
        {
            name = name[..bang];
            var underscore = name.IndexOf('_', StringComparison.Ordinal);
            if (underscore > 0)
            {
                name = name[..underscore];
            }

            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1)
            {
                name = name[(dot + 1)..];
            }
        }

        name = Path.GetFileName(name.Replace('/', '\\'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (Known.TryGetValue(name, out var known))
        {
            return known;
        }

        return name.Length == 0 ? appId : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
