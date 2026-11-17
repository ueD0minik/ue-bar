# Changelog

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
