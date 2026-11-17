// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using UeBar.App.Bar;
using UeBar.App.Localization;
using UeBar.App.Bar.Edit;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Infrastructure;
using UeBar.App.Plugins;
using UeBar.Core.Plugins;
using UeBar.App.Settings;
using UeBar.App.Tiles;
using UeBar.App.Tray;
using UeBar.Bar;
using UeBar.Core;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;
using UeBar.Core.Settings;

namespace UeBar.App;

/// <summary>
/// Entry point: no visible main window, only a tray icon (M1). Builds the core (guard, bus, registry),
/// loads the plugins (<see cref="PluginHost"/>, SPEC 4.4), registers their modules and starts them.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private FileLogger? _log;
    private ModuleRegistry? _registry;
    private TileHost? _tiles;
    private TrayIcon? _tray;
    private BarController? _bar;
    private LocalFileLayoutStore? _layoutCache;
    private SyncedLayoutStore? _layoutStore;
    private ModuleLayoutStoreSource? _remoteLayout;
    private LayoutCoordinator? _layout;
    private Heartbeat? _heartbeat;
    private UiWatchdog? _watchdog;
    private ExtrasController? _extras;
    private bool _exiting;
    private long? _exitStarted;
    private volatile string _exitStage = "not started";

    /// <summary>The exit guard ends the process if it still runs this long after the exit request.</summary>
    private static readonly TimeSpan ExitHardLimit = TimeSpan.FromSeconds(3);

    /// <summary>Each plugin gets at most this long to stop when the app exits (P2-1-Fix 1).</summary>
    private static readonly TimeSpan PluginStopLimit = TimeSpan.FromSeconds(2);
    private string? _lastUiErrorKey;
    private DateTime _lastUiErrorWindow;
    private int _lastUiErrorCount;
    private DateTime _uiErrorMutedUntil;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;

    /// <summary>Signal with which the installer/uninstaller ends the running ue Bar (per session).</summary>
    public const string ExitSignalName = @"Local\ueBar-Exit";

#if DEBUG
    private const string BuildVariant = "Debug";
#else
    private const string BuildVariant = "Release";
#endif

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.Default;

        // More details in the log: environment variable UEBAR_LOG_LEVEL=Debug
        var level = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("UEBAR_LOG_LEVEL"), true, out var l) ? l : LogLevel.Info;
        _log = new FileLogger(paths.Logs, level);
        HookGlobalExceptionHandlers(_log);

        // Very first line – even before the single-instance check.
        _log.Info(null, $"Process starting: PID {Environment.ProcessId}, {BuildVariant}, {Environment.ProcessPath}, plugin interface {CoreApiVersion.Text}, {Environment.OSVersion}{(AppPaths.IsCustom ? $", data folder {paths.Root}" : "")}{(CultureOverride is null ? "" : $", formats {CultureOverride}")}");

        // Restart after a plugin change: wait until the previous process has released the lock.
        await AppRestart.WaitForPredecessorAsync(e.Args, _log);

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null && !await HandleOtherInstanceAsync())
        {
            Shutdown(0);
            return;
        }

        _singleInstance!.StartServer(Dispatcher, BuildVariant, _log);
        _singleInstance.SecondInstanceStarted += request =>
            _tray?.ShowInfo(Loc.Get("App_AlreadyRunning"), Loc.Get("App_SecondStartIgnored"));

        // Installer/uninstaller (M7): named signal "please exit" → shut down cleanly.
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            await ExitAsync("installer");
        }), null, Timeout.Infinite, executeOnlyOnce: true);

        _watchdog = new UiWatchdog(Dispatcher, _log, paths.Logs);
        _watchdog.Start();

        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();

            var settings = new SettingsService(paths.SettingsFile, new DpapiSecretProtector(), _log);
            settings.Load();
            var general = new GeneralSettingsStore(settings);

            // Language (R1) before the first window: setting, otherwise the Windows display language.
            Loc.Apply(general.Current.Language);
            _log.Info(null, $"UI language: {Loc.Culture.Name} (setting: {UeBar.Core.Localization.UiLanguages.Normalize(general.Current.Language)})");

            // Background cleanup (M7): hang dumps – only the last 3 (logs: 7 days, FileLogger).
            if (FileRetention.KeepNewest(paths.Logs, "*.dmp", 3) is > 0 and var removed)
            {
                _log.Info(null, $"{removed} old hang dumps deleted");
            }

            // Software rendering saves ~35–50 MB (no Direct3D device/driver buffers in the process);
            // the bar is small and mostly hidden. Must be set before the first window.
            if (settings.GetSection<BarSettings>(BarController.SettingsSection).SoftwareRendering)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }

            // Appearance (M7) before the first window: accent color, columns.
            Appearance.ApplyAccent(general.Current.AccentColor);
            GridMetrics.Columns = general.Current.EffectiveColumns;

            var guard = new ModuleGuard(_log);
            var bus = new ModuleBus(guard, settings.BusPolicy, _log);
            _registry = new ModuleRegistry(guard, bus, _log, settings, paths.Root)
            {
                DeviceName = () => general.Current.DeviceName,
            };

            // Plugins (SPEC 4.4): find them, ask for every one that does not come with ue Bar (first time
            // and whenever its files changed), load them, hand their modules to the registry. A broken
            // plugin only gets a status – the app starts anyway.
            var plugins = PluginStartup.CreateHost(paths, settings, _log);
            plugins.Discover();
            foreach (var plugin in plugins.Plugins.Where(p => p.State == PluginState.NeedsApproval).ToList())
            {
                plugins.Decide(plugin, PluginTrustDialog.Ask(null, PluginTrustRequest.From(plugin)));
            }

            await plugins.LoadAsync();
            plugins.RegisterAll(_registry);

            _tiles = new TileHost(_registry, _log);
            await _registry.StartAllAsync();

            // Layout levels (SPEC 9): base in a module's store (e.g. Home Assistant, per user)
            // with layout.json as a local copy – without such a module local only. Device always in device.json.
            _layoutCache = new LocalFileLayoutStore(paths.LayoutFile, paths.Backups, _log);
            _remoteLayout = new ModuleLayoutStoreSource(_registry, _log) { Preference = general.Current.LayoutStore };
            _layoutStore = new SyncedLayoutStore(_layoutCache, _remoteLayout, paths.LayoutSyncFile, paths.Backups, _log);
            var manager = new LayoutManager(_layoutStore, new DeviceLayoutStore(paths.DeviceFile, _log), _log, general.Current.EffectiveDeviceName(Environment.MachineName));
            _layout = new LayoutCoordinator(manager, _layoutStore, _registry, _tiles, _log, Dispatcher);
            await _layout.ReloadAsync();

            var location = new LocationService(_registry, _log);
            UeBar.App.Bar.Media.ModuleImageLoader.Current = new UeBar.App.Bar.Media.ModuleImageLoader(_registry, _log);
            var viewModel = new BarViewModel(_registry, _tiles, Dispatcher, _log, location);
            viewModel.Attach();
            var window = new BarWindow();
            _bar = new BarController(window, viewModel, settings, _log, Dispatcher);
            _bar.Start();

            // Extras (M6): notifications, Now Playing, Assist, quick search – through core services.
            var quickCommands = new QuickCommandService(_registry, _log);
            var settingsForExtras = settings;
            _extras = new ExtrasController(
                viewModel,
                _bar,
                _registry,
                new NotificationHub(_registry, _log),
                new NowPlayingService(_registry, _log),
                new AssistService(_registry, quickCommands, _log),
                quickCommands,
                () => settingsForExtras,
                _log,
                Dispatcher);
            var extras = _extras;
            window.EscapePressed += (_, _) => extras.OnEscape();
            window.Panel.AssistInputRequested += (_, _) => extras.BeginInput();
            extras.QuickSearchOpened += (_, _) => window.Panel.FocusQuickSearch();
            _layout.ViewModel = viewModel;
            _layout.Bar = _bar;
            viewModel.EditRequested += (_, _) => _layout.StartEdit();

            var registry = _registry;
            var log = _log;
            var bar = _bar;
            var layout = _layout;
            _tray = new TrayIcon(_registry, _bar, Dispatcher);
            var tray = _tray;
            _tray.ExitRequested += async (_, _) => await ExitAsync("tray");
            _tray.ExportLayout = _layout.Export;
            _tray.ImportLayout = _layout.Import;
            var layoutStore = _layoutStore;
            _tray.LayoutStatus = () => layoutStore.Status;
            _tray.OpenQuickSearch = extras.OpenQuickSearch;
            _tray.ResetBar = async () =>
            {
                // Emergency exit (M6-Fix 1) – without an app restart. Editing is finished with saving first.
                try
                {
                    if (layout.IsEditing)
                    {
                        await layout.FinishEditAsync();
                    }

                    extras.Reset();
                    bar.Reset();
                    if (bar.HotkeyStatus.Result != UeBar.Bar.HotkeyRegistration.Registered)
                    {
                        tray.ShowInfo(Loc.Get("Tray_QuickSearch"), bar.HotkeyStatus.Message);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(null, "Reset bar failed", ex);
                }
            };

            // Keep the panel open while a setup is running (welcome, first start again).
            void HoldPanelWhile(Func<bool> active)
            {
                if (!bar.OpenQuickSearch())
                {
                    return;
                }

                System.ComponentModel.PropertyChangedEventHandler? handler = null;
                handler = (_, e) =>
                {
                    if (e.PropertyName is nameof(BarViewModel.IsSetup) or nameof(BarViewModel.IsWelcome) && !active())
                    {
                        viewModel.PropertyChanged -= handler;
                        bar.CloseQuickSearch();
                    }
                };
                viewModel.PropertyChanged += handler;
            }

            // Settings window (M7)
            var settingsHost = new SettingsHost
            {
                Settings = settings,
                General = general,
                Bar = bar,
                Registry = registry,
                LayoutSource = _remoteLayout,
                LayoutStore = layoutStore,
                Layout = layout,
                Paths = paths,
                Log = log,
                Plugins = plugins,
                Restart = async () =>
                {
                    if (layout.IsEditing)
                    {
                        await layout.FinishEditAsync();
                    }

                    if (AppRestart.StartSuccessor(log))
                    {
                        await ExitAsync("restart");
                    }
                },
                ResetBar = () => tray.ResetBar?.Invoke() ?? Task.CompletedTask,
                ClearNotificationHistory = extras.ClearHistory,
                NotificationHistoryCount = () => extras.HistoryCount,
                NowPlaying = extras.NowPlaying,
                GeneralChanged = (before, after) => _ = ApplyGeneralAsync(before, after, viewModel, bar, registry, log),
                ShowSetupAgain = () =>
                {
                    if (layout.ShowSetupAgain())
                    {
                        HoldPanelWhile(() => viewModel.IsSetup);
                    }
                },
            };
            _tray.OpenSettings = () => SettingsWindow.Show(settingsHost);

            // "ueBar.exe --settings" or "--settings=plugins": open the settings right after the start.
            if (e.Args.FirstOrDefault(a => a.StartsWith("--settings", StringComparison.OrdinalIgnoreCase)) is { } settingsArg)
            {
                var page = settingsArg.Contains('=') ? settingsArg[(settingsArg.IndexOf('=') + 1)..] : null;
                _ = Dispatcher.BeginInvoke(() => SettingsWindow.Show(settingsHost, string.IsNullOrWhiteSpace(page) ? null : page), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            viewModel.SettingsRequested += (_, _) =>
            {
                extras.CloseQuickSearch("settings opened"); // do not leave the pinned panel above the window
                SettingsWindow.Show(settingsHost);
            };

            // Welcome assistant (M7): first start without settings.
            await StartWelcomeIfNeededAsync(settings, general, registry, viewModel, log, HoldPanelWhile, tray);

            _layout.SetupShown += (_, _) => _tray?.ShowInfo(Loc.Get("App_SetupTitle"), Loc.Get("App_SetupText"));
            _layout.UpdateSetup();
            if (_bar.HotkeyStatus.Result != UeBar.Bar.HotkeyRegistration.Registered)
            {
                _tray.ShowInfo(Loc.Get("Tray_QuickSearch"), _bar.HotkeyStatus.Message);
            }

            // Plugins that could not be loaded: say so once (details on the Plugins page).
            var brokenPlugins = plugins.Plugins.Count(p => p.State is PluginState.Error or PluginState.Incompatible);
            if (brokenPlugins > 0)
            {
                _tray.ShowInfo(Loc.Get("Plugins_Problems_Title"), Loc.F("Plugins_Problems_Text", brokenPlugins));
            }

            _heartbeat = new Heartbeat(_registry, _log, location);
            _heartbeat.Start();
            _log.Info(null, $"Start finished (ue Bar {AppInfo.Version}{(AppInfo.IsPortable ? ", portable" : "")})");

#if DEBUG
            // Debug only: deliberately trigger a hang to check the UI watchdog.
            if (int.TryParse(Environment.GetEnvironmentVariable("UEBAR_TEST_HANG"), out var hangSeconds) && hangSeconds > 0)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _log.Warn(null, $"TEST: blocking UI thread {hangSeconds} s");
                    Thread.Sleep(TimeSpan.FromSeconds(hangSeconds));
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            // Debug only: "Reset bar" like from the tray, every N seconds (test without mouse).
            if (int.TryParse(Environment.GetEnvironmentVariable("UEBAR_TEST_RESET"), out var resetSeconds) && resetSeconds > 0)
            {
                var resetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(resetSeconds) };
                resetTimer.Tick += (_, _) => _ = _tray.ResetBar?.Invoke();
                resetTimer.Start();
            }
#endif
        }
        catch (Exception ex)
        {
            _log.Error(null, "Start failed", ex);
            MessageBox.Show(Loc.F("App_StartFailed", ex.Message, paths.Logs), "ue Bar", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync("start failed");
        }
    }

    /// <summary>
    /// Welcome assistant (M7): first start without settings.json (or never finished and a
    /// module is not set up yet). Generic: the first module with "Essential" fields.
    /// </summary>
    private async Task StartWelcomeIfNeededAsync(
        SettingsService settings,
        GeneralSettingsStore general,
        ModuleRegistry registry,
        BarViewModel viewModel,
        IUeLogger log,
        Action<Func<bool>> holdPanelWhile,
        TrayIcon tray)
    {
        var needsSetup = registry.Modules.Any(m => m.IsEnabled && m.Module is IModuleSettings && m.Status.State == UeBar.Sdk.ModuleState.NotConfigured);
        if (!WelcomeRules.ShouldShow(settings.FileExisted, general.Current.WelcomeDone, needsSetup))
        {
            return;
        }

        foreach (var module in registry.Modules.Where(m => m.IsEnabled && m.Module is IModuleSettings))
        {
            var fields = await registry.InvokeAsync(module.Id, "Read setting fields", (m, _) =>
                Task.FromResult(m is IModuleSettings s ? s.SettingFields : (IReadOnlyList<SettingField>)[]));
            if (fields is not { Succeeded: true, Value: { } list } || !list.Any(f => f.Essential))
            {
                continue;
            }

            log.Info(null, $"Welcome assistant for {module.Id}");
            var welcome = new WelcomeViewModel(registry, settings, log, module, list);
            welcome.Finished += (_, skipped) =>
            {
                general.Update(g => g.WelcomeDone = true);
                viewModel.HideWelcome();
                if (skipped)
                {
                    tray.ShowInfo("ue Bar", Loc.Get("App_SetupLater"));
                }
            };
            viewModel.ShowWelcome(welcome);
            holdPanelWhile(() => viewModel.IsWelcome || viewModel.IsSetup);
            return;
        }

        // No module needs input: only the first-start layout is left (if needed).
        general.Update(g => g.WelcomeDone = true);
    }

    /// <summary>
    /// Developers (screenshots, format tests): <c>UEBAR_CULTURE=en-US</c> sets the culture for numbers and
    /// dates without changing the Windows region. Runs in the static constructor – before the app's
    /// resources are loaded, which already read the format culture.
    /// </summary>
    static App()
    {
        CultureOverride = ApplyCultureOverride();
    }

    private static string? CultureOverride { get; }

    /// <summary>Environment variable <c>UEBAR_CULTURE</c> (e.g. "en-US"). Returns its name or null.</summary>
    private static string? ApplyCultureOverride()
    {
        if (Environment.GetEnvironmentVariable("UEBAR_CULTURE") is not { Length: > 0 } name)
        {
            return null;
        }

        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(name.Trim());
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            return culture.Name;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>General settings changed (M7/R1): apply accent color, columns, device name and language right away.</summary>
    private static async Task ApplyGeneralAsync(GeneralSettings before, GeneralSettings after, BarViewModel viewModel, BarController bar, ModuleRegistry registry, IUeLogger log)
    {
        if (UeBar.Core.Localization.UiLanguages.Normalize(before.Language) != UeBar.Core.Localization.UiLanguages.Normalize(after.Language))
        {
            // Modules, tiles, view models and open windows follow through UiCultureSource.Changed.
            Loc.Apply(after.Language);
            log.Info(null, $"UI language: {Loc.Culture.Name} (setting: {UeBar.Core.Localization.UiLanguages.Normalize(after.Language)})");
        }

        if (!string.Equals(before.AccentColor, after.AccentColor, StringComparison.OrdinalIgnoreCase))
        {
            Appearance.ApplyAccent(after.AccentColor);
            viewModel.RefreshLayout();
            log.Info(null, $"Accent color: {AccentColors.Normalize(after.AccentColor) ?? AccentColors.Default}");
        }

        if (before.EffectiveColumns != after.EffectiveColumns)
        {
            GridMetrics.Columns = after.EffectiveColumns;
            bar.ApplyColumns();
            log.Info(null, $"Tile grid: {after.EffectiveColumns} columns");
        }

        if (before.EffectiveDeviceName(Environment.MachineName) != after.EffectiveDeviceName(Environment.MachineName))
        {
            log.Info(null, $"Device name: {after.EffectiveDeviceName(Environment.MachineName)} – running modules reconnect");
            foreach (var module in registry.Modules.Where(m => m.IsEnabled && m.IsRunning))
            {
                await registry.RestartModuleAsync(module.Id);
            }
        }
    }

    /// <summary>
    /// Another instance holds the lock. Ask briefly (max. 2 s) whether it responds. If it does,
    /// this start ends. If it hangs, the user is asked whether it should be ended.
    /// Returns true if this start may continue.
    /// </summary>
    private async Task<bool> HandleOtherInstanceAsync()
    {
        var log = _log!;
        var (state, reply) = await SingleInstance.PingOtherAsync(BuildVariant);
        if (state == OtherInstanceState.Responsive)
        {
            log.Info(null, $"ue Bar is already running ({reply}) – this start ends.");
            return false;
        }

        var others = SingleInstance.OtherProcesses();
        var info = others.Count == 0
            ? "no process found"
            : string.Join(", ", others.Select(p => $"PID {p.Id}, started {SafeStart(p)}"));
        log.Warn(null, $"ue Bar is already running but does not respond within {SingleInstance.HandshakeTimeout.TotalSeconds:0} s ({info}).");

        var answer = MessageBox.Show(
            Loc.Get("App_HangingQuestion"),
            "ue Bar",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            log.Info(null, "User declined ending the hanging instance.");
            return false;
        }

        foreach (var process in others)
        {
            try
            {
                log.Warn(null, $"Ending hanging instance PID {process.Id}");
                process.Kill();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(cts.Token);
            }
            catch (Exception ex)
            {
                log.Error(null, $"Instance PID {process.Id} could not be ended: {ex.Message}");
            }
        }

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            log.Error(null, "Lock is still held after ending – start aborted.");
            MessageBox.Show(Loc.Get("App_CannotEndOld"), "ue Bar", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        return true;

        static string SafeStart(System.Diagnostics.Process p)
        {
            try
            {
                return p.StartTime.ToString("HH:mm:ss");
            }
            catch (InvalidOperationException)
            {
                return "?";
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exitStage = "OnExit";

        // Normally ExitAsync has already done all of this; this covers an early end (start aborted).
        _watchdog?.Dispose();
        _heartbeat?.Dispose();
        _tray?.Dispose();
        _layoutStore?.Dispose();
        _remoteLayout?.Dispose();
        _layoutCache?.Dispose();
        _singleInstance?.Dispose();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        var since = _exitStarted is { } started ? $", {ShutdownText(System.Diagnostics.Stopwatch.GetElapsedTime(started))} after the exit request" : "";
        _log?.Info(null, $"Process ended: PID {Environment.ProcessId}{since}");
        _exitStage = "runtime shutdown (after OnExit)";
        base.OnExit(e);
    }

    /// <summary>
    /// Ordered exit (P2-1-Fix 1): tray and bar first (nothing new can be triggered), then all
    /// plugins at the same time with 2 s each, then layout stores and the background helpers.
    /// Every step is timed and logged; nothing can hold up the steps after it. If the process is
    /// still there <see cref="ExitHardLimit"/> after the request, the exit guard logs where it hangs and
    /// ends the process.
    /// </summary>
    private async Task ExitAsync(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _exitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _log?.Info(null, $"Exit requested ({reason})");
        StartExitGuard();

        var registry = _registry;
        var layoutStore = _layoutStore;
        var remoteLayout = _remoteLayout;
        var layoutCache = _layoutCache;
        var singleInstance = _singleInstance;
        _layoutStore = null;
        _remoteLayout = null;
        _layoutCache = null;
        _singleInstance = null;

        var sequence = new ShutdownSequence(_log ?? (IUeLogger)NullLogger.Instance)
            .Add("tray", () =>
            {
                _tray?.Dispose();
                _tray = null;
            })
            .Add("bar", () =>
            {
                _bar?.Dispose();
                _bar = null;
            })
            .Add("plugins", async _ =>
            {
                if (registry is not null)
                {
                    await registry.ShutdownAsync(PluginStopLimit);
                }
            }, PluginStopLimit + TimeSpan.FromMilliseconds(300))
            // Not tied to the UI thread – off it, so a stuck file watcher or lock cannot freeze the exit.
            .Add("layout", _ => Task.Run(() =>
            {
                layoutStore?.Dispose();
                remoteLayout?.Dispose();
                layoutCache?.Dispose();
            }), TimeSpan.FromMilliseconds(500))
            .Add("helpers", _ => Task.Run(() =>
            {
                _heartbeat?.Dispose();
                _watchdog?.Dispose();
            }), TimeSpan.FromMilliseconds(500))
            // The mutex belongs to the UI thread – release it there.
            .Add("single instance", () => singleInstance?.Dispose());
#if DEBUG
        // Debug only: block the UI thread while exiting to check the exit guard.
        if (int.TryParse(Environment.GetEnvironmentVariable("UEBAR_TEST_EXIT_HANG"), out var exitHangSeconds) && exitHangSeconds > 0)
        {
            sequence.Add("test hang", () => Thread.Sleep(TimeSpan.FromSeconds(exitHangSeconds)));
        }
#endif
        sequence.StepStarted += name => _exitStage = name;
        await sequence.RunAsync();

        _exitStage = "WPF shutdown (closing windows)";
        Shutdown(0);
    }

    /// <summary>
    /// Last line of defense: a background thread that ends the process if the exit does not finish in
    /// time – after logging which stage hangs, so the next occurrence can be found in the log.
    /// </summary>
    private void StartExitGuard()
    {
        var log = _log;
        var started = _exitStarted ?? System.Diagnostics.Stopwatch.GetTimestamp();
        var thread = new Thread(() =>
        {
            Thread.Sleep(ExitHardLimit);
            log?.Error(null, $"Exit hangs in '{_exitStage}' – {ShutdownText(System.Diagnostics.Stopwatch.GetElapsedTime(started))} after the request; ending the process");

            // Environment.Exit runs the runtime's exit handlers; should even that get stuck, end hard.
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            })
            { IsBackground = true, Name = "Exit guard (hard)" }.Start();
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "Exit guard",
        };
        thread.Start();
    }

    private static string ShutdownText(TimeSpan time) => $"{time.TotalMilliseconds:0} ms";

    private void HookGlobalExceptionHandlers(IUeLogger log)
    {
        // Last line of defense: nothing may end the app silently.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // M7: the same exception in a loop (e.g. a broken template in the layout) must not flood the log
            // – after 5 identical ones in 30 s only one hint, then 60 s of quiet for exactly this one.
            var key = $"{args.Exception.GetType().FullName}: {args.Exception.Message}";
            var now = DateTime.UtcNow;
            if (key == _lastUiErrorKey && now - _lastUiErrorWindow < TimeSpan.FromSeconds(30))
            {
                _lastUiErrorCount++;
            }
            else if (key != _lastUiErrorKey || now >= _uiErrorMutedUntil)
            {
                _lastUiErrorKey = key;
                _lastUiErrorWindow = now;
                _lastUiErrorCount = 1;
                _uiErrorMutedUntil = DateTime.MinValue;
            }

            if (now < _uiErrorMutedUntil)
            {
                return;
            }

            if (_lastUiErrorCount > 5)
            {
                _uiErrorMutedUntil = now.AddSeconds(60);
                log.Error(null, $"The same exception repeats on the UI thread – further identical messages suppressed for 60 s: {key}");
                return;
            }

            log.Error(null, "Unhandled exception on the UI thread", args.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error(null, "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error(null, $"Unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);
    }
}
