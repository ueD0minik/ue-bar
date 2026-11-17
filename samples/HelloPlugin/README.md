# Hello plugin

Sample plugin for ue Bar: one tile that counts how often it was tapped (with a reset button), a
settings page (step, name), a suggested tab and texts in English and German. Read
[docs/WRITING-A-PLUGIN.md](../../docs/WRITING-A-PLUGIN.md) for the details.

```powershell
dotnet build -c Release -t:PackPlugin
```

→ `bin/Release/plugin/hello-1.0.0.zip`. In ue Bar: **Settings → Plugins → Add plugin …**.

Outside this repository the project uses the `UeBar.Sdk` NuGet package; inside it, it uses the
SDK project directly (see `HelloPlugin.csproj`).
