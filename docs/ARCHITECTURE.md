# Architecture

ue Bar is split into a **core** and **modules**. The core provides the bar, tabs, the tile grid,
layout storage, settings, search, notifications and the status line. Modules provide content.
Since 1.2.0 every module is a **plugin**: a folder with a `plugin.json` and its DLLs, loaded at
start-up. Home Assistant is just one plugin. The guiding rule:

> The app must start and run without the Home Assistant plugin. A new module (Windows, phone,
> MQTT, Spotify …) needs no change to the app or the core – only a new plugin project that
> references `UeBar.Sdk`.

## Projects

| Project | Role | References |
|---|---|---|
| `UeBar.Sdk` | the **contract** with plugins: interfaces, models, tile elements, `CoreApiVersion` – no implementation; also shipped as a NuGet package | nothing (no WPF, no NuGet packages) |
| `UeBar.Core` | the implementation: plugin loader, module registry, fault protection, module bus, layout, settings, logging, localization | Sdk |
| `UeBar.Bar` | pure bar logic: state machine, geometry, monitor choice, hotkeys, notification queue, OLED protection | Core |
| `UeBar.App` | the WPF app: bar window, panel, rendering of tiles, settings window, tray, Win32 interop | Core, Bar – **no plugin** |
| `UeBar.Modules.HomeAssistant` (`plugins/HomeAssistant/src`) | plugin `homeassistant`: Home Assistant over WebSocket – tiles, search, status line, notifications (mobile app), Assist, layout sync | Sdk only |
| `UeBar.Modules.Dummy` (`plugins/Dummy/src`) | plugin `dummy`: test module with in-memory devices (debug builds only; a separate ZIP for developers) | Sdk only |
| `samples/HelloPlugin` | sample plugin for plugin authors | Sdk only |

### Repository layout

```
src/        UeBar.App, UeBar.Bar, UeBar.Core, UeBar.Sdk
tests/      tests of the app, bar and core (+ test plugins for the loader)
plugins/
  HomeAssistant/   src/, tests/, README.md, README.de.md, images/
  Dummy/           src/, README.md, README.de.md
samples/HelloPlugin/
docs/       ARCHITECTURE.md, WRITING-A-PLUGIN.md, images/ (the bar and the core only)
```

**Official plugins** – the ones that come with the installer or are maintained with ue Bar – live
in this repository under `plugins/<Name>/`: they ship with the app, are built against the same SDK
version and are tested together with it. Each has its own README (English and German) with
everything specific to it. **Plugins by others** live in their own repositories and only need the
SDK package.

Plugins never reference each other or the app. The app only builds the bundled plugins first and
copies them to `plugins\<id>\` next to it (`build/BundledPlugins.targets`).

## Plugins

A plugin is a folder `<id>\` with `plugin.json` (id, name, description, version, author, license,
homepage, `sdkVersion`, `entryAssembly`), its DLLs and its language files. It is found in two
places:

| Where | What |
|---|---|
| `<app folder>\plugins\` | **bundled** plugins (Home Assistant), with a checksum list `bundled.sha256` written at build time |
| `%APPDATA%\ueBar\plugins\` | plugins the **user** added (Settings → Plugins → Add plugin …) |

The portable single exe carries the bundled plugins inside as a ZIP and unpacks them once per
version to `%LOCALAPPDATA%\ueBar\builtin-plugins\<version>-<checksum>\`. Their checksum list is read
from the exe, not from the unpacked folder, so files changed afterwards are noticed.

Loading (`PluginHost` in the core), only at start-up:

1. **Discover** every folder and read `plugin.json` – no plugin code runs. Checks: required fields,
   the entry DLL exists and is a .NET assembly built against `UeBar.Sdk`, no reference to app
   internals, the interface version fits. A user plugin may not take the ID of a bundled one; of
   two plugins with the same ID only the first counts.
2. **Trust**: a bundled plugin whose files match `bundled.sha256` loads without asking. Every other
   plugin needs the user's permission ("This plugin has full access to your PC …"); the decision
   is stored with the checksum of its code files in `plugin-trust.json`. Changed files → ask again.
3. **Load**: each plugin gets its own `AssemblyLoadContext`. `UeBar.Sdk` and everything the app
   already has (the framework) come from the app – otherwise `IModule` would be two different
   types. The plugin's own dependencies and language files come from its folder. Exception (1.4.0):
   the C#/WinRT runtime and projection (`WinRT.Runtime`, `Microsoft.Windows.SDK.NET`) register
   process-wide state and may exist only once – the highest version any plugin brings is loaded into
   the app's context and shared (`ProcessWideAssemblies`). The bundled plugins bring them only once,
   in `plugins\.shared\` (listed in `bundled.sha256`; used only if every file matches). The module
   class is created on the thread pool with a time limit.

Discovering runs off the UI thread. Checksums of unchanged files (same full path, size, last-write and
creation time) are kept between starts in `%APPDATA%\ueBar\plugin-hashes.json`; a changed file is always
hashed again, and the checksum itself is still compared with `bundled.sha256` or the trust store. With
`UEBAR_LOG_LEVEL=Debug` the log shows every start step with its duration.
4. **Register**: loaded modules go to the module registry. Plugins that are off, not allowed,
   incompatible or broken are registered as "not loaded" with the reason, so their tiles keep a
   fitting placeholder.

Nothing about a plugin can stop the app: every problem becomes the state "Error" (or
"Incompatible") with a reason, shown in Settings → Plugins and once in the tray. Plugins are never
unloaded while the app runs: switching a loaded plugin off stops it right away; switching it on
again, adding, updating or removing a loaded one takes effect after a restart ("Restart now").


## Exit

The app ends in a fixed order, each step timed and logged in one line ("Exit finished in … ms:
tray …, bar …, plugins …, layout …, helpers …, single instance …"):

1. tray icon and bar window (nothing new can be triggered),
2. all plugins **at the same time**, each at most **2 s** (waiting for a start or restart that is
   still running included) – a plugin that does not make it is left behind with its stop token
   cancelled and a warning in the log,
3. layout stores and background helpers (off the UI thread, with a time limit),
4. WPF shutdown.

An exit guard ends the process if it still runs 3 s after the request and logs the stage it hung
in. Plugins get `StopAsync` and their stop token; they must not rely on more time than that.

## Interfaces in `UeBar.Sdk`

| Interface | Purpose |
|---|---|
| `IModule` | `Id`, `DisplayName`, `Icon`, `Status`, `StartAsync`, `StopAsync`, the module's tile types |
| `ITileType` | a tile type (e.g. `ha.light`): name, icon, allowed sizes, slots it needs (e.g. `main`, `power`, `target`), creates a `TileModel` from a `TileConfig` |
| `IModuleContext` | given to `StartAsync`: the module's bus access, logger, stop token, `RunBackground`, data folder |
| `IEntitySource` | searchable, observable things with a default action (edit mode, quick search, tile suggestion) |
| `IStatusProvider` | small values for the status line |
| `INotificationSource` | notifications; the core shows them no matter where they come from |
| `IAssistProvider` | takes text, returns an answer |
| `IModuleSettings` | the module's settings as fields; the core builds the settings page and stores secrets encrypted (DPAPI) |
| `ILayoutStore` / `ILayoutStoreProvider` | where the synchronized base layout lives (local file in the core, Home Assistant in the HA module) |
| `IModuleBus` | communication between modules, only with permission |

Further capabilities are separate optional interfaces (`IModuleDiagnostics`,
`IQuickCommandProvider`, `INowPlayingSource`, `IImageSource`, `IVisibilityAware`,
`IDeviceBatterySource`, `IUiLanguageInfo` and `IDeviceBatteryCatalog` on the context …). Modules
implement only what they need; the core checks with `is`/`as`.

Two of them keep collapsed ue Bar cheap: `IVisibilityAware` tells a module what of it is on screen
(the tiles of the open tab, the status line), so it can poll only while something is visible; the
app passes on only changes. `IDeviceBatterySource`/`IDeviceBatteryCatalog` let one plugin show the
device batteries another plugin knows (e.g. a vendor plugin for a wireless dongle) – the core
collects them, the plugins never see each other.

Contract 1.3 (1.3.2) adds what the quick access plugin needed – all generic, none of it knows that
plugin:

- `ILiveModuleSettings`: a settings change is applied to the running module
  (`ModuleRegistry.ApplyChangedSettingsAsync`); modules without it are restarted as before.
- Settings fields `File`, `Folder` (the app shows the dialog) and `List` (entries with item fields),
  plus `ShowWhen` on any field.
- `IDropHandler`: something dropped on the panel in edit mode goes to the first running module that
  turns it into a tile (`ModuleRegistry.CreateTileForDropAsync`); the app adds the tile.
- `UeNotification.Priority`: important notifications pass a paused bar, "Do not disturb" and the
  fullscreen block as far as the bar settings allow (`NotificationGate` in `UeBar.Bar`).
- `IconGridElement` and `TileModel.ImageUrl` shown as the tile's icon.

Contract 1.4 (1.3.3): `ITileItemEditor` – a module lets the user edit the entries of a tile in edit
mode; it returns a changed copy of the `TileConfig`, the app gives it to the layout editor
(`TileItemEditing` in the core), so undo, cancel and sync work as for every edit. `TileConfig.Options`
holds own values of a tile. `ISettingListPresenter` shows a `List` setting compact and grouped.

Contract 1.5 (1.4.0) – pins and screenshots, all generic:

- **Pins** are a core feature. `PinManager` (core, WPF-free) keeps them in `%APPDATA%\ueBar\pins\`
  (`pins.json` + one image file per image pin) – never in the layout, never synchronized. Placement per
  monitor: offset from the monitor's corner in physical pixels, size in device-independent pixels; a
  missing monitor means the primary one (`PinGeometry` in `UeBar.Bar`). The app draws one `PinWindow`
  per visible pin (`PinController`): topmost, no taskbar, no focus except while editing text; snapping
  through `WM_MOVING`/`WM_SIZING`; dimming and pixel shift from `PinGlow` with a timer that runs only
  until the next change is due; fullscreen check every 2 s only while pins are visible. Modules use
  `IPinService` on their context (own pins only); the user uses the pin list in the panel, the hotkey,
  the quick command `pin` and the bus action `pins.create_text` (owner `pins`, a reserved ID).
- **Picking a screen area** (`IScreenAreaPicker`, `ScreenAreaPicker` in the app): hide the bar, capture
  the whole desktop once with GDI (the app is per-monitor DPI aware → physical pixels), one opaque
  overlay window per monitor showing its part of the frozen image. The mouse is read in physical pixels;
  each overlay maps them to its own device-independent pixels from its real size (`ScreenSelection`),
  so rectangles across monitors with different scaling are right. Optional action bar at the area.
  Hiding the bar goes through `CaptureSafety` (`UeBar.Bar`): whatever happens (action, Esc, error,
  exception) the bar is restored afterwards and the overlays are closed. Every switch to pill/panel checks
  with `BarWindowHealth` that Windows really shows the bar (visible, not cloaked, opacity, topmost, no
  display affinity other than the wanted one) and repairs it with a warning otherwise; "Reset bar"
  restores all of it.
- **Hide from screen captures** (1.6.0): `CaptureProtection` (app) sets the display affinity
  `WDA_EXCLUDEFROMCAPTURE` (Windows 10 2004+, else `WDA_MONITOR` = black) on every top-level window of the
  process. One place for all, only while the setting is on: a thread hook (`WH_CALLWNDPROC`) on the UI thread
  catches `WM_SHOWWINDOW` / `WM_WINDOWPOSCHANGING` with `SWP_SHOWWINDOW`, i.e. before Windows shows the window
  (the out-of-context WinEvent alone came one frame late), and a WinEvent hook on `EVENT_OBJECT_SHOW` for the
  own process catches windows of other threads; both hand the window to `CaptureShield` (`UeBar.Bar`, tested
  with a fake);
  switching applies to all open windows at once. Exceptions: the picking overlay (shows the frozen
  picture, in which the hidden windows are already missing) and Windows dialogs (`#32770` and windows they
  own). Switches: settings, tray, quick command `privat`, bus action `privacy.capture_hidden`
  (`CapturePrivacyCommands` in the core, owner `privacy`, a reserved ID).
- **Hang dumps** (1.6.0): the UI watchdog starts the own exe hidden (`ueBar.exe --hang-dump <pid> <file>`,
  `CreateNoWindow`), which writes the dump with `MiniDumpWriteDump` from outside and ends (handled in the
  static constructor of `App` before anything else). The runtime's own way started the console program
  `createdump.exe`, and Windows opened a terminal window for it.
- **Pins that let clicks through** get no mouse events. The existing mouse polling watches them
  (`PointerWatch`, only while such pins exist): over one it lights up, with Ctrl held it can be used for a
  moment.
- **Feedback on the user's own action** (`UeNotification.IsActionFeedback`): `NotificationGate` lets it pass
  "Do not disturb" (setting), the app shows it as a small window where the action bar was
  (`ActionFeedbackToast`).
- **"Save as"** (`IFileSaveDialog`): the Windows dialog on the UI thread; the last folder per owner is a
  local setting (`SaveFolders`).
- **Clipboard** (`IClipboardAccess`): write only, on the UI thread.
- **Hotkeys of plugins**: `SettingFieldKind.Hotkey` + `IHotkeyHandler`; the app registers them while the
  plugin runs (`PluginHotkeys`, `GlobalHotkeys` with its own message window, logic in `HotkeySet`). The
  shared recording field pauses every own hotkey while recording.
- `SettingFieldKind.Info` (a hint) and `ISettingListTiles` (a list entry becomes a tile; the app puts it
  into the layout through `LayoutCoordinator.AddTileAsync`).

Contract 1.6 (1.4.2): `ScreenAreaAction.Shortcut` – a key for a button of the picker bar ("Ctrl+C"),
handled by the overlay while the bar is shown (`PickerKeys` in `UeBar.Bar`), never registered system-wide.
Recognized text (Screenshot plugin, `OcrLayout`) is rebuilt from the word boxes: pieces of one screen line
are joined (Windows returns a bullet "•" as its own line), spaces only where the boxes have a gap, type size
by width per character, hanging indents of list items, tables only for runs of rows with the same structure.

Contract 1.7 (1.5.0) – what a clipboard history needs, all generic (the Clipboard plugin uses nothing else):

- **Watching the clipboard** (`IClipboardWatcher`, `ClipboardWatcher` in the app): a message-only window with
  `AddClipboardFormatListener`, created only while some plugin watches. After `WM_CLIPBOARDUPDATE` it waits
  120 ms (apps write in steps, one read per change, `GetClipboardSequenceNumber`), then checks the privacy
  marks **before reading anything** (`ClipboardPrivacy` in `UeBar.Bar`: `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory` = 0, `Clipboard Viewer Ignore` → nothing is read, nobody is told). It reads only
  what the watchers asked for – images only without text or files, size limits – and the source app from
  `GetClipboardOwner` (or the window in front; never our own process). Image pixels are copied to plain bytes on
  the UI thread; PNG and preview picture are encoded on the pool. The change goes to each module on the pool
  through the guard; stopping the module ends its watch. The log holds kind and size only.
- **Who wrote it:** `IClipboardAccess` and `IClipboardWriter` write through `ClipboardAccess` in the app, which
  remembers the sequence number after its write together with the module; the watcher reports it as
  `WrittenByModule` – so a history knows its own pasting.
- **Pasting** (`IPasteService`, `PasteService`): content to the clipboard, `SetForegroundWindow` on the target (we
  may, because our popup had the focus; fallback `AttachThreadInput`), wait until Shift/Ctrl/Alt/Win are released
  (at most 1.5 s, then they are released for the target), `Ctrl + V` with `SendInput`.
- **Choice popup** (`IChoicePopup`, `ChoicePopupHost`): a small WPF window in the look of the quick search,
  activated (it takes the keyboard), topmost, closes when it loses the focus. Search, keys, reselection after a
  change and placement are logic in `UeBar.Bar` (`ChoiceSearch`, `ChoiceKeys`, `KeyChord`, `ChoicePlacementRules`):
  under the caret of the window in front (`GetGUIThreadInfo`), above it without room, else at the mouse, always
  inside the work area of that monitor, sized for its scale. Actions with `KeepOpen` call the module through the
  guard (5 s) and show the new list.
- **Taking over a hotkey** (`IHotkeyTakeoverService`, `KeyboardHook`): a `WH_KEYBOARD_LL` hook on an own thread with
  its own message loop, set only while a takeover exists. The callback only asks `KeyHookFilter` (pure logic:
  modifiers tracked from the events, injected keys never fire, key repeat and key up of the hotkey are swallowed)
  and returns; the press goes out on the pool. For Win hotkeys a meaningless key (0xE8) is sent while Win is still
  down, so releasing Win does not open the start menu. `HookSchedule`: tries at start, after 2 s and 10 s, then
  "failed" (the module shows its replacement hotkey); a working hook is set again every 5 minutes, because Windows
  silently removes hooks that answer too slowly.
- **Tray entries** (`ITrayMenuProvider`): read when the tray menu opens, through the guard.
- **Where the popup appears** (1.5.1, `CaretLocator` in `UeBar.Bar`, `AutomationCaret` in the app): first
  `GetGUIThreadInfo` (decisive where it gives a position), then accessibility – the MSAA caret object of the focused
  window (Chromium, Electron) and the UI Automation text pattern of the focused element (WinUI/XAML, WebView2, UWP in
  another process than its frame) – on its own thread with a hard limit of 150 ms, then the mouse. Only when opening;
  while an earlier lookup still hangs, none is started.

Window screenshots (1.5.1): in mode "window" the picker takes the clicked window's own picture instead of cutting the
frozen screen – Windows.Graphics.Capture through the plain COM ABI (`GraphicsCapture` in the app: D3D11 device, capture
item for the window, free-threaded frame pool, one frame, staging texture; no projection assembly in the app), else
`PrintWindow` with `PW_RENDERFULLCONTENT`, else the frozen screen with a notice at the action bar. The rules
(`WindowCaptureRules` in `UeBar.Bar`) cut to the DWM frame, make the picture opaque and the Windows 11 corners
transparent, and treat an all-black result as failed.
- The Clipboard plugin stores its history in its data folder, every file encrypted with DPAPI (`ClipStore`), and
  publishes only metadata on the bus.

## Tiles are data

So that a module needs no WPF code, a tile describes its content as data made from elements,
and the app renders them:

`TileModel` = title, icon, state text, `IsActive` (accent color), optional image, and a list of
elements:

| Element | Example |
|---|---|
| `TapAction` | tap the whole tile = toggle/run |
| `SliderElement` | brightness, volume, temperature |
| `StepperElement` | −/+ with a value (target temperature) |
| `ButtonRowElement` | play/pause/next/previous, start/pause/dock |
| `ChoiceElement` | choice (fan speed, input) |
| `ValueDisplayElement` | large value + unit (sensor) |
| `MediaInfoElement` | cover, title, artist, progress |
| `IconGridElement` | icons with names, e.g. several shortcuts in one tile (1.3) |

Elements are observable (values change live) and report interactions back to the module.
Elements, `TileModel` and the layout are fully JSON-serializable (no WPF or Windows types,
colors as hex strings, icons as names, images as URL or ID) – another device could show and
operate the same tiles.

## Fault protection

A faulty module must never take down the app or other modules.

- Every call from the core into a module (start, stop, create tile, interaction, search, status
  …) goes through the `ModuleGuard`, which catches and logs exceptions with the module ID.
- If a module throws: status "Error", its tiles show a placeholder, all other modules keep
  running. Automatic restart with backoff (at most 3 attempts in 5 minutes), then it waits for a
  manual "Restart".
- Calls from the UI thread have a time limit; a hanging module does not freeze the bar.
- Background work of a module runs through `IModuleContext.RunBackground` with its own
  cancellation token; unobserved exceptions are attributed to the module.
- Rules for module authors: no `async void`, no own timers or threads, thread-safe code; moving
  changes to the UI thread is the app's job.

## Module bus

Modules work together only through the bus in the core:

- A module **publishes** what others may use: actions (e.g. `ha.call_service`), events (e.g.
  `ha.state_changed`) and values.
- Another module calls or subscribes through `IModuleBus` – **only with permission**. The default
  is "denied"; permissions are per pair and direction and stored in the settings.
- Everything on the bus runs through the fault protection; if a module fails, the others get a
  clean error result instead of an exception.

**Permissions in the settings** (since 1.3.1): Settings → Plugins → *a plugin* → **May use** lists
the other plugins with what they publish right now (actions, events, values) and one switch per
provider and kind – what this plugin may use of them. Everything is off by default. A plugin can
ask for a permission (`IModuleBus.RequestPermission`); the request appears as a hint with
**Allow**/**Decline** on the plugin's page and on the Plugins page. The decision is stored in
`settings.json` (`busGrants`) and checked on every call and delivery, so switching it off takes
effect at once. The page is generic: it knows no plugin by name.

First user: the Timer plugin's Pomodoro runs a chosen action of another plugin (e.g. a Home
Assistant script) when work or a break starts – only while it is permitted.

![May use](../plugins/Timer/images/permissions.png)

## Layout

- **Base layout** (tabs, tiles, status line): stored by a module that offers a layout store
  (Home Assistant: per HA user in `frontend/user_data`), always with a local copy
  (`layout.json`). Changes made offline are uploaded later; conflicts keep the newer version and
  back up the other one.
- **Device level** (`device.json`): tabs only on this device, base tabs hidden on this device.
- **Location**: tabs can be visible only at home or only away (the HA module knows the location
  from local vs. external connection).
- Tiles of missing, disabled, refused or broken plugins stay in the layout and show a placeholder.

## Stable contracts

The interfaces in `UeBar.Sdk` are the contract between ue Bar and its plugins. The app's look and
behavior can change freely as long as the contract stays the same.

- Interfaces are only **extended** (new optional interfaces), never changed in a breaking way.
  Every extension increases the minor version of `CoreApiVersion` (and of the SDK package).
- New capabilities always come as their own optional interface, so existing plugins keep working.
- A plugin states the version it needs (`sdkVersion`). It loads if the major version is the same
  and the app's minor version is not older.
- If a breaking change is ever unavoidable, the major version is increased and noted in the
  changelog; plugins for the old major version are then not loaded.

## Threads and resources

- Collapsed, the bar renders nothing; the only timer is the mouse polling on a background thread,
  which wakes the UI thread only when needed.
- Modules report changes on any thread; the app marshals them to the UI thread. When it copies a
  tile's element list it retries if the list was changed at the same moment; a module itself makes
  sure that two of its threads never change the same tile at once.
- A UI watchdog writes a small dump to the log folder if the UI thread hangs. Its decision is `HangClock`
  (`UeBar.Bar`): paused while Windows sleeps, and a long gap between its own ticks (Modern Standby pauses the whole
  app) starts it over instead of counting as a hang. No dumps until the start has finished.
- After heavy image or accessibility work (screenshots, clipboard images, the choice popup, the caret lookup)
  `MemoryTrim` cleans the memory once after 5 quiet seconds – native buffers are freed only when their .NET objects
  are finalized, and an idle app would not collect for hours.
- Clipboard watching costs nothing between changes (a message window, no polling). The keyboard hook for a
  taken-over hotkey has its own thread that only waits for messages; its callback decides and returns at once.

## Languages

Each project has its own texts (`Resources/Strings.resx` English, `Strings.de.resx` German).
The core offers the current UI language to modules through `IUiLanguageInfo` on the module
context. Numbers, dates and times follow the Windows region. Tests check that every key exists
in both languages and that no UI text is hard-wired in the code.
