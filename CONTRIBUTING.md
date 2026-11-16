# Contributing

Thanks for your interest in ue Bar! Bug reports, ideas and pull requests are welcome.

## Reporting a bug or an idea

Use the issue templates. For bugs, please add the version (Settings → About), Windows version,
what you did and what happened. The log is in `%APPDATA%\ueBar\logs` – **check it for anything
private before attaching it** (it never contains tokens, but it may contain entity names).

## Development

- Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- `dotnet build` and `dotnet test` must pass with 0 warnings (warnings are errors).
- Set `UEBAR_DATA_DIR` to a test folder while developing, so your real settings stay untouched.
- The tests for Home Assistant run against a fake HA server with an example home; no real
  Home Assistant is needed.

## Rules for code

- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. Most importantly:
  - `UeBar.Core` knows nothing about Home Assistant and references no WPF.
  - Everything module-specific lives in the module. A new module = a new project + one line in
    `ModuleCatalog.cs`; if the core has to change for it, the interface is wrong – open an issue.
  - Modules never reference each other; they talk through the module bus.
  - Interfaces in `UeBar.Core` are only extended, never changed in a breaking way.
  - In modules: no `async void`, no own timers or threads – use `IModuleContext.RunBackground`.
- UI texts go into `Resources/Strings.resx` (English) **and** `Strings.de.resx` (German) of the
  project; tests fail if a key is missing in one language or a text is hard-wired.
- Code, comments, log messages and commit messages in English.
- Every source file starts with the license header:

  ```csharp
  // SPDX-License-Identifier: GPL-3.0-or-later
  // Copyright (C) 2026 <author>
  ```

- Never commit secrets (tokens, passwords, webhook IDs) or personal data (real entity IDs, names,
  addresses). Use the example home in the tests (`light.office_desk`, `homeassistant.local`,
  `192.168.1.10`).

## License

By contributing you agree that your contribution is licensed under the
[GNU General Public License v3.0 or later](LICENSE).
