# Third-party notices

ue Bar is licensed under the GNU General Public License, version 3 or later
(GPL-3.0-or-later, see [LICENSE](LICENSE)). It uses the following third-party software. All
licenses below are compatible with GPL-3.0: the MIT license and the Apache License 2.0 are
permissive licenses that may be combined with GPL-3.0 code.

## Shipped with the app

| Component | Version | License | Copyright |
|---|---|---|---|
| .NET 10 runtime and WPF (Windows Desktop runtime) | 10.0 | MIT | © .NET Foundation and contributors |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | © .NET Foundation and contributors |
| [Microsoft.Diagnostics.NETCore.Client](https://github.com/dotnet/diagnostics) | 0.2.750501 | MIT | © Microsoft Corporation |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 8.0.2 | MIT | © Microsoft Corporation |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 8.0.3 | MIT | © Microsoft Corporation |

### Windows plugin (`plugins\windows\`)

| Component | Version | License | Copyright |
|---|---|---|---|
| Windows SDK projection for .NET (`Microsoft.Windows.SDK.NET.dll`, from the `net10.0-windows10.0.19041.0` target framework) | 10.0.19041.57 | MIT | © Microsoft Corporation |
| [C#/WinRT runtime](https://github.com/microsoft/CsWinRT) (`WinRT.Runtime.dll`) | 2.2 | MIT | © Microsoft Corporation |
| [System.Management](https://github.com/dotnet/runtime) (with System.CodeDom) | 10.0.0 | MIT | © .NET Foundation and contributors |

The plugin uses Windows' own APIs (Core Audio, media sessions, radios, Bluetooth, power, WMI);
nothing of Windows itself is shipped.

The installer is framework-dependent: it downloads the .NET 10 Desktop Runtime from Microsoft
if it is missing. The portable single executable contains the .NET runtime (self-contained).

The two `Microsoft.Extensions.*` packages come in as dependencies of
`Microsoft.Diagnostics.NETCore.Client` (used to write a small dump when the UI thread hangs).

## Build and test tools (not shipped)

| Tool | Version | License | Copyright |
|---|---|---|---|
| .NET SDK | 10.0 | MIT | © .NET Foundation and contributors |
| [Inno Setup](https://jrsoftware.org/isinfo.php) (builds the installer) | 6 | Inno Setup License (permissive, allows commercial use and redistribution) | © Jordan Russell, portions © Martijn Laan |
| [xUnit](https://github.com/xunit/xunit) (xunit, xunit.runner.visualstudio) | 2.9.3 / 3.1.4 | Apache-2.0 | © .NET Foundation and contributors |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT | © Microsoft Corporation |
| [Microsoft.Extensions.TimeProvider.Testing](https://github.com/dotnet/extensions) | 10.11.0 | MIT | © Microsoft Corporation |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 6.0.4 | MIT | © tonerdo and the coverlet contributors |

The installer itself is created with Inno Setup; its license requires that its copyright
notices are kept, which the Inno Setup runtime inside the installer does.

## Fonts, icons and names

- Icons are drawn with **Segoe Fluent Icons**, a font that ships with Windows. The font is
  **not** included in this repository or in the app.
- The app icon (`src/UeBar.App/Assets/ueBar.ico`) is created by `build/make-icon.ps1` and is
  part of this project (GPL-3.0-or-later).
- "Home Assistant" and "Nabu Casa" are names of their respective owners. "Samsung" and "One UI"
  are trademarks of Samsung Electronics. They are used only to describe compatibility and the
  design inspiration. ue Bar is **not** an official project of Home Assistant, Nabu Casa or
  Samsung, and it contains no logos of them.
