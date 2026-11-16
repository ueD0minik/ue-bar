# Architecture

ue Bar is split into a **core** and **modules**. The core provides the bar, tabs, the tile grid,
layout storage, settings, search, notifications and the status line. Modules provide content.
Home Assistant is just one module. The guiding rule:

> The app must start and run without the Home Assistant module. A new module (Windows, phone,
> MQTT, Spotify …) needs no change to the core – only a new project and one line of
> registration.

## Projects

| Project | Role | References |
|---|---|---|
| `UeBar.Core` | the contract (interfaces, models, tile elements) and the core services: module registry, fault protection, module bus, layout, settings, logging, localization | nothing (no WPF, no NuGet packages) |
| `UeBar.Bar` | pure bar logic: state machine, geometry, monitor choice, hotkeys, notification queue, OLED protection | Core |
| `UeBar.App` | the WPF app: bar window, panel, rendering of tiles, settings window, tray, Win32 interop | Core, Bar, all modules |
| `UeBar.Modules.HomeAssistant` | Home Assistant over WebSocket: tiles, search, status line, notifications (mobile app), Assist, layout sync | Core |
| `UeBar.Modules.Dummy` | test module with in-memory devices (off by default in release builds) | Core |

Modules never reference each other. The app registers them in one place,
`src/UeBar.App/ModuleCatalog.cs`.

## Interfaces in `UeBar.Core`

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
`IQuickCommandProvider`, `INowPlayingSource`, `IImageSource`, `IUiLanguageInfo` on the context
…). Modules implement only what they need; the core checks with `is`/`as`.

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

## Layout

- **Base layout** (tabs, tiles, status line): stored by a module that offers a layout store
  (Home Assistant: per HA user in `frontend/user_data`), always with a local copy
  (`layout.json`). Changes made offline are uploaded later; conflicts keep the newer version and
  back up the other one.
- **Device level** (`device.json`): tabs only on this device, base tabs hidden on this device.
- **Location**: tabs can be visible only at home or only away (the HA module knows the location
  from local vs. external connection).
- Tiles of missing or disabled modules stay in the layout and show a placeholder.

## Stable contracts

The interfaces in `UeBar.Core` are the contract between core and modules. The app's look and
behavior can change freely as long as the contract stays the same.

- Interfaces are only **extended** (new optional interfaces), never changed in a breaking way.
- New capabilities always come as their own optional interface, so existing modules keep working.
- If a breaking change is ever unavoidable, `CoreApiVersion` is increased and noted in the
  changelog.

## Threads and resources

- Collapsed, the bar renders nothing; the only timer is the mouse polling on a background thread,
  which wakes the UI thread only when needed.
- Modules report changes on any thread; the app marshals them to the UI thread.
- A UI watchdog writes a small dump to the log folder if the UI thread hangs.

## Languages

Each project has its own texts (`Resources/Strings.resx` English, `Strings.de.resx` German).
The core offers the current UI language to modules through `IUiLanguageInfo` on the module
context. Numbers, dates and times follow the Windows region. Tests check that every key exists
in both languages and that no UI text is hard-wired in the code.
