# UeBar.Sdk

Contracts for writing plugins for [ue Bar](https://github.com/ueD0minik/ue-bar), the slide-out
dashboard at the top edge of the Windows screen.

A plugin is a class library that references **only this package**, implements `IModule` and ships
with a `plugin.json`. It describes its tiles as data (`TileModel` with building blocks such as
`TapAction`, `SliderElement`, `ValueDisplayElement`); ue Bar renders them. No WPF needed.

```xml
<PackageReference Include="UeBar.Sdk" Version="1.0.0" />
```

```
dotnet build -c Release -t:PackPlugin   →   bin/Release/plugin/<id>-<version>.zip
```

Add the ZIP in ue Bar under *Settings → Plugins → Add plugin …*.

- Guide: [docs/WRITING-A-PLUGIN.md](https://github.com/ueD0minik/ue-bar/blob/main/docs/WRITING-A-PLUGIN.md)
- Sample: [samples/HelloPlugin](https://github.com/ueD0minik/ue-bar/tree/main/samples/HelloPlugin)

The package version is the version of the plugin interface (`CoreApiVersion`): `1.x` plugins load in
every ue Bar that offers interface `1.y` with `y ≥ x`.

License: GPL-3.0-or-later. Plugins link against ue Bar and must be licensed compatibly with the GPL.
