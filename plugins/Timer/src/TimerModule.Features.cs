// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using static UeBar.Modules.Timer.TimerText;

namespace UeBar.Modules.Timer;

/// <summary>IDs of the things the plugin offers (slot "main", status line, search).</summary>
internal static class TimerIds
{
    public const string Start = "timer.start";
    public const string Running = "timer.running";
    public const string Stopwatch = "timer.stopwatch";
    public const string Pomodoro = "timer.pomodoro";
    public const string Next = "timer.next";
}

public sealed partial class TimerModule
{
    // ------------------------------------------------------------------ settings

    public IReadOnlyList<SettingField> SettingFields
    {
        get
        {
            var actions = ActionChoices();
            var argumentsHint = T["Timer_SettingArguments_Hint"];
            return
            [
                new(TimerSettings.PresetsKey, T["Timer_SettingPresets"], SettingFieldKind.Text, "5, 10, 15, 25, 45", T["Timer_SettingPresets_Hint"]),
                new(TimerSettings.WorkKey, T["Timer_SettingWork"], SettingFieldKind.Number, "25", T["Timer_SettingMinutes_Hint"], Min: 0.1, Max: 600),
                new(TimerSettings.ShortKey, T["Timer_SettingShortBreak"], SettingFieldKind.Number, "5", T["Timer_SettingMinutes_Hint"], Min: 0.1, Max: 600),
                new(TimerSettings.LongKey, T["Timer_SettingLongBreak"], SettingFieldKind.Number, "15", T["Timer_SettingMinutes_Hint"], Min: 0.1, Max: 600),
                new(TimerSettings.RoundsKey, T["Timer_SettingRounds"], SettingFieldKind.Number, "4", null, Min: 1, Max: 12),
                new(TimerSettings.SoundKey, T["Timer_SettingSound"], SettingFieldKind.Choice, TimerSettings.SoundDefault, null,
                [
                    new(TimerSettings.SoundDefault, T["Timer_SoundDefault"]),
                    new(TimerSettings.SoundFile, T["Timer_SoundFile"]),
                    new(TimerSettings.SoundNone, T["Timer_SoundNone"]),
                ]),
                new(TimerSettings.SoundFileKey, T["Timer_SettingSoundFile"], SettingFieldKind.File, null, T["Timer_SettingSoundFile_Hint"])
                {
                    Placeholder = @"C:\Users\…\Music\gong.mp3",
                    FileFilter = T["Timer_SoundFileFilter"] + "|*.wav;*.mp3;*.wma;*.m4a",
                    ShowWhen = TimerSettings.SoundKey + "=" + TimerSettings.SoundFile,
                },
                new(TimerSettings.VolumeKey, T["Timer_SettingVolume"], SettingFieldKind.Number, "70", T["Timer_SettingVolume_Hint"], Min: 0, Max: 100),
                new(TimerSettings.WorkActionKey, T["Timer_SettingWorkAction"], SettingFieldKind.Choice, "", T["Timer_SettingAction_Hint"], actions),
                new(TimerSettings.WorkArgumentsKey, T["Timer_SettingWorkArguments"], SettingFieldKind.Text, null, argumentsHint) { Placeholder = ArgumentsExample },
                new(TimerSettings.BreakActionKey, T["Timer_SettingBreakAction"], SettingFieldKind.Choice, "", T["Timer_SettingAction_Hint"], actions),
                new(TimerSettings.BreakArgumentsKey, T["Timer_SettingBreakArguments"], SettingFieldKind.Text, null, argumentsHint) { Placeholder = ArgumentsExample },
            ];
        }
    }

    /// <summary>Example for the arguments of an action (JSON) – generic, nothing of a real home.</summary>
    private const string ArgumentsExample = "{ \"domain\": \"script\", \"service\": \"turn_on\", \"target\": { \"entity_id\": \"script.example\" } }";

    /// <summary>
    /// Actions of other plugins this plugin may use (module bus, permission under "May use").
    /// Value = "provider|action", so a revoked permission can be asked for again.
    /// </summary>
    private IReadOnlyList<SettingChoice> ActionChoices()
    {
        var choices = new List<SettingChoice> { new("", T["Timer_NoAction"]) };
        if (_context?.Bus is { } bus)
        {
            choices.AddRange(bus.GetVisiblePublications()
                .Where(p => p.Kind == BusAccess.Actions && !string.Equals(p.OwnerModuleId, ModuleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.OwnerModuleId, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new SettingChoice($"{p.OwnerModuleId}|{p.Id}", string.IsNullOrWhiteSpace(p.Description) ? $"{p.Id} ({p.OwnerModuleId})" : $"{p.Id} – {p.Description}")));
        }

        return choices;
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var settings = TimerSettings.From(values);
        lock (_lock)
        {
            _settings = settings;
            _engine.PomodoroSettings = settings.Pomodoro;
        }

        Wake();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ module bus (SPEC 4.7)

    private void PublishOnBus(IModuleBus bus)
    {
        bus.PublishEvent("timer.finished", "A timer ran out: { name, seconds, at }");
        bus.PublishEvent("pomodoro.phase_changed", "Pomodoro phase changed: { phase: work/short_break/long_break/idle, round, completedToday }");
        bus.PublishAction("timer.start", (call, _) =>
        {
            var args = call.Arguments as JsonObject;
            var duration = args?["duration"]?.GetValueKind() == JsonValueKind.String && TimerCommand.TryParseDuration(args["duration"]!.GetValue<string>(), out var d) ? d
                : Number(args?["seconds"]) is { } s ? TimeSpan.FromSeconds(s)
                : Number(args?["minutes"]) is { } m ? TimeSpan.FromMinutes(m)
                : throw new ArgumentException("\"minutes\", \"seconds\" or \"duration\" (e.g. \"1:30\") is missing");
            if (duration <= TimeSpan.Zero || duration > TimerCommand.Longest)
            {
                throw new ArgumentException("Duration must be between 1 s and 24 h");
            }

            var name = args?["name"]?.GetValueKind() == JsonValueKind.String ? args["name"]!.GetValue<string>() : null;
            var timer = Change(e => e.Start(duration, name, Now));
            return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = timer.Id });
        }, "Start a timer: { minutes | seconds | duration: \"1:30\", name? }");
        bus.PublishAction("pomodoro.start", async (_, _) =>
        {
            await StartPomodoroAsync().ConfigureAwait(false);
            return null;
        }, "Start (or resume) Pomodoro");
        bus.PublishAction("pomodoro.stop", (_, _) =>
        {
            StopPomodoro();
            return Task.FromResult<JsonNode?>(null);
        }, "Stop Pomodoro");
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture)
            : null;

    internal async Task StartPomodoroAsync()
    {
        var started = Change(e =>
        {
            var idle = e.Pomodoro.Phase == PomodoroPhase.Idle;
            e.StartPomodoro(Now);
            return idle;
        });
        if (started)
        {
            // A new set starts with work: same reaction as a phase change.
            await PhaseChangedAsync(new PomodoroPhaseChanged(PomodoroPhase.Idle, PomodoroPhase.Work, 1, Now, false), CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal void StopPomodoro()
    {
        var wasRunning = Change(e =>
        {
            var active = e.Pomodoro.Phase != PomodoroPhase.Idle;
            e.StopPomodoro();
            return active;
        });
        if (wasRunning)
        {
            RaisePhaseEvent(PomodoroPhase.Idle, 1);
        }
    }

    internal async Task SkipPomodoroAsync()
    {
        var change = Change(e => e.SkipPomodoroPhase(Now, Today));
        if (change is not null)
        {
            await PhaseChangedAsync(change, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Bus event + the action configured for the new phase (only if permitted – the core checks).</summary>
    private async Task PhaseChangedAsync(PomodoroPhaseChanged change, CancellationToken cancellationToken)
    {
        RaisePhaseEvent(change.Next, change.Round);
        var settings = Settings;
        var (action, arguments, what) = change.Next switch
        {
            PomodoroPhase.Work => (settings.WorkAction, settings.WorkArguments, "work"),
            PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => (settings.BreakAction, settings.BreakArguments, "break"),
            _ => (null, null, ""),
        };
        if (action is not null)
        {
            await InvokeConfiguredActionAsync(action, arguments, what, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RaisePhaseEvent(PomodoroPhase phase, int round) =>
        _context?.Bus.RaiseEvent("pomodoro.phase_changed", new JsonObject
        {
            ["phase"] = phase switch
            {
                PomodoroPhase.Work => "work",
                PomodoroPhase.ShortBreak => "short_break",
                PomodoroPhase.LongBreak => "long_break",
                _ => "idle",
            },
            ["round"] = round,
            ["completedToday"] = With(e => e.CompletedToday(Today)),
        });

    /// <summary>Runs "provider|action" with the JSON arguments. Denied → asks the user for the permission.</summary>
    internal async Task<BusResult?> InvokeConfiguredActionAsync(string setting, string? arguments, string what, CancellationToken cancellationToken)
    {
        if (_context?.Bus is not { } bus)
        {
            return null;
        }

        var bar = setting.IndexOf('|', StringComparison.Ordinal);
        var provider = bar > 0 ? setting[..bar] : null;
        var actionId = bar > 0 ? setting[(bar + 1)..] : setting;
        JsonNode? args = null;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                args = JsonNode.Parse(arguments);
            }
            catch (JsonException ex)
            {
                Log?.Warn($"Pomodoro {what} action: arguments are no valid JSON ({ex.Message}) – not run");
                return null;
            }
        }

        var result = await bus.InvokeAsync(actionId, args, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            Log?.Info($"Pomodoro {what}: ran {actionId}");
        }
        else
        {
            Log?.Warn($"Pomodoro {what}: {actionId} not run ({result.Error}: {result.Message})");
            if (result.Error is BusErrorKind.Denied or BusErrorKind.NotFound && provider is not null)
            {
                bus.RequestPermission(provider, BusAccess.Actions, T.Format("Timer_PermissionReason", actionId));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ quick commands

    public IReadOnlyList<QuickCommandInfo> QuickCommands =>
    [
        new("t", T["Timer_QuickCommand"], "Clock"),
        new("pomo", T["Pomodoro_QuickCommand"], "U+E823") { ArgumentOptional = true },
    ];

    public async Task<QuickCommandResult> ExecuteQuickCommandAsync(string prefix, string argument, CancellationToken cancellationToken)
    {
        if (string.Equals(prefix.Trim(), "pomo", StringComparison.OrdinalIgnoreCase))
        {
            if (argument.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                StopPomodoro();
                return new QuickCommandResult(true, T["Pomodoro_Stopped"]);
            }

            await StartPomodoroAsync().ConfigureAwait(false);
            return new QuickCommandResult(true, T.Format("Pomodoro_Started", Duration(Settings.Pomodoro.Work)));
        }

        if (!TimerCommand.TryParse(argument, out var request))
        {
            return new QuickCommandResult(false, T["Timer_QuickCommandUsage"]);
        }

        Change(e => e.Start(request.Duration, request.Name, Now));
        return new QuickCommandResult(true, request.Name is { } name
            ? T.Format("Timer_StartedNamed", name, Duration(request.Duration))
            : T.Format("Timer_Started", Duration(request.Duration)));
    }

    // ------------------------------------------------------------------ search

    private IEnumerable<(EntityInfo Info, string[] Words)> AllEntities()
    {
        (EntityInfo, string[]) Make(string id, string nameKey, string icon, string? state, params string[] extra) =>
            (new EntityInfo(id, T[nameKey], icon, T["Timer_ModuleName"], state, "timer"), [.. BothLanguages(nameKey), .. extra]);

        var now = Now;
        var (running, stopwatch, pomodoro) = With(e => (e.Timers.Count, e.Stopwatch.Elapsed(now), e.Pomodoro.Phase));
        yield return Make(TimerIds.Start, "Timer_NewTimer", "Clock", T["Timer_NewTimerHint"], "countdown", "wecker", "alarm");
        yield return Make(TimerIds.Running, "Timer_Running", "Clock", running == 0 ? T["Timer_NoneRunning"] : T.Format("Timer_CountRunning", running), "countdown");
        yield return Make(TimerIds.Stopwatch, "Timer_Stopwatch", "U+E916", Clock(stopwatch), "stoppuhr", "stopwatch");
        yield return Make(TimerIds.Pomodoro, "Pomodoro_Name", "U+E823", PhaseName(pomodoro), "fokus", "focus", "lernen", "study");
    }

    public Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var q = query?.Trim() ?? "";
        IReadOnlyList<EntityInfo> result =
        [
            .. AllEntities()
                .Where(e => q.Length == 0 || e.Info.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Words.Any(w => w.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .Take(maxResults)
                .Select(e => e.Info),
        ];
        return Task.FromResult(result);
    }

    public async Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        var now = Now;
        switch (entityId)
        {
            case TimerIds.Start:
                Change(e => e.Start(Settings.Presets[0], null, now));
                break;
            case TimerIds.Running:
                Change(e =>
                {
                    if (e.Ordered(now).FirstOrDefault() is { } first && !e.Pause(first.Id, now))
                    {
                        e.Resume(first.Id, now);
                    }
                });
                break;
            case TimerIds.Stopwatch:
                Change(e =>
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
            case TimerIds.Pomodoro:
                if (With(e => e.Pomodoro.IsRunning))
                {
                    Change(e => e.PausePomodoro(now));
                }
                else
                {
                    await StartPomodoroAsync().ConfigureAwait(false);
                }

                break;
        }

        if (AllEntities().FirstOrDefault(e => e.Info.Id == entityId) is { Info: { } info })
        {
            RaiseEntityChanged(info);
        }
    }

    public TileConfig? SuggestTile(EntityInfo entity)
    {
        var type = entity.Id switch
        {
            TimerIds.Start => "timer.start",
            TimerIds.Running => "timer.running",
            TimerIds.Stopwatch => "timer.stopwatch",
            TimerIds.Pomodoro => "timer.pomodoro",
            _ => null,
        };
        return type is null ? null : new TileConfig
        {
            Id = $"{ModuleId}-{Guid.NewGuid():N}"[..14],
            Module = ModuleId,
            Type = type,
            Size = TileSize.Large,
            Entities = new Dictionary<string, string> { ["main"] = entity.Id },
        };
    }

    // ------------------------------------------------------------------ suggested layout

    public LayoutDocument GetSuggestedLayout() => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "timer",
                Name = T["Timer_TabName"],
                Icon = "Clock",
                Tiles =
                [
                    Tile("t1", "timer.start", TimerIds.Start),
                    Tile("t2", "timer.running", TimerIds.Running),
                    Tile("t3", "timer.pomodoro", TimerIds.Pomodoro),
                    Tile("t4", "timer.stopwatch", TimerIds.Stopwatch),
                ],
            },
        ],
    };

    private static TileConfig Tile(string id, string type, string entity) => new()
    {
        Id = id,
        Module = ModuleId,
        Type = type,
        Size = TileSize.Large,
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };
}
