# Writing a plugin

A plugin brings content into ue Bar: tile types, and optionally search, status values,
notifications, settings and more. It is a normal .NET class library that references **only the
ue Bar SDK** (`UeBar.Sdk`), plus a `plugin.json`. ue Bar loads it at start-up – no need to rebuild
the app. The Home Assistant integration itself is such a plugin.

Plugins need no WPF code: a tile describes its content as data and ue Bar renders it (see
[ARCHITECTURE.md](ARCHITECTURE.md)).

The complete sample is [`samples/HelloPlugin`](../samples/HelloPlugin): one tile that counts taps,
a settings page, a suggested tab and two languages. Copy it as a starting point.

## 1. Create the project

```powershell
dotnet new classlib -n MyPlugin -f net10.0
cd MyPlugin
dotnet add package UeBar.Sdk --version 1.0.0
```

- Target `net10.0` (not `net10.0-windows`): plugins do not reference WPF.
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
  "sdkVersion": "1.0",
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
`ValueDisplayElement`, `MediaInfoElement`. Changing a property (like `value.Value` above) updates
the tile live; ue Bar moves the change to the UI thread. A 1x1 tile shows icon, title and state,
2x1 also the first visible element, 2x2 all of them.

## 5. Optional capabilities

Implement only what you need; ue Bar checks with `is`/`as`.

| Interface | What it adds |
|---|---|
| `IEntitySource` | searchable things with a default action (quick search, "Add tile", tile suggestion) |
| `IStatusProvider` | small values for the status line |
| `IModuleSettings` | settings fields; ue Bar builds a settings page and stores secrets encrypted |
| `IModuleSettingsTest` | "Test connection" for the entered settings |
| `IModuleDiagnostics` | diagnostic lines on the plugin's settings page |
| `INotificationSource` | notifications shown as a pill and in the history |
| `IAssistProvider` / `IAssistConversationProvider` | answers text questions (the Assist field) |
| `IQuickCommandProvider` | quick commands with a prefix, e.g. `+ milk` |
| `INowPlayingSource` | what is playing right now |
| `ILayoutAware` | the plugin's tiles in the current layout (to subscribe only what is shown) |
| `ILayoutStoreProvider` | a place to sync the layout (e.g. a server) |
| `ILayoutSuggestionProvider` / `ILayoutSuggestionOptions` | a suggested layout for the first start |
| `ITileSlotAssistant` | slot suggestions in edit mode |
| `IImageSource` | images (cover art) by image ID – credentials stay in the plugin |
| `ILocationProvider` | at home / away (for tabs visible only at home or away) |
| `IDeveloperModule` | marks a developer/test plugin |

On the module context ue Bar offers more: `context as IHostInfo` (device name),
`context as IModuleSecrets` (small encrypted secrets), `context as IUiLanguageInfo` (UI language
and its change event).

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

A plugin that needs `1.3` is not loaded by a ue Bar that offers `1.2` (it says "update ue Bar").

## Rules

These keep one plugin from taking down the others. They cannot all be enforced technically:

- **No `async void`.** An exception there ends the whole app.
- **No own timers or threads.** Background work only through `IModuleContext.RunBackground`, waits
  through the stop token. Exceptions there are attributed to your plugin: it goes to "Error" and
  restarts automatically (at most 3 times in 5 minutes).
- **Never block.** ue Bar calls plugins with time limits; a hanging call counts as an error.
- **Thread-safe.** ue Bar calls you from the thread pool; it moves UI updates itself.
- **Never log secrets** (tokens, passwords).
- **No WPF.** If a tile really needs its own look, talk to us first (open an issue).

## License

ue Bar is licensed under the GPL-3.0-or-later and plugins run inside it, linked against its SDK.
Plugins you distribute must therefore be under a **GPL-compatible license** (for example
GPL-3.0-or-later, MIT, Apache-2.0, BSD).
