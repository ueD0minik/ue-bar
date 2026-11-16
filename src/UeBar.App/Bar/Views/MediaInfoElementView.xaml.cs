// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UeBar.App.Bar.Media;
using UeBar.Core.Tiles;

namespace UeBar.App.Bar.Views;

/// <summary>
/// Media info. The progress is computed from position + timestamp; it only counts up
/// through <see cref="MediaClock"/> (panel visible and playing), otherwise the display stands still.
/// </summary>
public partial class MediaInfoElementView : UserControl
{
    private MediaInfoElement? _element;

    public MediaInfoElementView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            MediaClock.Tick += OnTick;
            Attach(DataContext as MediaInfoElement);
        };
        Unloaded += (_, _) =>
        {
            MediaClock.Tick -= OnTick;
            Attach(null);
        };
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded)
            {
                Attach(e.NewValue as MediaInfoElement);
            }
        };
    }

    /// <summary>Current position in seconds (without duration: null).</summary>
    internal static double? CurrentPosition(MediaInfoElement element, DateTimeOffset now)
    {
        if (element.Duration is not { } duration || element.Position is not { } position)
        {
            return null;
        }

        if (element.IsPlaying && element.PositionUpdatedAt is { } at)
        {
            position += Math.Max(0, (now - at).TotalSeconds);
        }

        return Math.Clamp(position, 0, duration);
    }

    internal static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Attach(MediaInfoElement? element)
    {
        if (_element is not null)
        {
            _element.PropertyChanged -= OnElementChanged;
        }

        _element = element;
        if (element is not null)
        {
            element.PropertyChanged += OnElementChanged;
        }

        UpdatePlaying();
        UpdateProgress();
    }

    private void OnElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Comes from the module on any thread.
        Dispatcher.BeginInvoke(() =>
        {
            UpdatePlaying();
            UpdateProgress();
        });
    }

    private void OnTick(object? sender, EventArgs e) => UpdateProgress();

    private void UpdatePlaying() =>
        MediaClock.SetPlaying(this, IsLoaded && _element is { IsPlaying: true, Duration: > 0 });

    private void UpdateProgress()
    {
        if (_element is null || CurrentPosition(_element, DateTimeOffset.Now) is not { } position || _element.Duration is not { } duration)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            return;
        }

        ProgressRow.Visibility = Visibility.Visible;
        Progress.Maximum = duration;
        Progress.Value = position;
        TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
    }
}
