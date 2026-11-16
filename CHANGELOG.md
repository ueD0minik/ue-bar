# Changelog

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
