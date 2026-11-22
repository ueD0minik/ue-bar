# Changelog

## 1.4.0 – Pins and the Screenshot plugin

**Plugin interface (`CoreApiVersion`): 1.5** – extended, nothing broken: plugins for 1.0–1.4 keep
loading. SDK package `UeBar.Sdk 1.5.0`.

### New
- **Pins** (a core feature): notes and images as small windows on top of everything – borderless,
  rounded, movable (edges snap to the screen), resizable, position lockable. Images: mouse wheel =
  zoom, drag a zoomed image to pan. Text: double click = edit, Ctrl + mouse wheel = font size. Right
  click: copy, save as (images), opacity, lock position, let clicks through, hide, close.
  - **Pin list in the bar** (pin icon in the panel): show/hide single pins or all, close all, "Pin
    note". Hotkey **Win + Alt + P** shows/hides all pins (changeable); showing all also ends "let
    clicks through".
  - **Let clicks through, and back:** hold **Ctrl** while the mouse is over such a pin – it can be used
    for a moment (thin frame, right click works); release Ctrl and it lets clicks through again. A short
    hint at the pin says so when the option is switched on. Tray: "Make all pins clickable again".
    Done through the existing mouse polling, only while such pins exist – no timer of its own. A dimmed
    click-through pin also lights up under the mouse.
  - **Opacity** in the pin menu is the same slider as everywhere (drag with a live preview, no steps).
  - **OLED:** a pin dims after 60 s without the mouse (to 35 %) and lights up at once when the mouse
    comes; pixel shift every few minutes; hidden over fullscreen apps (all adjustable under Settings →
    Pins).
  - Kept across restarts in `%APPDATA%\ueBar\pins` – content, size and place per monitor (a missing
    monitor means the primary one). Never in the synced layout. **Closing a pin deletes it with its
    image;** files without an entry are removed at the start. Settings → Pins shows "Stored pins: 3
    (1.2 MB)" and opens the folder.
  - Quick command `pin <text>` (quick search and Assist), bus action `pins.create_text` (provider
    `pins`, with permission).
- **Screenshot plugin** (`plugins/Screenshot`, plugin ID `screenshot`, comes with ue Bar and is on):
  **Win + Alt + S**, a tile and the quick command `shot` capture an area (default), a window or a
  screen – also across monitors with different scaling. A small bar at the area: pin, copy, save
  (`Pictures\Screenshots\ue Bar`, dated names) or recognize text (Windows OCR, offline; copied or
  pinned). A default action skips the bar. Missing recognition language → a hint how to add one.
  Bus: event `screenshot.taken` (metadata only), action `screenshot.capture`.
  - **Save as …** (small arrow next to "Save"): the Windows dialog with a free folder and name; it
    starts in the folder picked last time (remembered per PC). "Save" still saves straight into the
    default folder.
  - The capture stays in memory: copying and recognizing text write no file, not even a temporary
    one; pinning never writes into the screenshot folder; ue Bar never deletes anything there.
  - **Feedback on your own action** ("233 characters recognized and copied · Pin", "Saved") is shown
    by ue Bar itself as a small message where the action bar was – also during Windows "Do not
    disturb" (Settings → Notifications, on by default). Other notifications are unchanged.
- **Picking a screen area** as a core function: the screen freezes, one overlay per monitor, drag a
  rectangle, click a window or a monitor, Esc/right click cancels.
- **Quick access settings** are easier: a hint at the top (tiles are made in the bar), "Also add as
  tile" (on, with the tab) when creating an entry, "Add as tile" for entries on no tile yet; the
  group "Not used" is now called "On no tile yet".
- **SDK 1.5:** `IPinService`, `IScreenAreaPicker` (+ `ScreenRect`, `ScreenMonitor`,
  `ScreenAreaRequest/Result`), `IClipboardAccess` (write only), settings field kinds `Hotkey` (with
  `IHotkeyHandler`: plugins get global hotkeys, recorded like the quick search's) and `Info`,
  `ISettingListTiles` + `SettingListRow.CanAddTile`, `IFileSaveDialog` (the core's "Save as"
  dialog), `UeNotification.IsActionFeedback`, `ScreenAreaAction.Compact`.

### Fixed before the release
- **After the first screen capture the bar no longer showed.** Hiding it for the capture set its
  opacity to 0 also when the bar was collapsed, but the restore only ran when it had been visible –
  so it stayed invisible (the state machine kept working, "Reset bar" did not help). Now everything
  changed for a capture is restored in every case (action, Esc, error, exception), the overlay windows
  are closed for real, "Reset bar" also restores opacity, cloaking, display affinity and topmost, and
  every switch to pill/panel checks that Windows really shows the window (warning in the log and
  repaired otherwise).
- **Start took 15–50 s** with the UI thread blocked (start-up hang warning, once a dump). Every start step
  is logged with its duration (Debug); the plugin folders are checked off the UI thread; checksums of
  unchanged files (path, size, times) are kept in `plugin-hashes.json`. Warm start now about 1–2 s.
- **WinRT projection only once:** the bundled plugins share one copy in `plugins\.shared\` (checked
  against `bundled.sha256` like every bundled file) instead of one per plugin – the installer is small
  again.

### Changed
- The hotkey recording field is shared by the quick search, the pins and plugin hotkey fields; while
  recording, every own hotkey pauses.
- Plugin loader: `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` are loaded once per process and
  shared by all plugins (the highest version any plugin brings). Two plugins using Windows APIs (here
  Windows and Screenshot) each loading their own copy made the second one fail.
- The module ID `pins` is reserved for the core.

### Tests
- Core +16 (pins: create/change/close, restart, broken files, limits, owner-only access through the
  SDK, bus action with permission, quick command, reserved ID, contract 1.5), Bar +18 (placement per
  monitor and scaling, missing monitor, snapping, fullscreen hiding, dimming and pixel shift, picking
  across monitors, several hotkeys), Screenshot +30 (actions, file names, events with metadata only,
  OCR with German/English test images and a missing language, quick command, hotkey, bus, plugin
  load), Quick access +1. Before the release (the fixes above): Bar +30 (every way a capture can end
  leaves the bar visible, slider dragging, Ctrl over click-through pins, feedback during "Do not
  disturb", last folder of "Save as"), Core +6 (checksum cache, shared folder checked against the list,
  pin files deleted on close and orphans at the start), Screenshot +6 (no file at all for copy and
  OCR – also with the real OCR –, pinning never writes into the screenshot folder, "Save as"). Total 733.

## 1.3.3 – Collections edited in the bar, compact settings lists

**Plugin interface (`CoreApiVersion`): 1.4** – extended, nothing broken: plugins for 1.0–1.3 keep
loading. SDK package `UeBar.Sdk 1.4.0`. From now on the app follows Semantic Versioning: the last
digit for fixes and improvements, the middle one for new features and plugins, the first one when
something breaks.

### New
- **Quick access: build collections right in the bar.** In edit mode tap the collection tile: add
  entries by search (Start menu apps and your entries), "Choose file …", "Choose folder …", "Enter
  web page …" or by dropping a file, folder, shortcut or link on the tile; reorder by dragging,
  rename, remove. The entries live in the tile (layout): they sync, and "Cancel" discards them too.
- **Compact settings lists:** one row per entry (icon, name, detail), a click opens its fields, a
  search field, groups. Quick access groups by single tiles, each collection and not used, and also
  lists what lives only in the layout.
- **SDK:** `ITileItemEditor` (edit a tile's entries in the bar), `TileConfig.Options` (own values of a
  tile, synced with the layout), `ISettingListPresenter` (how a list setting is shown).

### Changed
- The collection tile no longer asks for six slots when it is added – it starts empty and is filled
  in the bar. Old collections (also with gaps between their entries) keep working.
- The memory warning of the sign-of-life log line now starts at 150 MB (the target since 09.11.),
  not 80 MB.

## 1.3.2 – Quick access plugin, important notifications, live settings

**Plugin interface (`CoreApiVersion`): 1.3** – extended, nothing broken: plugins for 1.0–1.2 keep
loading. SDK package `UeBar.Sdk 1.3.0`.

### New
- **Quick access plugin** (`plugins/QuickAccess`, plugin ID `quickaccess`, comes with ue Bar and is
  on): Start menu apps (classic and Store, optionally as administrator), files, Office templates (a
  tap opens a new document from the template), folders, web pages and own commands as tiles with the
  real icons from Windows. A collection tile (2x2) holds up to six of them. Tiles by search in edit
  mode, by drag and drop (files, folders, `.lnk`, `.url`, links) and from own entries in the settings;
  Start menu apps and entries in the quick search. Paths below the user folder are stored as
  `%USERPROFILE%\…`, so a synced layout fits another PC; a missing target shows "Not found" and stays.
  Own commands never travel with the layout – a command tile only points to the entry of this PC.
  Bus action `quickaccess.open` (entry ID).
- **Important notifications:** `UeNotification.Priority` (normal/important). Settings →
  Notifications → "Important notifications": also while the bar is paused, during "Do not disturb",
  over fullscreen apps (all on). A paused bar lets an important pill out (it can be opened for its
  buttons) and is paused again afterwards. The timer reports a timer that ran out as important; its
  two own switches of 1.3.1 are gone – a switched-off value is taken over once (settings.json
  version 2).
- **Settings applied live:** `ILiveModuleSettings` – a module with it gets changed settings while it
  runs instead of a restart. Timer, Windows and Quick access use it (no flicker any more).
- **New settings fields:** `File` and `Folder` with "Browse…" (dialog of the app), `List` of entries
  with item fields, and `ShowWhen` (show a field only while another one has a certain value). The
  timer's own sound file uses `File` and appears only with "Own sound file".
- **Drag and drop:** `IDropHandler` – in edit mode the panel takes files, folders, shortcuts and
  links; the first plugin that turns it into a tile gets it.
- **Tile building blocks:** `IconGridElement` (icons with names); `TileModel.ImageUrl` is now shown as
  the tile's icon (a real app icon instead of the glyph).

### Changed
- After the quick search ran an action, the bar no longer pulls the previously active window back to
  the front – an app the action started keeps the focus.

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
