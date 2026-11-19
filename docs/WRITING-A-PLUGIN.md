# Writing a plugin

A plugin brings content into ue Bar: tile types, and optionally search, status values,
notifications, settings and more. It is a normal .NET class library that references **only the
ue Bar SDK** (`UeBar.Sdk`), plus a `plugin.json`. ue Bar loads it at start-up – no need to rebuild
the app. The Home Assistant integration itself is such a plugin.

Plugins need no WPF code: a tile describes its content as data and ue Bar renders it (see
[ARCHITECTURE.md](ARCHITECTURE.md)).

The complete sample is [`samples/HelloPlugin`](../samples/HelloPlugin): one tile that counts taps,
a settings page, a suggested tab and two languages. Copy it as a starting point.

**Where your plugin lives:** a plugin of your own goes into **its own repository** and references the
SDK package. Only the official plugins (the ones ue Bar ships or maintains) live in the ue Bar
repository, each in `plugins/<Name>/` with this layout:

```
plugins/<Name>/
  src/           the plugin project (UeBar.Modules.<Name>.csproj + plugin.json)
  tests/         its tests (if any)
  README.md      what it does, setup, settings, screenshots – English
  README.de.md   the same in German
  images/        screenshots for the README
```

An official plugin is added to the plugin table in the main README and, if it ships with the
installer, as a `<BundledPlugin>` in `UeBar.App.csproj` – nothing else in the app changes. Its ZIP
in a release is called `ueBar-plugin-<id>-<version>.zip`.

## 1. Create the project

```powershell
dotnet new classlib -n MyPlugin -f net10.0
cd MyPlugin
dotnet add package UeBar.Sdk --version 1.4.0
```

- Target `net10.0` (not `net10.0-windows`): plugins do not reference WPF. A plugin that needs
  Windows Runtime APIs may target a Windows SDK version instead, e.g.
  `net10.0-windows10.0.19041.0` (like the Windows plugin) – the WinRT projection then travels in
  the plugin folder.
- The SDK package is not on nuget.org (yet). Download `UeBar.Sdk.<version>.nupkg` from the
  [releases](https://github.com/ueD0minik/ue-bar/releases) and add its folder as a package source:
  `dotnet nuget add source C:\path\to\folder --name uebar-local`.
- The package contains only the contracts (a reference assembly) and a little build logic. At run
  time `UeBar.Sdk.dll` always comes from ue Bar, never from your plugin folder.
- A plugin never references another plugin or ue Bar's internal assemblies (`UeBar.Core`,
  `UeBar.Bar`, `ueBar`) – ue Bar refuses to load it. Plugins work together only through the
  module bus (`IModuleBus`).

Your own NuGet dependencies are fine: they are copied next to your DLL and loaded from your plugin
folder, separately from other plugins. Anything ue Bar itself already has (the .NET runtime) is
shared.

## 2. `plugin.json`

Next to the `.csproj`:

```json
{
  "id": "myplugin",
  "name": { "en": "My plugin", "de": "Mein Plugin" },
  "description": { "en": "What it does.", "de": "Was es tut." },
  "version": "1.0.0",
  "author": "Your Name",
  "license": "GPL-3.0-or-later",
  "homepage": "https://example.org/myplugin",
  "sdkVersion": "1.4",
  "entryAssembly": "MyPlugin.dll"
}
```

| Field | Meaning |
|---|---|
| `id` | unique, lower case: letters, digits, `.`, `-`, `_`. It is the module ID (`IModule.Id` must return the same), the folder name and the prefix of your tile type IDs. |
| `name`, `description` | a text, or one text per language (`en`, `de`) |
| `version` | your plugin's version |
| `author`, `license`, `homepage` | shown in Settings → Plugins and in the "Allow plugin?" dialog |
| `sdkVersion` | the plugin interface version you built against, `major.minor` (see [Versions](#versions)) |
| `entryAssembly` | the DLL with your module |
| `entryType` | optional: the full class name, if the DLL contains more than one `IModule` |

The build logic from the SDK package copies `plugin.json` to the output and stops the build if it
is missing or has no `id`/`version`.

## 3. The module

A public class that implements `IModule` and has a constructor without parameters. ue Bar creates
it once at start-up – keep the constructor cheap.

```csharp
using UeBar.Sdk;

namespace MyPlugin;

public sealed class MyModule : IModule
{
    private readonly ITileType[] _tileTypes = [new CounterTileType()];

    public string Id => "myplugin";                 // = "id" in plugin.json

    public string DisplayName => "My plugin";

    public string Icon => "Star";                   // a Segoe Fluent Icons name

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Log.Info("My plugin started");
        SetStatus(ModuleStatus.Connected);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
```

`StartAsync` should return quickly. Longer work (a connection loop, polling) goes through
`context.RunBackground(name, work)` with the stop token `context.Stopping`. The context also gives
you a logger (`context.Log`) and a data folder (`context.DataDirectory`,
`%APPDATA%\ueBar\modules\<id>`).

## 4. A tile type

A tile type has an ID with your module's prefix, a name, an icon, allowed sizes and the slots it
needs. `CreateTile` builds an observable `TileModel` from elements and must be fast (no network
calls).

```csharp
internal sealed class CounterTileType : ITileType
{
    public string Id => "myplugin.counter";

    public string DisplayName => "Counter";

    public string Icon => "Star";

    public IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public TileSize DefaultSize => TileSize.Small;

    public IReadOnlyList<TileSlot> Slots => [];      // needs no "thing" from the module

    public TileModel CreateTile(TileConfig config)
    {
        var count = 0;
        var value = new ValueDisplayElement { Id = "value", Value = "0" };
        var tile = new TileModel { Title = "Counter", Icon = Icon, StateText = "Tap to count" };
        tile.Elements.Add(new TapAction { Id = "tap" });
        tile.Elements.Add(value);

        // Interactions arrive on a thread-pool thread, always through ue Bar's fault protection.
        tile.Handler = new DelegateInteractionHandler((t, interaction) =>
        {
            if (interaction.Kind == InteractionKind.Tap)
            {
                count++;
                value.Value = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
                t.IsActive = count % 2 == 1;
            }
        });
        return tile;
    }
}
```

Elements: `TapAction`, `SliderElement`, `StepperElement`, `ButtonRowElement`, `ChoiceElement`,
`ValueDisplayElement`, `MediaInfoElement`, `IconGridElement` (icons with names, e.g. several
shortcuts in one tile; a tap reports `Press` with the entry's ID, 1.3). Changing a property (like
`value.Value` above) updates the tile live; ue Bar moves the change to the UI thread. A 1x1 tile
shows icon, title and state, 2x1 also the first visible element, 2x2 all of them.

`TileModel.ImageUrl` (an image ID your `IImageSource` answers, or an http(s) address) replaces the
icon of the tile with a real image, e.g. an app icon (1.3); the glyph stays until the image is there.

If your plugin changes a tile's elements on its own thread (adding/removing elements), make sure two
threads never change the same tile at once: a new tile is refreshed on the core's thread while your
worker may refresh it already – one lock around "track + first refresh" and around your refresh
round is enough.

## 5. Optional capabilities

Implement only what you need; ue Bar checks with `is`/`as`.

| Interface | What it adds |
|---|---|
| `IEntitySource` | searchable things with a default action (quick search, "Add tile", tile suggestion) |
| `IStatusProvider` | small values for the status line |
| `IModuleSettings` | settings fields; ue Bar builds a settings page and stores secrets encrypted ([fields](#settings-fields)) |
| `ILiveModuleSettings` | a change of the settings is applied while the plugin runs – no restart, no flicker (1.3) |
| `IModuleSettingsTest` | "Test connection" for the entered settings |
| `IModuleDiagnostics` | diagnostic lines on the plugin's settings page |
| `INotificationSource` | notifications shown as a pill and in the history |
| `IAssistProvider` / `IAssistConversationProvider` | answers text questions (the Assist field) |
| `IQuickCommandProvider` | quick commands with a prefix, e.g. `+ milk`; a word prefix like `t` needs a space after it, `ArgumentOptional` allows the prefix alone (1.2) |
| `INowPlayingSource` | what is playing right now (`NowPlayingPriority`: rank among several sources, 1.1) |
| `ILayoutAware` | the plugin's tiles in the current layout (to subscribe only what is shown) |
| `IVisibilityAware` | what of the plugin is on screen right now – pause polling while nothing is visible (1.1) |
| `IDeviceBatterySource` | battery levels of devices (headphones, mice …) for other plugins to show (1.1) |
| `IPillActivitySource` | something going on (e.g. a timer) the pill shows when it comes out (1.2) |
| `INotificationActionHandler` | buttons on your notifications (`UeNotification.Actions`, e.g. "+5 min"); notifications can also bring a sound (1.2) and be **important** (`Priority`, 1.3, see below) |
| `IDropHandler` | turn something dropped on the panel in edit mode (file, folder, shortcut, link) into a tile (1.3) |
| `ITileItemEditor` | the user edits the entries of a tile right in the bar's edit mode: add (search, file, folder, web page, drop on the tile), reorder, rename, remove (1.4) |
| `ISettingListPresenter` | how a `List` field is shown: compact rows, groups, rows that are only shown (1.4) |
| `IModuleSettingsActions` | buttons on your settings page (`SettingFieldKind.Button`, 1.2) |
| `ILayoutStoreProvider` | a place to sync the layout (e.g. a server) |
| `ILayoutSuggestionProvider` / `ILayoutSuggestionOptions` | a suggested layout for the first start |
| `ITileSlotAssistant` | slot suggestions in edit mode |
| `IImageSource` | images (cover art) by image ID – credentials stay in the plugin |
| `ILocationProvider` | at home / away (for tabs visible only at home or away) |
| `IDeveloperModule` | marks a developer/test plugin |

### Settings fields

`SettingFieldKind`: `Text`, `Password` (stored encrypted), `Number`, `Toggle`, `Choice`,
`MultiChoice`, `Button` (1.2) and from 1.3:

- `File` / `Folder` – a path with "Browse…" (the core's dialog; `FileFilter` like
  `"Sounds|*.wav;*.mp3"`). Plugins never show windows of their own.
- `List` – entries with the fields of `ItemFields` (no secrets, no nested lists). The value is a JSON
  array; read it with `values.GetList(key)`. Every entry gets a stable `"id"` from the core.
- `ShowWhen = "kind=file"` (or `"kind=app|command"`) on any field: shown only while the other field
  of the same level – the page or the same list entry – has one of these values.

With `ILiveModuleSettings` your `ApplySettingsAsync` is also called while the plugin runs; it must
then be safe against your own work (a lock around the values is usually all it takes).

### Entries of a tile, edited in the bar (1.4)

With `ITileItemEditor` a tile (like the quick access collection) gets an entries section in edit mode.
ue Bar gives you a copy of the tile's `TileConfig`; return the changed copy, or `null` for "not
possible". ue Bar puts it into the layout like any edit, so undo, "Cancel" and the layout sync cover
it. Keep the entries in the configuration: slots in `Entities` and own values in `Options` (a
string dictionary that travels with the layout, 1.4). `GetTileItems` says which entries there are, how
many are allowed and where new ones may come from (`TileItemSources`).

### Important notifications

`UeNotification.Priority = NotificationPriority.Important` marks what must not be missed (a timer
that ran out). The user decides in Settings → Notifications what important notifications may do:
come out while the bar is paused, during "Do not disturb" and over fullscreen apps (all on by
default). Everything else follows the bar. The flags `IgnoreDoNotDisturb`/`ShowInFullscreen` of 1.2
still work, but prefer the priority.

### Drag and drop

In edit mode the user can drop files, folders, shortcuts (`.lnk`, `.url`) and links on the panel.
ue Bar asks the running plugins with `IDropHandler` one after another
(`CreateTileForDropAsync(DroppedItem, ct)`); the first that returns a `TileConfig` gets the tile,
`null` means "not mine". Only describe the tile – ue Bar adds it to the layout.

On the module context ue Bar offers more: `context as IHostInfo` (device name),
`context as IModuleSecrets` (small encrypted secrets), `context as IUiLanguageInfo` (UI language
and its change event), `context as IDeviceBatteryCatalog` (device batteries reported by other
plugins, 1.1).

## 6. Texts in two languages

Put your texts in `Resources/Strings.resx` (English) and `Resources/Strings.de.resx` (German) and
read them with the SDK's `TextCatalog`:

```csharp
using System.Globalization;
using UeBar.Sdk;

internal static class MyText
{
    public static CultureInfo? Culture { get; set; } // set from IUiLanguageInfo on start and on change

    public static TextCatalog T { get; } =
        TextCatalog.For(typeof(MyText).Assembly, "MyPlugin", () => Culture);
}
```

The German texts end up in `de\MyPlugin.resources.dll`, which belongs into the plugin folder (the
packing step below takes care of it). Format numbers and dates with `CultureInfo.CurrentCulture`
(the Windows region). When the user switches the language, `UiCultureChanged` fires; ue Bar then
recreates all tiles.

## 7. Build and install

```powershell
dotnet build -c Release -t:PackPlugin
```

This builds and writes

- `bin\Release\plugin\<id>\` – the plugin folder exactly as ue Bar loads it, and
- `bin\Release\plugin\<id>-<version>.zip` – the same as a ZIP.

In ue Bar: **Settings → Plugins → Add plugin …**, pick the ZIP (or "Add from folder …"). ue Bar
checks it (plugin.json, the DLL, the interface version), shows who made it with a clear warning
and copies it to `%APPDATA%\ueBar\plugins\<id>\` after you allow it. It is loaded on the next
start – the page offers "Restart now".

While developing you can also copy the plugin folder to `%APPDATA%\ueBar\plugins\` yourself (use
`UEBAR_DATA_DIR` to keep your real settings out of it). Every time the plugin's files change, ue
Bar asks again before loading it.

## Trust

A plugin runs inside ue Bar with all your rights: it can read files, use the network and use the
Home Assistant connection's data it is given. That is why ue Bar asks before loading any plugin
that does not come with it, and asks again when its files change. The decision is stored with a
checksum of the plugin's code files (`.dll`, `.exe`, `*.deps.json`, `plugin.json`) in
`%APPDATA%\ueBar\plugin-trust.json`.

## Versions

`sdkVersion` and the SDK package version follow the **plugin interface version**
(`CoreApiVersion`, `major.minor`):

- The **minor** version grows when the interface is extended (new optional capability). A plugin
  built for `1.0` loads in every ue Bar that offers `1.x`.
- The **major** version changes only for an unavoidable breaking change. Plugins for another major
  version are not loaded – Settings → Plugins says why.

A plugin that needs `1.5` is not loaded by a ue Bar that offers `1.4` (it says "update ue Bar").

## Rules

These keep one plugin from taking down the others. They cannot all be enforced technically:

- **No `async void`.** An exception there ends the whole app.
- **No own timers or threads.** Background work only through `IModuleContext.RunBackground`, waits
  through the stop token. Exceptions there are attributed to your plugin: it goes to "Error" and
  restarts automatically (at most 3 times in 5 minutes).
- **Never block.** ue Bar calls plugins with time limits; a hanging call counts as an error.
- **Stop quickly.** When ue Bar exits, every plugin gets at most 2 s for `StopAsync`; after that
  the exit goes on without it. Cancel your work through the stop token instead of waiting for it.
- **Thread-safe.** ue Bar calls you from the thread pool; it moves UI updates itself.
- **Never log secrets** (tokens, passwords).
- **No WPF.** If a tile really needs its own look, talk to us first (open an issue).

## License

ue Bar is licensed under the GPL-3.0-or-later and plugins run inside it, linked against its SDK.
Plugins you distribute must therefore be under a **GPL-compatible license** (for example
GPL-3.0-or-later, MIT, Apache-2.0, BSD).
