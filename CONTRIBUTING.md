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
  `UEBAR_CULTURE=en-US` sets the number/date format, `UEBAR_LOG_LEVEL=Debug` logs more.
- The tests for Home Assistant run against a fake HA server with an example home; no real
  Home Assistant is needed.

## Rules for code

- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. Most importantly:
  - `UeBar.Sdk` (the contract) and `UeBar.Core` (the implementation) know nothing about Home
    Assistant and reference no WPF.
  - Every module is a plugin: a new project that references **only** `UeBar.Sdk`, with a
    `plugin.json` (see [docs/WRITING-A-PLUGIN.md](docs/WRITING-A-PLUGIN.md)). The app and the core
    do not change for it; if they have to, the interface is wrong – open an issue.
  - Official plugins live in `plugins/<Name>/` (`src/`, `tests/`, `README.md` + `README.de.md`,
    `images/`) and get a row in the plugin table of the main README. Plugins by others belong in
    their own repositories.
  - Plugins never reference each other; they talk through the module bus.
  - Interfaces in `UeBar.Sdk` are only extended, never changed in a breaking way; each extension
    raises the minor version of `CoreApiVersion` and of the SDK package.
  - In plugins: no `async void`, no own timers or threads – use `IModuleContext.RunBackground`.
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
