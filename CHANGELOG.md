# Changelog

## 1.3.1 – Timer plugin, bus permissions

**Plugin interface (`CoreApiVersion`): 1.2** – extended, nothing broken: plugins for 1.0 and 1.1
keep loading. SDK package `UeBar.Sdk 1.2.0`. From now on new plugins, features and fixes count up
the last digit of the app version.

### New
- **Timer plugin** (`plugins/Timer`, plugin ID `timer`, comes with ue Bar and is on): several named
  timers with presets (5/10/15/25/45 min, changeable) and an own duration, a stopwatch with laps,
  Pomodoro (25/5/15, long break after 4 rounds, "done today"). Quick commands `t 10`, `t 1:30`,
  `t 25 Learning`, `pomo`/`pomo stop`. The pill shows the time left of the next timer; a status
  line value. A timer that runs out: notification with sound (Windows alarm or own file, volume),
  60 s or until closed, **+5 min** button, also during "Do not disturb" and over fullscreen apps
  (both settings, default on). Timers survive a restart and standby (absolute times; ones that ran
  out meanwhile are reported with their time). Module bus: actions `timer.start`,
  `pomodoro.start`, `pomodoro.stop`, events `timer.finished`, `pomodoro.phase_changed`. See
  [plugins/Timer](plugins/Timer/README.md).
- **Module bus permissions in the settings:** Settings → Plugins → *a plugin* → **May use** – per
  other plugin and kind (actions, events, values), default off; requests of plugins appear with
  **Allow**/**Decline**. First collaboration: Pomodoro can run a permitted action of another plugin
  (e.g. a Home Assistant script) when work or a break starts.
- Contract 1.2: `UeNotification` with `Id`, `Actions`, `Sound`/`SoundVolume`, `IgnoreDoNotDisturb`,
  `ShowInFullscreen` and `INotificationActionHandler`; `IPillActivitySource`/`PillActivity`;
  `ModuleVisibility.IsBarVisible`; `QuickCommandInfo.ArgumentOptional`; `SettingFieldKind.Button`
  with `IModuleSettingsActions`.
- Notification banner in the open panel: buttons of the notification and **×** to close it.
- Windows plugin: when the Wi-Fi name is hidden (Windows 11 24H2 needs location access), the tile
  says so and the plugin's settings offer **Open location access**.

### Changed
- Quick commands with a word prefix need a space after it ("t 10" is a timer, "toaster" a search).
- Settings sidebar: plugin pages indented again under "Plugins" (with their icon).
- Windows plugin: the device list re-reads the batteries of other plugins while it is visible.

### Fixed
- `Network_change_with_healthy_connection_does_not_reconnect` is deterministic. Cause: on a busy
  machine the first connection attempt could miss its real-time local limit, and the reconnect pause
  then ran on the test's fake clock, which nobody moved. The test now lets the clock move until the
  session runs, afterwards only while the client waits for the debounce, and checks that no
  *additional* connection is made. The layout-sync test had the same trap.
- Timer worker: time that passed between computing and registering a wait is no longer lost.
- `Nothing_reachable_shows_disconnected_with_next_attempt_and_retries` (and the reconnect test) read
  the state and the text in two steps – the client could already be trying again in between. The
  test helper now records every status change; state and text come from the same one.
- Saving the layout could fail with "file in use" when someone held `layout.json` open for a moment
  (the layout watcher reading it back, a virus scanner): Windows does not replace an open file. The
  save now retries briefly (up to ~1.3 s) instead of losing the change. Loading does the same while
  a save replaces the file, and the file watcher looks again instead of missing the change. Found
  through rare failures of the layout-sync conflict test.
- Tiles whose elements a plugin changes on its own thread (app volume, device list, running timers):
  the app's snapshot of the elements only retried when the list reported the change, not when it was
  caught half way through growing (`IndexOutOfRangeException`) – a rare crash of the redraw. The
  tests read the elements the same safe way.
- Windows and Timer plugins: a new tile was refreshed on the core's thread while the worker could
  already refresh it – two threads adding to one element list left it broken for good. Tile changes
  are now serialized.
- HA tests no longer ping every 250 ms in real time by default: on a busy machine the pong came too
  late, the client dropped a healthy connection and a command running meanwhile failed
  (`Command_results_are_detached_not_copied_and_errors_still_arrive`). Only the tests about missing
  pongs use short times.

## 1.3.0 – Windows plugin

**Plugin interface (`CoreApiVersion`): 1.1** – extended, nothing broken: plugins built for 1.0 keep
loading. SDK package `UeBar.Sdk 1.1.0`.

### New
- **Windows plugin** (`plugins/Windows`, plugin ID `windows`, comes with ue Bar and is on):
  volume with mute and **output device** (headphones ↔ monitor ↔ speakers; virtual cables and
  app channels of mixer tools only on request), **app volume** for the apps that play sound,
  **media of all apps** Windows knows (Spotify, YouTube in the browser …) with cover, progress,
  controls and source switching – also as **Now Playing** in the pill –, **Wi-Fi** and
  **Bluetooth** on/off, **devices & batteries** (the battery levels Windows shows for Bluetooth
  devices), **battery** with time left/until full, **CPU, memory, disk**, **brightness** of the
  built-in display, **lock, sleep** (tap twice) and **screen off**. Everything searchable, a
  suggested "Windows" tab, status line values, settings, module bus publications (`win.set_volume`,
  `win.mute`, `win.media_play_pause`, `win.lock`, values `win.battery`, `win.cpu`,
  `win.audio_device`, event `win.media_changed`). See [plugins/Windows](plugins/Windows/README.md).
- **Settings → Layout → Add a plugin's suggestion:** adds the tabs a plugin suggests to the
  existing layout (backed up first) – the rest of the layout stays as it is.
- **Settings → Bar → Now Playing: order of the sources** – when several plugins report playback.
  Default: playback on this PC first; what plays always comes before what is paused.
- Contract 1.1: `IVisibilityAware` (what of a plugin is on screen – poll only while visible),
  `IDeviceBatterySource` and `IDeviceBatteryCatalog` (device batteries across plugins),
  `INowPlayingSource.NowPlayingPriority` (default member).
- For developers: with its own `UEBAR_DATA_DIR` ue Bar runs as a separate instance (own lock);
  `UEBAR_WINDOWS_DEMO=1` gives the Windows plugin demo data.

### Changed
- The settings sidebar entry of a plugin page has the plugin's icon and the normal indentation.
- Bundled plugins are build-order references without target framework negotiation, so a plugin
  may target a newer Windows SDK than the app.

## 1.2.1 – clean exit, repository layout

**Plugin interface (`CoreApiVersion`): 1.0** – unchanged; SDK package `UeBar.Sdk 1.0.0`.

### Fixed
- **Exit no longer hangs.** Exiting (tray, installer, restart) runs in a fixed order – tray and
  bar, then all plugins at the same time with at most 2 s each, then the layout stores and
  helpers – and logs each step with its duration ("Exit finished in … ms: …"). A plugin that does
  not stop in time, or a start that is still running, no longer holds up the exit. Before, the
  plugins had up to 15 s each behind a blanket 5 s limit, the steps after it had no limit and
  nothing was logged. As a last line of defense an exit guard ends the process 3 s after the
  request and logs the stage it hung in.
- The tray menu's "Exit" runs after the menu has finished handling the click (it used to dispose
  the menu from inside its own click).
- Two tests that failed now and then on a busy machine are deterministic: the Home Assistant
  connection loop takes its clock from `HaClientOptions.Time` (ping interval, pong limit, network
  debounce, local recheck), the tests use a fake clock and wait on signals instead of fixed
  delays. The layout-sync test creates its store before the connection starts.

### Changed
- **Repository layout:** official plugins live in `plugins/<Name>/` with `src/`, `tests/`, their
  own README (English and German) and images – `plugins/HomeAssistant`, `plugins/Dummy`. The main
  README is shorter and lists the plugins in a table; everything about Home Assistant (token,
  addresses, tiles, notifications, Assist, users without admin rights) moved to
  [plugins/HomeAssistant/README.md](plugins/HomeAssistant/README.md).
- The release workflow uploads the test results when it fails.

## 1.2.0 – plugins

**Plugin interface (`CoreApiVersion`): 1.0** – first version with major/minor; the SDK package is
`UeBar.Sdk 1.0.0`.

### New
- **Plugins.** Modules are no longer built into the app: every module is a folder with a
  `plugin.json` and its DLLs, loaded at start-up – a new module no longer needs a new ue Bar.
  Home Assistant is the first plugin (plugin ID = former module ID, so settings, token, layout
  and the notification registration carry over unchanged).
- **Settings → Plugins** (replaces "Modules"): plugins that come with ue Bar and plugins you added,
  with status, version, author and license; switch on/off, details (description, interface
  version, folder, checksum), open folder, **Add plugin …** (ZIP or folder), **Remove**. Plugins
  that do not fit or are broken are listed with the reason. Changes that need a restart show
  **Restart now**.
- **Trust:** plugins that come with ue Bar are checked against a checksum list written at build
  time and load without asking. Every other plugin asks once – name, author, version, folder and
  a clear warning – and asks again whenever its files change. Decisions are stored in
  `%APPDATA%\ueBar\plugin-trust.json`.
- Every plugin runs in its own `AssemblyLoadContext` (its own dependencies, shared SDK and
  framework). Missing files, wrong formats, an exception in the constructor, a wrong interface
  version or a duplicate ID never stop the app: the plugin gets the state "Error" with a reason
  and its tiles keep a placeholder.
- **Plugin SDK:** new project `UeBar.Sdk` with all contracts, also as a NuGet package
  (`UeBar.Sdk.<version>.nupkg` in the release). The package brings the build logic: it copies
  `plugin.json`, and `dotnet build -t:PackPlugin` produces the plugin folder and ZIP.
- Sample plugin `samples/HelloPlugin` (a counter tile, settings, two languages) and the guide
  [docs/WRITING-A-PLUGIN.md](docs/WRITING-A-PLUGIN.md).
- Release files: also the SDK package, the sample plugin and the test module as plugin ZIPs.
- `ueBar.exe --settings[=page]` opens the settings after the start; `UEBAR_CULTURE` sets the format
  culture (for screenshots and tests).

### Changed
- `UeBar.Core` keeps the implementation (registry, guard, bus, stores, plugin loader) and
  references the SDK. The contracts moved from the `UeBar.Core.*` namespaces to `UeBar.Sdk`.
- The test module is no longer part of the installer or the portable version (debug builds and a
  separate ZIP for developers only).
- Portable version: the bundled plugins are embedded and unpacked once per version to
  `%LOCALAPPDATA%\ueBar\builtin-plugins\`.
- The installer removes the module DLLs of older versions and replaces `plugins\` as a whole.
- Quick command `+ …`: no built-in default to-do list any more – without a choice the first list
  is used. An existing choice stays.
- About page: the repository button links to GitHub.

## 1.1.1 – first public release

- Slide-out bar at the top edge of the screen: pill on dwell, panel with tabs and a tile grid,
  pin and move, multi-monitor, hidden in fullscreen apps, OLED protection (dimming, pixel shift).
- Home Assistant module over WebSocket: local address first with fallback to the external one,
  reconnect with backoff, live states of the tiles in the layout only.
- Tiles: lights, switches, scripts/scenes/buttons, climate (also IR climate from three entities),
  media players with cover and controls, vacuums, sensors, selects; immediate (optimistic)
  feedback with automatic correction.
- Edit mode in the panel: add tiles by search with slot suggestions, resize, move between tabs,
  tabs per device or for all devices, visibility at home/away, status line editor, undo.
- First start: suggested layout from the Home Assistant areas.
- Layout sync per Home Assistant user with a local copy, offline changes and conflict backups;
  import/export as JSON.
- Notifications through the mobile app integration (`notify.mobile_app_…`), history, respects
  Windows "Do not disturb".
- Quick search with a global hotkey, quick command `+ …` for the shopping list, Assist field
  (pipeline of the UI language, conversation context), Now Playing in the pill.
- Settings window (general, bar, OLED, notifications, layout, modules with generated pages,
  diagnostics, about), welcome assistant, autostart.
- German and English, switchable at runtime; numbers and times follow the Windows region.
- Module architecture: core without WPF, tiles as JSON-serializable elements, fault protection
  with automatic restart, module bus with permissions.
- Installer (German/English, downloads the .NET 10 Desktop Runtime if needed) and a portable
  single executable.
