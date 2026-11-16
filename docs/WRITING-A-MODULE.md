# Writing a module

A module brings content into ue Bar: tile types, and optionally search, status values,
notifications, settings and more. This guide builds a tiny module with one tile type: a counter
that counts up when you tap it. It needs no WPF code – tiles are described as data and the app
renders them (see [ARCHITECTURE.md](ARCHITECTURE.md)).

## 1. Create the project

```powershell
dotnet new classlib -n UeBar.Modules.Counter -o src/UeBar.Modules.Counter
dotnet sln add src/UeBar.Modules.Counter
dotnet add src/UeBar.Modules.Counter reference src/UeBar.Core
```

Target `net10.0` (no `-windows`): modules do not reference WPF. A module **never** references
another module; if modules need to work together they use the module bus.

## 2. The tile type

A tile type has an ID with the module's prefix, a name, an icon (a
[Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font)
name), allowed sizes and the slots it needs. `CreateTile` builds an observable `TileModel` from
elements and must be fast (no network calls).

```csharp
using UeBar.Core.Abstractions;
using UeBar.Core.Layout;
using UeBar.Core.Tiles;

namespace UeBar.Modules.Counter;

internal sealed class CounterTileType : ITileType
{
    public string Id => "counter.counter";

    public string DisplayName => "Counter";

    public string Icon => "Add";

    public IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public TileSize DefaultSize => TileSize.Small;

    // The counter needs no "thing" from the module, so no slots.
    public IReadOnlyList<TileSlot> Slots => [];

    public TileModel CreateTile(TileConfig config)
    {
        var count = 0;
        var value = new ValueDisplayElement { Id = "value", Value = "0" };
        var tile = new TileModel { Title = "Counter", Icon = Icon, StateText = "Tap to count" };
        tile.Elements.Add(new TapAction { Id = "tap", Label = "Count" });
        tile.Elements.Add(value);

        // Interactions arrive on a thread-pool thread, always through the core's fault protection.
        tile.Handler = new DelegateInteractionHandler((t, interaction) =>
        {
            if (interaction is { ElementId: "tap", Kind: InteractionKind.Tap })
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

Elements available: `TapAction`, `SliderElement`, `StepperElement`, `ButtonRowElement`,
`ChoiceElement`, `ValueDisplayElement`, `MediaInfoElement`. Changing a property (as `value.Value`
above) updates the tile live; the app moves the change to the UI thread.

## 3. The module

```csharp
using UeBar.Core.Abstractions;

namespace UeBar.Modules.Counter;

public sealed class CounterModule : IModule
{
    private readonly ITileType[] _tileTypes = [new CounterTileType()];

    public string Id => "counter";

    public string DisplayName => "Counter";

    public string Icon => "Add";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Log.Log(UeBar.Core.Logging.LogLevel.Info, "Counter module started");
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
`context.RunBackground(name, work)` with the stop token `context.Stopping` – no `async void`, no
own timers or threads. Exceptions in background work set the module to "Error" and trigger an
automatic restart instead of ending the app.

## 4. Register it

Add a project reference from `src/UeBar.App` to the module and one line in
`src/UeBar.App/ModuleCatalog.cs`:

```csharp
registry.Register(new CounterModule());
```

That is all the app needs: the module appears under **Settings → Modules** (it can be switched
off there), and in edit mode "Add tile" lists its tile type once you search for it – or add the
tile to the layout by hand:

```json
{ "id": "c1", "module": "counter", "type": "counter.counter", "size": "1x1" }
```

## 5. Optional capabilities

A module implements only what it needs. The core checks the capabilities with `is`/`as`:

| Interface | What it adds |
|---|---|
| `IEntitySource` | searchable things with a default action (quick search, "Add tile", tile suggestion) |
| `IStatusProvider` | small values for the status line |
| `IModuleSettings` | settings fields; the core builds a settings page and stores secrets encrypted |
| `IModuleSettingsTest` | "Test connection" for the entered settings |
| `IModuleDiagnostics` | diagnostic lines on the module's settings page |
| `INotificationSource` | notifications shown as a pill and in the history |
| `IAssistProvider` | answers text questions (the Assist field) |
| `IQuickCommandProvider` | quick commands with a prefix, e.g. `+ milk` |
| `INowPlayingSource` | what is playing right now (pill) |
| `ILayoutStoreProvider` | a place to sync the layout (e.g. a server) |
| `ILayoutSuggestionProvider` / `ILayoutSuggestionOptions` | a suggested layout for the first start |
| `IImageSource` | images (cover art) by image ID, credentials stay in the module |

On the module context, the core offers more: `context as IHostInfo` (device name),
`context as IModuleSecrets` (encrypted small secrets), `context as IUiLanguageInfo` (UI language
and its change event).

## 6. Texts in two languages

Modules bring their own texts. Put them in `Resources/Strings.resx` (English) and
`Resources/Strings.de.resx` (German) in the module project and read them with a
`TextCatalog`:

```csharp
using System.Globalization;
using UeBar.Core.Localization;

internal static class CounterText
{
    public static CultureInfo? Culture { get; set; } // set from IUiLanguageInfo on start and on change

    public static TextCatalog T { get; } =
        TextCatalog.For(typeof(CounterText).Assembly, "UeBar.Modules.Counter", () => Culture);
}
```

Format numbers and dates with `CultureInfo.CurrentCulture` (the Windows region). When the user
switches the language, `IUiLanguageInfo.UiCultureChanged` fires; the app recreates all tiles
afterwards, so labels set in `CreateTile` follow automatically.

## Rules

- Never block: the core calls modules with time limits; a hanging call shows up as an error.
- Modules may be called from any thread – keep them thread-safe.
- Never log secrets (tokens, passwords).
- Interfaces in `UeBar.Core` are only ever extended, never changed in a breaking way. A module
  written today keeps working with later versions of the app.
