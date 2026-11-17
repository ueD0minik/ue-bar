# Test module for ue Bar

**English** · [Deutsch](README.de.md) · [back to ue Bar](../../README.md)

A plugin for developing and testing ue Bar itself (plugin ID `dummy`): a simulated fan (switch),
desk lamp (dimmer), thermostat and music player whose values live only in memory, and a
**Trigger crash** tile that throws on purpose, to check the fault protection of ue Bar. It needs
no Home Assistant and no network, so it shows quickly how a change looks with each kind of tile.

- **Debug builds** of ue Bar bring it along; release builds do not.
- For a release build, add `ueBar-plugin-dummy-<version>.zip` from the release page under
  **Settings → Plugins → Add plugin …**.
- It marks itself as a developer module and suggests its own test tabs.

`src/` the plugin (references only the ue Bar SDK). It is also used by the core tests in
`tests/UeBar.Core.Tests`.
