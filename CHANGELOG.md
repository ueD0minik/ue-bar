# Changelog

## 1.6.0 – Hide from screen captures

New function; plugin interface unchanged (1.7) – plugins do nothing for it.

### New
- **Hide from screen captures** (off by default): every window of ue Bar – bar with pill, pins, the `Win + V` choice,
  quick search and Assist, settings and welcome, own notifications and feedback, the pin list, context menus,
  tooltips and popups, edit dialogs, the tray menu – is missing when the screen is shared or recorded (Teams, Discord,
  OBS, Snipping Tool); the capture shows what lies behind. Applies at once to all open windows and to every window that
  appears later, without a restart. Switch it under **Settings → General → Privacy**, in the **tray menu** (with a check
  mark), with the quick command **`privat`** (`privat on` / `privat off`), or through the bus action
  `privacy.capture_hidden` (`{ "state": "on" | "off" | "toggle" }`, with permission – e.g. from a Home Assistant
  automation). While it is on, a crossed-out eye in the header of the open panel and the tray tooltip say so (nothing
  while collapsed). Windows 10 2004 or newer hides the windows completely, older versions show a black area (the
  settings say so). Not covered: Windows' own dialogs (Save as). The own screenshot (`Win + Alt + S`) keeps working –
  its picture simply has no ue Bar in it. No protection against malware under the own account.

### Fixed
- **A terminal window popped up after waking up:** it was the hang dump – the runtime wrote it with its console program
  `createdump.exe`, and Windows opened a terminal for it (25./26.11., exactly at the times of the false alarms that 1.5.2
  already fixed). Dumps of real hangs are now written by a hidden second start of `ueBar.exe` (`CreateNoWindow`, no
  console) with `MiniDumpWriteDump` – same content (stacks of all threads), no window. The packages
  `Microsoft.Diagnostics.NETCore.Client` and its two `Microsoft.Extensions.*` dependencies are gone from the install.

### Checked for real
- Screen copies from outside in a test instance: with the setting on, pin, settings, the `Win + V` choice and the action
  feedback are missing (only the background behind them); off, they are there; switching on → off → on works at once.
  A newly opened `Win + V` choice is not even on a single frame (copied ~50 times per second while it opens). The
  screenshot overlay stays usable, its frozen picture has no pin in it, `Ctrl + A` saves the whole screen.
- A real hang in the test instance: dump in 0.9 s (24 MB, as before, stacks readable with `dotnet-dump`), no window.

### Measured
- Memory, third load round with a **fixed history size** (20 entries, filled before the start; release build; each
  round 200 × the choice popup in Edge, 20 Full-HD images, 50 window screenshots): one minute after the load
  **74.3 MB (round 1), 84.2 MB (round 2), 85.7 MB (round 3)** private, two minutes after 76.0 / 87.1 / 86.0 MB. The
  growth stops after round 2 – a level the app settles at after heavy use, not a leak. Nothing to fix.

### Tests
- Bar +8 (way per Windows version, all open windows on/off live, new windows, fallback to black and greyed out,
  overlay and Windows dialogs left alone, the bar check, the dump start without a window and its arguments),
  Core +13 (quick command `privat`, bus action with permission, reserved ID `privacy`). Total 876.

## 1.5.2 – A click takes the window, Ctrl + A takes the screen, no false hang alarm after sleep

Fixes only; plugin interface unchanged (1.7).

### Fixed
- **A click in area mode (`Win + Alt + S`) now takes the window** under the mouse the same way as the window mode –
  its own content without what lies in front (Windows.Graphics.Capture, else `PrintWindow`, else cut with a notice).
  The window is framed while the mouse moves over it; dragging (from 5 px) still takes everything visible in the
  rectangle. A maximized window no longer brings its 8 px beyond the monitor into the picture.
- **Transparent corners on the clipboard:** the PNG keeps them; programs that only read the old bitmap format get the
  corners in the window's color instead of black.
- **False alarm "UI thread not responding for 3255 s" after sleep:** the watchdog pauses while Windows sleeps and starts
  over after a long gap between its own ticks (Modern Standby pauses the whole app) – no report, no memory dump. A dump
  that fails no longer leaves an empty file; empty and surplus dumps are removed at start.
- **Memory after heavy use:** screenshots (a frozen copy of every monitor), clipboard images, the choice popup and the
  caret lookup left native memory behind that an idle app did not free for hours (151 MB on 26.11.). 5 s after such
  work the memory is cleaned once (collect, finalizers, give back to Windows).
- **Start right after logging in (30–50 s):** registering the plugins no longer blocks the UI thread (it took up to 10 s
  per plugin while Windows was busy logging in), no hang dumps while starting, and the autostart entry now starts with
  `--autostart`: the app waits until it is 10 s old before it starts working. Existing entries are updated.

### New
- **`Ctrl + A` while picking a screen area** takes the whole monitor under the mouse (in its scaling); the bar follows as
  usual, so `Ctrl + A` → `Ctrl + C` copies the whole screen. The hint at the top says so.

### Measured
- Load in a test instance (release build), twice in a row: 200 × the choice popup with the caret lookup in Edge,
  20 Full-HD images on the clipboard, 50 window screenshots by click. Idle 58.8 MB private, peak 112–129 MB,
  **one minute after the load 74.5 MB (round 1) and 78.9 MB (round 2)** – part of the +9 MB is the history itself.
  All 400 popups at the caret, all 100 clicks taken as the window's own picture (140–180 ms).

### Tests
- Bar +8 (click or drag at the threshold, Ctrl+A picks the right monitor with size and scale, maximized window without
  the overhang, corners for old programs, the watchdog: real hang once, 54 minutes of sleep, suspend/resume, display
  off). Total 855.

## 1.5.1 – Window screenshots without overlapping windows, history at the text cursor everywhere

Fixes only; plugin interface unchanged (1.7).

### Fixed
- **Screenshot "Window" showed windows lying in front of it** (e.g. a small chat window over a maximized browser): it
  was cut from the frozen screen. Now the window's own picture is taken – Windows.Graphics.Capture (one frame, no
  recording, no cursor, without the yellow frame where Windows allows it), else `PrintWindow` with
  `PW_RENDERFULLCONTENT`. Only if both fail (some protected or GPU content) is it cut from the screen as before, and
  the bar says "overlapping windows are in the picture". No invisible frame, no shadow; rounded corners on Windows 11
  are transparent. Area mode is unchanged.
- **The clipboard history appeared at the mouse in Chrome, Edge, Electron apps (Claude app, Obsidian, Discord,
  Spotify …), Explorer's address bar and search box, Windows search and the search box of Settings.** These apps do not
  report their text cursor the old way. Now: old way first (Notepad, Office, Photoshop stay exactly as they were), then
  accessibility – the caret object (MSAA) and the UI Automation text pattern of the focused element, on its own thread,
  at most 150 ms, only when opening, never moving the focus – then the mouse. A lookup that hangs is given up and never
  started twice.

### Measured (by hand on this PC)
- Window capture with Windows.Graphics.Capture: own test windows and Edge (also after 3 s completely covered, as under
  the picker) – only the clicked window, 150–220 ms. `PrintWindow` as the second way also leaves out what is in front.
- Text cursor: Edge 16–48 ms (caret object), Explorer address bar and search box, Windows search, Settings search box
  3–5 ms (100–145 ms on the very first call after a start).

### Tests
- Bar +10 (which windows can be picked, cutting off the invisible frame – also when the capture already has the visible
  size, as found in the real test –, corners, empty captures, order of the capture ways; caret order, 150 ms limit,
  no second lookup while one hangs, implausible positions). Total 847.

## 1.5.0 – Clipboard history with Win + V

**Plugin interface (`CoreApiVersion`): 1.7** – extended (SDK package 1.7.0); plugins for 1.0–1.6 keep loading.

### New
- **Clipboard plugin** (`clipboard`, comes with ue Bar, on by default): text (with HTML/RTF), images (up to
  10 MB) and file lists from Explorer (paths only) are recorded – with time, app (name and icon), kind and size.
  The same content again only moves its entry to the top. Limits: 100 entries, 7 days, 200 MB in total; kept
  entries (★) are exempt. All configurable.
- **`Win + V` opens the history** (taken over from Windows, can be switched off; `Win + Alt + V` always works).
  It appears at the text cursor of the window you were in: `Enter`/click pastes there, `Shift + Enter` as plain
  text, `Ctrl + C` only copies, `Ctrl + M` keeps (★), `Ctrl + P` pins, `Delete` deletes, typing searches. Pasting
  from the history does not add a new entry.
- **Private content stays private:** what password managers mark (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory` = 0, `Clipboard Viewer Ignore`) is never read; excluded apps (KeePass,
  KeePassXC, Bitwarden, 1Password and more, configurable) are never stored; pause in the tray, on the tile, with
  `cb pause` or in the settings (a restart ends it); clear with or without the kept entries.
- **Stored encrypted (DPAPI, current Windows user)** in `%APPDATA%\ueBar\modules\clipboard\`, kept across restarts,
  never synchronized, never in the layout or the log (the log says kind and size only). Deleting an entry deletes
  its files at once; stray files are removed at start.
- **Tile** "Clipboard" (latest entries – tap copies, "All" opens the history, pause), **quick command** `cb <search>`
  (Enter copies), `cb pause`, `cb clear`; **bus:** `clipboard.changed` (metadata only), `clipboard.copy_text`,
  `clipboard.recent` (contents only if the user allows it).
- **SDK 1.7** for every plugin: `IChoicePopup` (a popup list with search, previews, groups and keyed actions at the
  text cursor), `IPasteService` (focus back + `Ctrl + V`), `IClipboardWatcher` (event driven, privacy marks
  honored), `IClipboardWriter` (text, HTML, RTF, image and files at once; tells the watcher who wrote),
  `IHotkeyTakeoverService` (hotkeys Windows keeps for itself, with a lean keyboard hook on its own thread),
  `ITrayMenuProvider` (entries in the tray menu).

### Changed
- `IClipboardAccess` (1.5) now writes through the new writer, so a clipboard watcher knows which plugin wrote.

### Measured
- Release build, collapsed, two instances side by side (one without the plugin): with 100 entries of which 10 are
  Full-HD images 56 MB private / 153 MB working set – the one without the plugin 58 / 150 MB. CPU 0.2–0.5 s per
  minute in both (no measurable difference). History on disk 3.2 MB.
- Images leave large buffers behind that an idle app would keep for a long time: 5 s after the last image the
  memory is cleaned once (without that: +35 MB after 10 Full-HD images).
- Installer 7.8 MB (+0.14 MB).

### Tests
- Clipboard +26 (all kinds, duplicates, limits and kept entries, password-manager marks and excluded apps, pause,
  encryption and restart, deleting and orphans, Win+V on/off/failed, popup keys, pasting, plain text, pinning,
  quick command, tile, bus, no content in the log, memory with 100 entries, loading as a plugin), Bar +9 (keyboard
  hook filter and schedule, popup keys, search, placement, privacy marks), Core +5 (contract 1.7 through the
  guard). Total 837.

## 1.4.3 – Text recognition measured on real screenshots

Fixes only; plugin interface unchanged (1.6).

### Fixed
- **Small screen text is read better:** captures are enlarged with linear instead of Fant interpolation.
  On real screenshots (a Claude Code window, a light web page, a dark list) wrong words per 100 went
  from 9.8 / 11.4 / 11.2 to 7.9 / 3.5 / 7.7. Under 0.2 s per capture.
- **A bullet in front of a quote** ("• „Absatz“ …") was not recognized at all with the old scaling – it is now.
- **Punctuation on its own** ("genannt ,", "( Planung )") sticks to its word – a spacing rule, no text is
  changed. There is still no dictionary correction: "rn" read as "m" ("Kern" → "Kem") and a missing dot
  ("1.4.2" → "1.42") remain – they are already in Windows' result, enlarging (up to ×3), grey and inverting
  did not change them.

### Measured
- Grey (against ClearType fringes), inverting dark images, cubic interpolation, ×2.5/×3 and a second,
  enlarged pass were measured on the real screenshots and were not better (or took more than 1 s). A Word page
  with formatting marks shown loses whole paragraphs in Windows' recognition with every preparation.
- The real screenshots stay private; their tests run only where they are (skipped on the build server).

### Tests
- Screenshot +3 (real screenshots: the shipped preparation is the best and under 1 s, the bullet case; the
  order-free error rate; punctuation). The comparison on the made-up pictures of 1.4.2 is gone – the real
  screenshots decide, and the build server can read only part of the made-up set. Total 797.

## 1.4.2 – More reliable text recognition, keys for the picker bar

**Plugin interface (`CoreApiVersion`): 1.6** – extended by one property (SDK package 1.6.0); plugins for
1.0–1.5 keep loading.

### Fixed
- **Running text broke mid-paragraph** when one line held " - " and another ": " (1.4.1 took that for a
  table). A line that reaches the right edge of its paragraph wrapped and is always joined; a table or
  vocabulary list needs at least two rows in a row with the same separator (two of them ending clearly
  before the edge) or a column gap at the same position.
- **Lists:** Windows returns a bullet "•" as its own line (after its text) and sometimes one screen line in
  pieces. These pieces are put back together, so a wrapped item stays one item (hanging indent), there is no
  empty line inside a list, and no word or number gets lost.
- **Spaces:** a space only where the word boxes really have a gap – no more "hast ." or "Vokabel liste".
- **Type size** (headings) is compared by width per character; lines without letters below the baseline no
  longer look like smaller type.

### New
- **Keys at the picker bar:** `Ctrl + C` copy, `Ctrl + S` save as …, `Enter` the default action, `Esc`
  cancel – only while the bar is shown, named in the tooltips. SDK 1.6: `ScreenAreaAction.Shortcut`.

### Measured
- 11 test screenshots with demo text (dark terminal, dark editor, dark chat, light web page, slide, small
  text at 100 %, vocabulary, whole-window captures), wrong words per 100: 4.8 with the text as Windows gives
  it (1.4.1) → 2.4 now. Inverting dark images (3.2) or a second, enlarged pass (2.8) did not read better – they
  stay off (switchable for measuring). Under 1 s per capture (17–57 ms).

### Tests
- Screenshot +29 (quality and structure of all 11 screenshots through the real Windows OCR, the reported
  cases as regression tests), Bar +4 (picker keys). Total 794.

## 1.4.1 – Text pins, paragraphs of recognized text, start

Fixes only; plugin interface unchanged (`CoreApiVersion` 1.5, SDK 1.5.0).

### Fixed
- **Text pins can be resized** at every edge and corner (image pins too), with a minimum size; the text
  wraps to the width and scrolls when it is longer. The size is kept, also after a restart. ue Bar now
  resizes pins itself – Windows did not size the borderless window reliably.
- **`Ctrl` + mouse wheel** over a text pin changes the font size (it never arrived: the scroll viewer took
  every wheel turn). Without `Ctrl` the wheel scrolls the text. The size is kept.
- **Paragraphs of recognized text:** instead of Windows' plain lines, the structure is rebuilt from the
  line positions – a new paragraph at a clearly larger gap than the usual line pitch (median), at a
  heading in larger letters or another indent; bullets and numbers (`•`, `-`, `1.`, `a)`) always start a
  new line; two columns are read one after the other. New setting **Format of the recognized text**:
  running text (default – lines of a paragraph joined, hyphens at line ends removed, table and
  vocabulary lines kept) or lines as in the image. The same for copying and text pins.
- **Start:** plugin discovery opens no DLL of an unchanged plugin any more (the check of the entry
  assembly is remembered with its checksum). On a test PC discovery took 15 s each time – the same
  moments the virus scanner reported a cloud lookup. Every sub-step of discovery is measured (Debug,
  Info when ≥ 1 s), and a still-running discovery says after 3 s which step it is in. Plugins may take
  45 s instead of 15 s to load, so a scan of their DLLs cannot make them fail.

### Tests
- Screenshot +13 (paragraphs, headings, hyphens, lists, vocabulary and table lines, columns – with
  made-up boxes and with three test slides through the real Windows OCR), Bar +12 (resizing at edges
  and corners, minimum size), Core +3 (remembered entry check, a changed DLL is checked again, load
  time limit). Total 761.

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
