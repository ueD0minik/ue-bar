// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows.Media;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Sound of a notification (P2-3, <see cref="UeNotification.Sound"/>): the Windows alarm sound or a
/// file, with volume. One player at a time; it is dropped when it has finished. UI thread only.
/// </summary>
internal static class NotificationSound
{
    private static MediaPlayer? _player;

    /// <summary>Windows alarm sounds, in this order (they are in every Windows since 8).</summary>
    private static readonly string[] DefaultFiles = ["Alarm01.wav", "Windows Notify System Generic.wav", "Windows Notify.wav"];

    public static void Play(string sound, double volume, IUeLogger log)
    {
        var file = Resolve(sound);
        if (file is null)
        {
            log.Warn(null, $"Notification sound not found: {sound}");
            return;
        }

        try
        {
            Stop();
            var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
            player.MediaEnded += (_, _) => Stop();
            player.MediaFailed += (_, e) =>
            {
                log.Warn(null, $"Notification sound could not be played: {e.ErrorException?.Message}");
                Stop();
            };
            player.Open(new Uri(file, UriKind.Absolute));
            player.Play();
            _player = player;
        }
        catch (Exception ex)
        {
            log.Warn(null, "Notification sound could not be played", ex);
        }
    }

    /// <summary>"default" → Windows alarm sound; otherwise an existing file.</summary>
    internal static string? Resolve(string sound)
    {
        if (string.Equals(sound, UeNotification.DefaultSound, StringComparison.OrdinalIgnoreCase))
        {
            var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            return DefaultFiles.Select(f => Path.Combine(media, f)).FirstOrDefault(File.Exists);
        }

        var path = Environment.ExpandEnvironmentVariables(sound.Trim().Trim('"'));
        return Path.IsPathRooted(path) && File.Exists(path) ? path : null;
    }

    private static void Stop()
    {
        if (_player is { } player)
        {
            _player = null;
            player.Stop();
            player.Close();
        }
    }
}
