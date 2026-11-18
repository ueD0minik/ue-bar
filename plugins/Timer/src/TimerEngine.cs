// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Serialization;

namespace UeBar.Modules.Timer;

/// <summary>A countdown with a name. Running: <see cref="EndsAt"/> counts; paused: <see cref="PausedRemaining"/>.</summary>
internal sealed class CountdownTimer
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    /// <summary>Length it was started with (for "+5 min" and texts).</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>UTC end while running, null while paused.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    public TimeSpan PausedRemaining { get; set; }

    [JsonIgnore]
    public bool IsRunning => EndsAt is not null;

    public TimeSpan Remaining(DateTimeOffset now) => EndsAt is { } end ? end - now : PausedRemaining;
}

internal sealed class StopwatchState
{
    /// <summary>UTC start of the current run, null while stopped.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Time of earlier runs (before the last pause).</summary>
    public TimeSpan Accumulated { get; set; }

    /// <summary>Total time at each lap (newest last).</summary>
    public List<TimeSpan> Laps { get; set; } = [];

    [JsonIgnore]
    public bool IsRunning => StartedAt is not null;

    public TimeSpan Elapsed(DateTimeOffset now) => Accumulated + (StartedAt is { } start ? now - start : TimeSpan.Zero);
}

internal enum PomodoroPhase
{
    Idle,
    Work,
    ShortBreak,
    LongBreak,
}

internal sealed class PomodoroState
{
    public PomodoroPhase Phase { get; set; }

    /// <summary>Work round within the set (1 … rounds before the long break).</summary>
    public int Round { get; set; } = 1;

    /// <summary>UTC end of the phase while running, null while paused or idle.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    public TimeSpan PausedRemaining { get; set; }

    /// <summary>Finished work rounds on <see cref="CompletedDate"/> (local date).</summary>
    public int CompletedCount { get; set; }

    public DateOnly CompletedDate { get; set; }

    [JsonIgnore]
    public bool IsRunning => EndsAt is not null;

    public TimeSpan Remaining(DateTimeOffset now) => EndsAt is { } end ? end - now : PausedRemaining;
}

/// <summary>Lengths of the Pomodoro phases.</summary>
internal sealed record PomodoroSettings(TimeSpan Work, TimeSpan ShortBreak, TimeSpan LongBreak, int RoundsBeforeLongBreak)
{
    public static PomodoroSettings Default { get; } = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 4);

    public TimeSpan LengthOf(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Work => Work,
        PomodoroPhase.ShortBreak => ShortBreak,
        PomodoroPhase.LongBreak => LongBreak,
        _ => TimeSpan.Zero,
    };
}

/// <summary>Something that happened in <see cref="TimerEngine.Process"/>.</summary>
internal abstract record TimerEvent(DateTimeOffset At);

/// <summary>A timer ran out. <paramref name="WhileAway"/>: it ended while ue Bar was not running (or the PC slept).</summary>
internal sealed record TimerFinished(CountdownTimer Timer, DateTimeOffset At, bool WhileAway) : TimerEvent(At);

/// <summary>A Pomodoro phase ended, <paramref name="Next"/> started (or Idle).</summary>
internal sealed record PomodoroPhaseChanged(PomodoroPhase Ended, PomodoroPhase Next, int Round, DateTimeOffset At, bool WhileAway) : TimerEvent(At);

/// <summary>
/// Timers, stopwatch and Pomodoro as plain state with absolute UTC times – survives a restart and does
/// not drift. All time from the caller (<see cref="TimeProvider"/> in the module). Not thread-safe:
/// the module locks around it.
/// </summary>
internal sealed class TimerEngine
{
    /// <summary>A due time that passed longer ago than this counts as "while away" (PC slept, app was closed).</summary>
    public static readonly TimeSpan AwayThreshold = TimeSpan.FromSeconds(30);

    public List<CountdownTimer> Timers { get; set; } = [];

    public StopwatchState Stopwatch { get; set; } = new();

    public PomodoroState Pomodoro { get; set; } = new();

    [JsonIgnore]
    public PomodoroSettings PomodoroSettings { get; set; } = PomodoroSettings.Default;

    /// <summary>Anything that changes every second (for the display).</summary>
    [JsonIgnore]
    public bool AnythingRunning => Timers.Any(t => t.IsRunning) || Stopwatch.IsRunning || Pomodoro.IsRunning;

    /// <summary>Running timers and paused ones, the one due first first.</summary>
    public IReadOnlyList<CountdownTimer> Ordered(DateTimeOffset now) =>
        [.. Timers.OrderByDescending(t => t.IsRunning).ThenBy(t => t.Remaining(now))];

    // ------------------------------------------------------------------ timers

    public CountdownTimer Start(TimeSpan duration, string? name, DateTimeOffset now)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A timer needs a positive duration");
        }

        var timer = new CountdownTimer
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Duration = duration,
            EndsAt = now + duration,
        };
        Timers.Add(timer);
        return timer;
    }

    public CountdownTimer? Find(string id) => Timers.FirstOrDefault(t => t.Id == id);

    public bool Pause(string id, DateTimeOffset now)
    {
        if (Find(id) is not { IsRunning: true } timer)
        {
            return false;
        }

        timer.PausedRemaining = timer.Remaining(now);
        timer.EndsAt = null;
        return true;
    }

    public bool Resume(string id, DateTimeOffset now)
    {
        if (Find(id) is not { IsRunning: false } timer)
        {
            return false;
        }

        timer.EndsAt = now + timer.PausedRemaining;
        return true;
    }

    /// <summary>Back to the full duration, paused.</summary>
    public bool Reset(string id)
    {
        if (Find(id) is not { } timer)
        {
            return false;
        }

        timer.EndsAt = null;
        timer.PausedRemaining = timer.Duration;
        return true;
    }

    public bool Add(string id, TimeSpan extra, DateTimeOffset now)
    {
        if (Find(id) is not { } timer)
        {
            return false;
        }

        if (timer.EndsAt is { } end)
        {
            timer.EndsAt = (end < now ? now : end) + extra;
        }
        else
        {
            timer.PausedRemaining += extra;
        }

        return true;
    }

    public bool Remove(string id) => Timers.RemoveAll(t => t.Id == id) > 0;

    // ------------------------------------------------------------------ stopwatch

    public void StartStopwatch(DateTimeOffset now) => Stopwatch.StartedAt ??= now;

    public void PauseStopwatch(DateTimeOffset now)
    {
        if (Stopwatch.StartedAt is { } start)
        {
            Stopwatch.Accumulated += now - start;
            Stopwatch.StartedAt = null;
        }
    }

    public TimeSpan? Lap(DateTimeOffset now)
    {
        if (!Stopwatch.IsRunning)
        {
            return null;
        }

        var total = Stopwatch.Elapsed(now);
        Stopwatch.Laps.Add(total);
        return total - (Stopwatch.Laps.Count > 1 ? Stopwatch.Laps[^2] : TimeSpan.Zero);
    }

    /// <summary>Lap times (split of each lap), newest first.</summary>
    public IReadOnlyList<(int Number, TimeSpan Split)> LapSplits()
    {
        var laps = Stopwatch.Laps;
        return [.. laps.Select((total, i) => (i + 1, total - (i > 0 ? laps[i - 1] : TimeSpan.Zero))).Reverse()];
    }

    public void ResetStopwatch() => Stopwatch = new StopwatchState();

    // ------------------------------------------------------------------ Pomodoro

    /// <summary>Starts a set with a work phase (or resumes a paused phase).</summary>
    public void StartPomodoro(DateTimeOffset now)
    {
        if (Pomodoro.Phase == PomodoroPhase.Idle)
        {
            Pomodoro.Phase = PomodoroPhase.Work;
            Pomodoro.Round = 1;
            Pomodoro.EndsAt = now + PomodoroSettings.Work;
        }
        else if (!Pomodoro.IsRunning)
        {
            Pomodoro.EndsAt = now + Pomodoro.PausedRemaining;
        }
    }

    public void PausePomodoro(DateTimeOffset now)
    {
        if (Pomodoro.IsRunning)
        {
            Pomodoro.PausedRemaining = Pomodoro.Remaining(now);
            Pomodoro.EndsAt = null;
        }
    }

    public void StopPomodoro()
    {
        var (count, date) = (Pomodoro.CompletedCount, Pomodoro.CompletedDate);
        Pomodoro = new PomodoroState { CompletedCount = count, CompletedDate = date };
    }

    /// <summary>Ends the current phase early (it does not count as finished work) and starts the next one.</summary>
    public PomodoroPhaseChanged? SkipPomodoroPhase(DateTimeOffset now, DateOnly today) =>
        Pomodoro.Phase == PomodoroPhase.Idle ? null : Advance(now, today, counts: false, whileAway: false);

    /// <summary>Finished work rounds today.</summary>
    public int CompletedToday(DateOnly today) => Pomodoro.CompletedDate == today ? Pomodoro.CompletedCount : 0;

    private PomodoroPhaseChanged Advance(DateTimeOffset now, DateOnly today, bool counts, bool whileAway)
    {
        var ended = Pomodoro.Phase;
        PomodoroPhase next;
        if (ended == PomodoroPhase.Work)
        {
            if (counts)
            {
                if (Pomodoro.CompletedDate != today)
                {
                    Pomodoro.CompletedDate = today;
                    Pomodoro.CompletedCount = 0;
                }

                Pomodoro.CompletedCount++;
            }

            next = Pomodoro.Round >= PomodoroSettings.RoundsBeforeLongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
        }
        else
        {
            if (ended == PomodoroPhase.LongBreak)
            {
                Pomodoro.Round = 1;
            }
            else
            {
                Pomodoro.Round++;
            }

            next = PomodoroPhase.Work;
        }

        Pomodoro.Phase = next;
        Pomodoro.EndsAt = now + PomodoroSettings.LengthOf(next);
        Pomodoro.PausedRemaining = TimeSpan.Zero;
        return new PomodoroPhaseChanged(ended, next, Pomodoro.Round, now, whileAway);
    }

    // ------------------------------------------------------------------ time passes

    /// <summary>
    /// Everything that is due by <paramref name="now"/>: finished timers are removed; a finished Pomodoro
    /// phase starts the next one. A due time far in the past (standby, app closed) is reported as
    /// "while away" with its real end time; after such a gap the next Pomodoro phase starts now
    /// instead of catching up on phases nobody saw.
    /// </summary>
    public IReadOnlyList<TimerEvent> Process(DateTimeOffset now, DateOnly today)
    {
        var events = new List<TimerEvent>();
        foreach (var timer in Timers.Where(t => t.EndsAt is { } end && end <= now).OrderBy(t => t.EndsAt).ToList())
        {
            var end = timer.EndsAt!.Value;
            events.Add(new TimerFinished(timer, end, now - end > AwayThreshold));
            Timers.Remove(timer);
        }

        if (Pomodoro.EndsAt is { } phaseEnd && phaseEnd <= now)
        {
            var away = now - phaseEnd > AwayThreshold;
            var change = Advance(away ? now : phaseEnd, today, counts: true, whileAway: away);
            events.Add(change with { At = phaseEnd });
        }

        return events;
    }

    /// <summary>Next point in time something is due (null = nothing runs down).</summary>
    public DateTimeOffset? NextDue() =>
        Timers.Where(t => t.EndsAt is not null).Select(t => t.EndsAt!.Value)
            .Concat(Pomodoro.EndsAt is { } p ? [p] : [])
            .Order()
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();
}
