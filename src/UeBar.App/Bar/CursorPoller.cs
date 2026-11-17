// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;

namespace UeBar.App.Bar;

/// <summary>
/// Queries the mouse position with GetCursorPos every ~60 ms on a background thread
/// (no global hook). The UI thread is only woken when there is something to do: bar visible,
/// mouse in the trigger zone or mouse just left it. Pauses completely (waits for a
/// signal) while <see cref="SetPaused"/> is active.
/// </summary>
internal sealed class CursorPoller : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(60);

    private readonly Action<PixelPoint> _onTick;
    private readonly IUeLogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _paused;
    private volatile PixelRect[] _zones = [];
    private volatile bool _barVisible;
    private int _tickPending;
    private bool _lastInZone;

    /// <param name="onTick">Called on the background thread; must switch to the UI thread itself.</param>
    public CursorPoller(Action<PixelPoint> onTick, IUeLogger log)
    {
        _onTick = onTick;
        _log = log;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void SetZones(IEnumerable<PixelRect> zones) => _zones = [.. zones];

    public void SetBarVisible(bool visible) => _barVisible = visible;

    /// <summary>Call from the UI thread after processing a tick (prevents a backlog).</summary>
    public void TickHandled() => Interlocked.Exchange(ref _tickPending, 0);

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
            if (!paused)
            {
                _resume.TrySetResult();
            }
            else
            {
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _resume.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_paused)
                {
                    Task wait;
                    lock (_lock)
                    {
                        wait = _resume.Task;
                    }

                    await wait.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                Poll();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(null, "Mouse polling ended", ex);
        }
    }

    private void Poll()
    {
        if (MonitorService.CursorPosition() is not { } p)
        {
            return; // e.g. secure desktop (UAC)
        }

        var inZone = false;
        foreach (var zone in _zones)
        {
            if (zone.Contains(p))
            {
                inZone = true;
                break;
            }
        }

        var needed = _barVisible || inZone || _lastInZone;
        _lastInZone = inZone;
        if (!needed || Interlocked.Exchange(ref _tickPending, 1) == 1)
        {
            return;
        }

        _onTick(p);
    }
}
