# ue Bar

**English** · [Deutsch](README.de.md)

A slide-out dashboard for the top edge of your Windows screen. Move the mouse to the top
edge: a small pill appears, and with it your tiles, a status line, notifications and a quick
search. Inspired by the "Now Bar" of Samsung One UI. What the tiles show comes from
**plugins** – [Home Assistant](plugins/HomeAssistant/README.md) is the first one and comes with
ue Bar; more (Windows, phone, timers …) can be added without rebuilding the app.

![Panel with tiles of an example home](docs/images/panel.png)

| Pill | Edit mode | Settings → Plugins |
|---|---|---|
| ![Pill](docs/images/pill.png) | ![Edit mode](docs/images/edit.png) | ![Settings, Plugins page](docs/images/settings.png) |

## What the bar does

- **Pill and panel at the top edge**: appears after a short dwell, stays open while the mouse is
  on it, or pin it to keep it visible. Works with several monitors and hides in fullscreen apps.
- **Tiles in tabs**, live updated. **Edit mode** right in the panel: add tiles by searching,
  resize, move (also to other tabs), tabs per device or shared, tabs visible only at home or only
  away.
- **Status line** with small values in the panel and the pill, **Now Playing** in the pill.
- **Notifications** in the panel, with a history.
- **Quick search** with a global hotkey (default `Win + Alt + Space`).
- **German and English**, switchable at runtime. Numbers and times follow your Windows region.
- OLED protection (dimming, pixel shift), light on resources when collapsed.

## Requirements

- Windows 10 (version 1809 or later) or Windows 11, x64
- .NET 10 Desktop Runtime – the installer downloads it from Microsoft if it is missing; the
  portable version does not need it
- What each plugin needs is in its README (Home Assistant: an instance reachable from the PC and
  a long-lived access token)

## Installation

Download from the [Releases](../../releases) page:

- `ueBar-Setup-<version>.exe` – installer (German/English, no admin rights needed, installs to
  `%LOCALAPPDATA%\Programs\ueBar`)
- `ueBar-<version>-portable.exe` – a single file, nothing to install
- for plugin authors: `UeBar.Sdk.<version>.nupkg` (the SDK) and `ueBar-plugin-<id>-<version>.zip`
  (plugins that do not come with ue Bar, e.g. the sample plugin)

**Windows SmartScreen:** the files are not code-signed, so Windows may show "Windows protected
your PC". Click **More info → Run anyway**. If in doubt, build it yourself from the source code
(see below).

Settings, layout and logs are stored in `%APPDATA%\ueBar`.

## First steps

1. Start ue Bar. On the first start a welcome assistant sets up the plugin that comes with it –
   for Home Assistant: address and token, then the areas you want in the bar
   ([details](plugins/HomeAssistant/README.md#first-setup)).
2. Move the mouse to the top edge of the screen: the pill appears; stay on it and the panel
   opens. Click the pin to keep it open.
3. The pen icon starts the edit mode, the gear icon (or a double-click on the tray icon) opens
   the settings.

## Plugins

Every source of tiles is a plugin: a folder with a `plugin.json` and its DLLs, loaded at
start-up, each in its own load context. A plugin that is broken or does not fit this version of
ue Bar is listed with the reason under **Settings → Plugins** – the app keeps running and its
tiles show a placeholder. Plugins that come with ue Bar are checked against a checksum list
written when ue Bar is built and load without asking. For every other plugin ue Bar asks once
(name, author, version, source and a clear warning) and again whenever its files change. Add one
as a ZIP with **Settings → Plugins → Add plugin …**.

| Plugin | What it does | Comes with ue Bar |
|---|---|---|
| [Home Assistant](plugins/HomeAssistant/README.md) | Lights, climate, media, vacuums, sensors and more from Home Assistant; notifications, Assist, layout sync | yes |
| [Test module](plugins/Dummy/README.md) | Simulated devices for developing and testing ue Bar | no – debug builds and a separate ZIP |
| [Hello](samples/HelloPlugin) | Sample plugin: a counter tile, the starting point for your own | no – sample in the source |

### Plugins by others

None yet. Want to write one? Start with [writing a plugin](docs/WRITING-A-PLUGIN.md) – plugins
live in their own repository and only need the SDK package.

## Building from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/UeBar.App
```

Installer, portable executable, SDK package and plugin ZIPs (needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

The results are written to `dist/`. For development, set the environment variable
`UEBAR_DATA_DIR` to a test folder so your real settings stay untouched. `ueBar.exe --settings`
(or `--settings=plugins`) opens the settings right after the start; `UEBAR_CULTURE=en-US` sets
the number and date format without changing the Windows region.

Repository layout: `src/` the app, bar, core and SDK · `plugins/<Name>/` the official plugins
(`src/`, `tests/`, README) · `samples/` the sample plugin · `docs/` architecture and plugin guide.

Further reading: [architecture](docs/ARCHITECTURE.md) · [writing a plugin](docs/WRITING-A-PLUGIN.md)
· [contributing](CONTRIBUTING.md) · [changelog](CHANGELOG.md)

## License

Copyright (C) 2026 Dominik Poschinger

ue Bar is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License](LICENSE), version 3 or (at your option) any later version. It is
distributed WITHOUT ANY WARRANTY. Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

ue Bar is **not** an official project of Home Assistant, Nabu Casa or Samsung. These names are
used only to describe what ue Bar works with and what its design is inspired by; no logos of
them are used.
