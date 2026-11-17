# Test-Modul für ue Bar

[English](README.md) · **Deutsch** · [zurück zu ue Bar](../../README.de.md)

Ein Plugin zum Entwickeln und Testen von ue Bar selbst (Plugin-ID `dummy`): ein simulierter
Lüfter (Schalter), eine Schreibtischlampe (Dimmer), ein Thermostat und ein Musikplayer, deren Werte
nur im Speicher liegen, dazu eine Kachel **Absturz auslösen**, die absichtlich eine Ausnahme wirft,
um den Fehlerschutz von ue Bar zu prüfen. Es braucht weder Home Assistant noch Netzwerk und zeigt
deshalb schnell, wie eine Änderung mit jeder Kachelart aussieht.

- **Debug-Builds** von ue Bar bringen es mit, Release-Builds nicht.
- Für einen Release-Build `ueBar-plugin-dummy-<version>.zip` von der Release-Seite unter
  **Einstellungen → Plugins → Plugin hinzufügen …** hinzufügen.
- Es kennzeichnet sich als Entwickler-Modul und schlägt eigene Test-Tabs vor.

`src/` das Plugin (referenziert nur das ue-Bar-SDK). Die Kern-Tests in `tests/UeBar.Core.Tests`
verwenden es ebenfalls.
