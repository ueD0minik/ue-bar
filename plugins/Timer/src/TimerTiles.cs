// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Runtime.CompilerServices;
using UeBar.Sdk;
using static UeBar.Modules.Timer.TimerText;

namespace UeBar.Modules.Timer;

/// <summary>Shared base of the timer tiles: elements from the SDK building blocks, refreshed from the engine.</summary>
internal abstract class TimerTileType(TimerModule module) : ITileType
{
    protected TimerModule Module { get; } = module;

    public abstract string Id { get; }

    protected abstract string NameKey { get; }

    protected abstract string Entity { get; }

    public string DisplayName => T[NameKey];

    public abstract string Icon { get; }

    public virtual IReadOnlyList<TileSize> AllowedSizes => TileSize.All;

    public TileSize DefaultSize => TileSize.Large;

    public IReadOnlyList<TileSlot> Slots => [new TileSlot("main", T["Timer_SlotMain"], false, Id)];

    public TileModel CreateTile(TileConfig config)
    {
        var tile = new TileModel { Title = DisplayName, Icon = Icon };
        Build(tile);
        tile.Handler = new DelegateInteractionHandler((t, i, ct) => HandleAsync(t, i));
        lock (Module.ViewLock)
        {
            // Tracked first, so no change is missed – the lock keeps the worker off it meanwhile.
            Module.Track(tile, this, config);
            Refresh(tile, config);
        }

        return tile;
    }

    public void Refresh(TileModel tile, TileConfig config)
    {
        Update(tile, out var title);
        if (string.IsNullOrWhiteSpace(config.Label))
        {
            tile.Title = title;
        }
    }

    protected abstract void Build(TileModel tile);

    protected abstract void Update(TileModel tile, out string title);

    protected abstract Task HandleAsync(TileModel tile, TileInteraction interaction);

    protected static void SetButtons(ButtonRowElement row, IReadOnlyList<TileButton> wanted)
    {
        if (row.Buttons.Select(b => b.Id).SequenceEqual(wanted.Select(b => b.Id)))
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                row.Buttons[i].Icon = wanted[i].Icon;
                row.Buttons[i].Label = wanted[i].Label;
                row.Buttons[i].IsActive = wanted[i].IsActive;
                row.Buttons[i].IsEnabled = wanted[i].IsEnabled;
            }

            return;
        }

        row.Buttons = [.. wanted];
    }

    protected static TileButton Button(string id, string icon, string label, bool active = false, bool enabled = true) =>
        new() { Id = id, Icon = icon, Label = label, IsActive = active, IsEnabled = enabled };

    protected static void SetOptions(ChoiceElement choice, IReadOnlyList<ChoiceOption> wanted, string? selected)
    {
        if (!choice.Options.Select(o => (o.Id, o.Label)).SequenceEqual(wanted.Select(o => (o.Id, o.Label))))
        {
            choice.Options = [.. wanted];
        }

        choice.SelectedId = selected;
    }

    protected static string PresetLabel(TimeSpan duration) =>
        duration.TotalMinutes >= 1 && duration.Seconds == 0 && duration.TotalHours < 1
            ? ((int)duration.TotalMinutes).ToString(CultureInfo.CurrentCulture)
            : Duration(duration);
}

/// <summary>New timer: presets (5/10/15/25/45 …), own duration with −/+, start.</summary>
internal sealed class StartTileType(TimerModule module) : TimerTileType(module)
{
    /// <summary>Own duration per tile (minutes) – lives in memory only.</summary>
    private readonly ConditionalWeakTable<TileModel, StrongBox<double>> _custom = [];

    public override string Id => "timer.start";

    protected override string NameKey => "Timer_NewTimer";

    protected override string Entity => TimerIds.Start;

    public override string Icon => "Clock";

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "quick", Label = T["Timer_StartFirstPreset"] });
        tile.Elements.Add(new ChoiceElement { Id = "presets" });
        tile.Elements.Add(new StepperElement { Id = "custom", Min = 1, Max = 240, Step = 1, Unit = " min", Value = 20 });
        tile.Elements.Add(new ButtonRowElement { Id = "start" });
    }

    protected override void Update(TileModel tile, out string title)
    {
        title = T["Timer_NewTimer"];
        var presets = Module.Settings.Presets;
        var running = Module.With(e => e.Timers.Count(t => t.IsRunning));
        tile.StateText = running > 0 ? T.Format("Timer_CountRunning", running) : T.Format("Timer_TapToStart", Duration(presets[0]));
        tile.IsActive = running > 0;
        if (tile.FindElement<ChoiceElement>("presets") is { } choice)
        {
            choice.Label = T["Timer_Presets"];
            SetOptions(choice, [.. presets.Select(p => new ChoiceOption { Id = ((int)p.TotalSeconds).ToString(CultureInfo.InvariantCulture), Label = PresetLabel(p) })], null);
        }

        var custom = _custom.GetValue(tile, _ => new StrongBox<double>(20)).Value;
        if (tile.FindElement<StepperElement>("custom") is { } stepper)
        {
            stepper.Label = T["Timer_OwnDuration"];
            stepper.Value = custom;
        }

        if (tile.FindElement<ButtonRowElement>("start") is { } row)
        {
            SetButtons(row, [Button("start", "Play", T.Format("Timer_StartMinutes", (int)custom))]);
        }
    }

    protected override Task HandleAsync(TileModel tile, TileInteraction interaction)
    {
        var box = _custom.GetValue(tile, _ => new StrongBox<double>(20));
        switch (interaction)
        {
            case { ElementId: "quick", Kind: InteractionKind.Tap }:
                Module.Change(e => e.Start(Module.Settings.Presets[0], null, Module.Now));
                break;
            case { ElementId: "presets", Kind: InteractionKind.Select, Text: { } seconds } when int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s):
                Module.Change(e => e.Start(TimeSpan.FromSeconds(s), null, Module.Now));
                break;
            case { ElementId: "custom", Kind: InteractionKind.Increment }:
                box.Value = Math.Min(240, box.Value + (box.Value >= 30 ? 5 : 1));
                break;
            case { ElementId: "custom", Kind: InteractionKind.Decrement }:
                box.Value = Math.Max(1, box.Value - (box.Value > 30 ? 5 : 1));
                break;
            case { ElementId: "custom", Kind: InteractionKind.SetValue, Number: { } value }:
                box.Value = Math.Clamp(Math.Round(value), 1, 240);
                break;
            case { ElementId: "start", Kind: InteractionKind.Press }:
                Module.Change(e => e.Start(TimeSpan.FromMinutes(box.Value), null, Module.Now));
                break;
        }

        Module.Wake();
        return Task.CompletedTask;
    }
}

/// <summary>Running timers: time of the chosen one, choice between several, pause/resume, +1 min, stop.</summary>
internal sealed class RunningTileType(TimerModule module) : TimerTileType(module)
{
    private readonly ConditionalWeakTable<TileModel, StrongBox<string?>> _selected = [];

    public override string Id => "timer.running";

    protected override string NameKey => "Timer_Running";

    protected override string Entity => TimerIds.Running;

    public override string Icon => "Clock";

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = T["Timer_PauseResume"] });
        tile.Elements.Add(new ValueDisplayElement { Id = "time" });
        tile.Elements.Add(new ButtonRowElement { Id = "controls" });
    }

    private CountdownTimer? Selected(TileModel tile, IReadOnlyList<CountdownTimer> timers)
    {
        var box = _selected.GetValue(tile, _ => new StrongBox<string?>());
        var selected = timers.FirstOrDefault(t => t.Id == box.Value) ?? timers.FirstOrDefault();
        box.Value = selected?.Id;
        return selected;
    }

    protected override void Update(TileModel tile, out string title)
    {
        var now = Module.Now;
        var timers = Module.With(e => e.Ordered(now));
        var timer = Selected(tile, timers);
        title = timer?.Name ?? T["Timer_Running"];
        tile.IsActive = timer?.IsRunning == true;
        tile.StateText = timers.Count switch
        {
            0 => T["Timer_NoneRunning"],
            1 => timer!.IsRunning ? Clock(timer.Remaining(now), roundUp: true) : T["Timer_Paused"],
            var n => T.Format("Timer_CountRunning", n),
        };

        if (tile.FindElement<ValueDisplayElement>("time") is { } value)
        {
            value.Value = timer is null ? "–" : Clock(timer.Remaining(now), roundUp: true);
            value.Label = timer is null ? T["Timer_NoneRunning"]
                : (timer.IsRunning ? T.Format("Timer_EndsAt", EndsAtText(timer.EndsAt!.Value)) : T["Timer_Paused"]);
        }

        // Several timers: a choice between them (sits after the time).
        var choice = tile.FindElement<ChoiceElement>("select");
        if (timers.Count > 1)
        {
            if (choice is null)
            {
                choice = new ChoiceElement { Id = "select" };
                tile.Elements.Insert(2, choice);
            }

            SetOptions(choice, [.. timers.Take(4).Select((t, i) => new ChoiceOption { Id = t.Id, Label = t.Name ?? Clock(t.Duration) })], timer?.Id);
        }
        else if (choice is not null)
        {
            tile.Elements.Remove(choice);
        }

        if (tile.FindElement<ButtonRowElement>("controls") is { } row)
        {
            var has = timer is not null;
            SetButtons(row,
            [
                Button("toggle", timer?.IsRunning == true ? "Pause" : "Play", T["Timer_PauseResume"], enabled: has),
                Button("plus", "Add", T["Timer_PlusOne"], enabled: has),
                Button("stop", "Stop", T["Timer_Stop"], enabled: has),
            ]);
        }
    }

    private string EndsAtText(DateTimeOffset end) =>
        TimeZoneInfo.ConvertTime(end, Module.Time.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture);

    protected override Task HandleAsync(TileModel tile, TileInteraction interaction)
    {
        var now = Module.Now;
        if (interaction is { ElementId: "select", Kind: InteractionKind.Select, Text: { } id })
        {
            _selected.GetValue(tile, _ => new StrongBox<string?>()).Value = id;
            Module.Wake();
            return Task.CompletedTask;
        }

        var timer = Selected(tile, Module.With(e => e.Ordered(now)));
        if (timer is null)
        {
            return Task.CompletedTask;
        }

        var command = interaction switch
        {
            { ElementId: "toggle", Kind: InteractionKind.Tap } => "toggle",
            { ElementId: "controls", Kind: InteractionKind.Press, Text: { } button } => button,
            _ => null,
        };
        switch (command)
        {
            case "toggle":
                Module.Change(e =>
                {
                    if (!e.Pause(timer.Id, now))
                    {
                        e.Resume(timer.Id, now);
                    }
                });
                break;
            case "plus":
                Module.Change(e => e.Add(timer.Id, TimeSpan.FromMinutes(1), now));
                break;
            case "stop":
                Module.Change(e => e.Remove(timer.Id));
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Stopwatch: time, start/pause, lap, reset; the last laps below the time.</summary>
internal sealed class StopwatchTileType(TimerModule module) : TimerTileType(module)
{
    public override string Id => "timer.stopwatch";

    protected override string NameKey => "Timer_Stopwatch";

    protected override string Entity => TimerIds.Stopwatch;

    public override string Icon => "U+E916";

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = T["Timer_StartPause"] });
        tile.Elements.Add(new ValueDisplayElement { Id = "time" });
        tile.Elements.Add(new ButtonRowElement { Id = "controls" });
    }

    protected override void Update(TileModel tile, out string title)
    {
        var now = Module.Now;
        var (elapsed, running, laps) = Module.With(e => (e.Stopwatch.Elapsed(now), e.Stopwatch.IsRunning, e.LapSplits()));
        title = T["Timer_Stopwatch"];
        tile.IsActive = running;
        tile.StateText = elapsed == TimeSpan.Zero ? T["Timer_StopwatchReady"] : Clock(elapsed) + (laps.Count > 0 ? " · " + T.Format("Timer_Laps", laps.Count) : "");
        if (tile.FindElement<ValueDisplayElement>("time") is { } value)
        {
            value.Value = Clock(elapsed);
            value.Label = laps.Count == 0 ? null : string.Join(" · ", laps.Take(3).Select(l => T.Format("Timer_Lap", l.Number, Clock(l.Split))));
        }

        if (tile.FindElement<ButtonRowElement>("controls") is { } row)
        {
            SetButtons(row,
            [
                Button("toggle", running ? "Pause" : "Play", T["Timer_StartPause"], running),
                Button("lap", "U+E7C1", T["Timer_LapButton"], enabled: running),
                Button("reset", "U+E72C", T["Timer_Reset"], enabled: elapsed > TimeSpan.Zero),
            ]);
        }
    }

    protected override Task HandleAsync(TileModel tile, TileInteraction interaction)
    {
        var now = Module.Now;
        var command = interaction switch
        {
            { ElementId: "toggle", Kind: InteractionKind.Tap } => "toggle",
            { ElementId: "controls", Kind: InteractionKind.Press, Text: { } button } => button,
            _ => null,
        };
        switch (command)
        {
            case "toggle":
                Module.Change(e =>
                {
                    if (e.Stopwatch.IsRunning)
                    {
                        e.PauseStopwatch(now);
                    }
                    else
                    {
                        e.StartStopwatch(now);
                    }
                });
                break;
            case "lap":
                Module.Change(e => e.Lap(now));
                break;
            case "reset":
                Module.Change(e => e.ResetStopwatch());
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Pomodoro: time of the phase, phase and round, start/pause, skip, stop; "done today".</summary>
internal sealed class PomodoroTileType(TimerModule module) : TimerTileType(module)
{
    public override string Id => "timer.pomodoro";

    protected override string NameKey => "Pomodoro_Name";

    protected override string Entity => TimerIds.Pomodoro;

    public override string Icon => "U+E823";

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = T["Timer_StartPause"] });
        tile.Elements.Add(new ValueDisplayElement { Id = "time" });
        tile.Elements.Add(new ButtonRowElement { Id = "controls" });
    }

    protected override void Update(TileModel tile, out string title)
    {
        var now = Module.Now;
        var settings = Module.Settings.Pomodoro;
        var (phase, round, remaining, running, done) = Module.With(e => (e.Pomodoro.Phase, e.Pomodoro.Round, e.Pomodoro.Remaining(now), e.Pomodoro.IsRunning, e.CompletedToday(Module.Today)));
        title = T["Pomodoro_Name"];
        tile.IsActive = running;
        tile.StateText = T.Format("Pomodoro_DoneToday", done);
        if (tile.FindElement<ValueDisplayElement>("time") is { } value)
        {
            value.Value = phase == PomodoroPhase.Idle ? Clock(settings.Work) : Clock(remaining, roundUp: true);
            value.Label = phase == PomodoroPhase.Idle
                ? T["Pomodoro_Idle"]
                : T.Format("Pomodoro_PhaseRound", TimerModule.PhaseName(phase), round, settings.RoundsBeforeLongBreak) + (running ? "" : " · " + T["Timer_Paused"]);
        }

        if (tile.FindElement<ButtonRowElement>("controls") is { } row)
        {
            var active = phase != PomodoroPhase.Idle;
            SetButtons(row,
            [
                Button("toggle", running ? "Pause" : "Play", T["Timer_StartPause"], running),
                Button("skip", "Next", T["Pomodoro_Skip"], enabled: active),
                Button("stop", "Stop", T["Pomodoro_StopButton"], enabled: active),
            ]);
        }
    }

    protected override async Task HandleAsync(TileModel tile, TileInteraction interaction)
    {
        var command = interaction switch
        {
            { ElementId: "toggle", Kind: InteractionKind.Tap } => "toggle",
            { ElementId: "controls", Kind: InteractionKind.Press, Text: { } button } => button,
            _ => null,
        };
        switch (command)
        {
            case "toggle":
                if (Module.With(e => e.Pomodoro.IsRunning))
                {
                    Module.Change(e => e.PausePomodoro(Module.Now));
                }
                else
                {
                    await Module.StartPomodoroAsync().ConfigureAwait(false);
                }

                break;
            case "skip":
                await Module.SkipPomodoroAsync().ConfigureAwait(false);
                break;
            case "stop":
                Module.StopPomodoro();
                break;
        }
    }
}
