# ue Bar

[English](README.md) · **Deutsch**

Ein ausfahrbares Dashboard am oberen Bildschirmrand von Windows. Maus an den oberen Rand: Eine
kleine Pill erscheint und mit ihr deine Kacheln, eine Statuszeile, Benachrichtigungen und eine
Schnellsuche. Vorbild ist die „Now Bar“ von Samsung One UI. Was die Kacheln zeigen, kommt aus
**Plugins** – [Home Assistant](plugins/HomeAssistant/README.de.md), [Windows](plugins/Windows/README.de.md)
(Lautstärke, Medien, WLAN, Bluetooth, Akkus, Systemwerte dieses PCs), [Timer](plugins/Timer/README.de.md)
(Timer, Stoppuhr, Pomodoro), [Schnellzugriff](plugins/QuickAccess/README.de.md) (Apps, Dateien,
Ordner, Webseiten als Kacheln), [Screenshot](plugins/Screenshot/README.de.md) (aufnehmen, anpinnen,
Text erkennen) und [Zwischenablage](plugins/Clipboard/README.de.md) (Verlauf mit `Win + V`) werden mitgeliefert; weitere kommen dazu, ohne die App neu zu bauen. Notizen und
Screenshots lassen sich als kleine Fenster, die immer obenauf bleiben, auf dem Bildschirm **anpinnen**.

![Panel mit Kacheln eines Beispiel-Zuhauses](docs/images/panel.png)

| Pill | Bearbeiten | Einstellungen → Plugins |
|---|---|---|
| ![Pill](docs/images/pill.png) | ![Bearbeiten-Modus](docs/images/edit.png) | ![Einstellungen, Seite Plugins](docs/images/settings.png) |

## Was die Bar kann

- **Pill und Panel am oberen Rand**: erscheint nach kurzem Verweilen, bleibt offen, solange die
  Maus darauf ist, oder lässt sich fixieren. Mehrere Monitore, bei Vollbild-Apps ausgeblendet.
- **Kacheln in Tabs**, live aktualisiert. **Bearbeiten-Modus** direkt im Panel: Kacheln per Suche
  hinzufügen, Größe ändern, verschieben (auch in andere Tabs), Tabs pro Gerät oder für alle, Tabs
  nur daheim oder nur unterwegs.
- **Statuszeile** mit kleinen Werten im Panel und in der Pill, **Now Playing** in der Pill.
- **Benachrichtigungen** im Panel, mit Verlauf.
- **Schnellsuche** mit globalem Tastenkürzel (Standard `Win + Alt + Leertaste`).
- **Pins**: Notizen und Screenshots als kleine Fenster über allem (siehe unten).
- **Deutsch und Englisch**, im laufenden Betrieb umschaltbar. Zahlen und Uhrzeiten nach der
  Windows-Region.
- OLED-Schutz (Abdunkeln, Pixel-Shift), eingeklappt sparsam mit Ressourcen.
- **Vor Bildschirmaufnahmen verbergen** bei Bildschirmfreigabe und Aufnahmen (siehe unten).
- **Inhalts-Aktionen**: Plugins können Aktionen für einen Text, einen Screenshot oder einen Eintrag der
  Zwischenablage anbieten („Zusammenfassen“, „Fragen …“) – nach einem Screenshot, mit `Strg + K` im
  `Win + V`-Verlauf, im Menü eines Pins und für den markierten Text jedes Fensters (`Win + Alt + C`, siehe unten).

## Vor Bildschirmaufnahmen verbergen

Wenn du deinen Bildschirm teilst (Teams, Discord, Unterricht) oder aufnimmst (OBS, Snipping Tool),
kann die ue Bar draußen bleiben: Alle Fenster der App – die Bar mit der Pill, Pins, die
`Win + V`-Auswahl, Schnellsuche, Einstellungen und Willkommens-Assistent, eigene Benachrichtigungen
und Rückmeldungen, Kontextmenüs, Tooltips und Popups – fehlen auf der Aufnahme; die anderen sehen,
was dahinter liegt. Du selbst siehst und bedienst alles wie gewohnt.

- **Standard: an** (seit 1.7.0; eine bestehende Installation wird beim Update einmal eingeschaltet, danach gilt
  deine Wahl). Umschalten unter **Einstellungen → Allgemein → Datenschutz**, mit einem Klick im
  **Tray-Menü** (**Vor Bildschirmaufnahmen verbergen**, mit Häkchen), mit dem Schnellbefehl
  **`privat`** (`privat an` / `privat aus`) – oder aus Automationen über die Bus-Aktion
  `privacy.capture_hidden` (`{ "state": "on" | "off" | "toggle" }`, mit Freigabe wie jede Aktion; z. B.
  einschalten, wenn OBS startet). Wirkt sofort auf alle offenen Fenster, ohne Neustart.
- Solange es an ist, zeigt ein kleines durchgestrichenes Auge im Kopf des offenen Panels und der
  Tray-Tooltip den Zustand. Eingeklappt wird nichts zusätzlich angezeigt.
- **Was es nicht tut:** Es ist kein Schutz vor Schadsoftware, die unter deinem eigenen Konto läuft –
  so ein Programm kann weiterhin lesen, was die ue Bar zeigt. Es hält die Fenster nur aus
  Bildschirmfreigaben und Aufnahmen heraus.
- **Folgen:** Solange es an ist, kannst auch du die ue Bar nicht fotografieren – für Bilder der Bar
  vorher ausschalten. Remotedesktop und Remotehilfe sehen die Bar ebenfalls nicht. Meldungen und
  Rückfragen der ue Bar sind eigene Fenster und werden ebenfalls verborgen. Sichtbar bleibt, was Windows gehört:
  Datei-Dialoge (**Speichern unter**, **Öffnen**), SmartScreen, der Installer und die seltenen Meldungen, bevor die
  Einstellungen geladen sind (eine andere Instanz hängt, der Start schlug fehl). Der eigene Screenshot (`Win + Alt + S`)
  funktioniert weiter – auf seinem Bild fehlt einfach die ue Bar.
- Braucht Windows 10 Version 2004 oder neuer; ältere Versionen zeigen die Fenster auf Aufnahmen nur
  als schwarze Fläche (das steht dann in den Einstellungen).

## Inhalts-Aktionen und „Markierten Text an …“

Plugins können Aktionen für Inhalte anbieten – zum Beispiel das
[Claude-Plugin](https://github.com/ueD0minik/ue-bar-plugins/tree/main/claude) („Claude fragen …“, „Zusammenfassen“,
„Übersetzen“, „Korrigieren“). Sie erscheinen

- in der Leiste nach einem Screenshot (**Aktionen …**, `Strg + K`),
- im `Win + V`-Verlauf: Eintrag wählen, `Strg + K`,
- im Menü eines Pins (**Aktionen**),
- für den **markierten Text jedes Fensters**: `Win + Alt + C` (Einstellungen → Allgemein → Inhalts-Aktionen) kopiert die
  Markierung, gibt der Zwischenablage sofort ihren alten Inhalt zurück und zeigt die Aktionen an der Textmarke.

Die Antwort erscheint in einem Fenster der ue Bar: kopieren, anpinnen, **einfügen** in das Fenster, aus dem der Text
kam (z. B. den korrigierten Text direkt zurück in Word), nachfragen. Ein Plugin bekommt Inhalte nur, wenn du eine
seiner Aktionen startest; das Gespräch ist weg, sobald du das Fenster schließt.

## Pins

Ein Pin ist ein kleines Fenster, das über allem bleibt – eine Notiz oder ein Screenshot zum
Abschreiben, während du in einem anderen Fenster arbeitest.

- **Anlegen:** `pin <Text>` in der Schnellsuche oder im Assist, **Notiz anpinnen** in der Pin-Liste der
  Bar (Pin-Symbol im Panel), ein Screenshot ([Screenshot-Plugin](plugins/Screenshot/README.de.md),
  `Win + Alt + S`) – und jedes Plugin über das SDK.
- **Bedienen:** ziehen = verschieben (rastet an den Bildschirmrändern ein). **Größe ändern** an jedem
  Rand und jeder Ecke (der Mauszeiger zeigt, wo); die Größe bleibt, auch nach einem Neustart. Bilder:
  Mausrad = Zoom, gezoomtes Bild ziehen = verschieben, Doppelklick = einpassen. Text: bricht in der
  Breite des Pins um, das Mausrad scrollt längeren Text, **`Strg` + Mausrad** = Schriftgröße (bleibt
  ebenfalls), Doppelklick = bearbeiten.
- **Rechtsklick:** Kopieren · Speichern unter (Bild) · Deckkraft (Regler ziehen) · Position sperren ·
  **Klicks durchlassen** (der Pin bleibt sichtbar, du arbeitest „durch ihn hindurch“) · Ausblenden ·
  Schließen.
- **Durchlässigen Pin wieder bedienen:** `Strg` halten, solange die Maus über dem Pin ist – ein dünner
  Rahmen zeigt, dass er bedienbar ist, der Rechtsklick funktioniert; `Strg` loslassen, und Klicks gehen
  wieder durch. Außerdem: Pin-Liste in der Bar oder **Alle Pins wieder anklickbar machen** im Tray-Menü.
- **Alle auf einmal:** `Win + Alt + P` blendet alle Pins aus oder ein (änderbar unter
  **Einstellungen → Pins**); beim Einblenden werden durchlässige Pins wieder anklickbar. Die
  Pin-Liste in der Bar zeigt, versteckt und schließt einzelne oder alle.
- **OLED-freundlich:** Nach 60 s ohne Maus wird ein Pin auf 35 % abgedunkelt und ist sofort wieder
  hell, wenn die Maus kommt; alle Pins wandern alle paar Minuten um ein, zwei Pixel; über Vollbild-Apps
  sind sie ausgeblendet. Alles unter **Einstellungen → Pins** einstellbar.
- Pins überstehen einen Neustart (Inhalt, Größe, Platz – pro Monitor; fehlt der Monitor, kommt der
  Pin auf den Hauptbildschirm). Sie liegen nur auf diesem PC (`%APPDATA%\ueBar\pins`), nie im
  synchronisierten Layout.

**Wo liegt was?** Ein offener Pin liegt in `%APPDATA%\ueBar\pins` (`pins.json` und je Bild-Pin eine
Bilddatei). **Schließen löscht den Pin samt Bild**; Dateien ohne Eintrag räumt ue Bar beim Start weg.
**Einstellungen → Pins** zeigt, wie viele Pins gespeichert sind und wie viel Platz sie brauchen, und
öffnet den Ordner. Ein Pin ist nie ein gespeicherter Screenshot – wer ein Bild behalten will, nimmt
**Speichern unter …** im Menü des Pins (oder „Speichern“ im [Screenshot-Plugin](plugins/Screenshot/README.de.md)).

![Ein Screenshot und sein erkannter Text als Pins](plugins/Screenshot/images/screenshot-pins.png)

## Voraussetzungen

- Windows 10 (ab Version 1809) oder Windows 11, x64
- .NET 10 Desktop Runtime – der Installer lädt sie bei Bedarf von Microsoft; die portable
  Version braucht sie nicht
- Was ein Plugin braucht, steht in seiner README (Home Assistant: eine vom PC erreichbare Instanz
  und ein langlebiges Zugriffstoken; Windows: nichts, ab Windows 10 2004)

## Installation

Herunterladen auf der Seite [Releases](../../releases):

- `ueBar-Setup-<version>.exe` – Installer (Deutsch/Englisch, ohne Administratorrechte, nach
  `%LOCALAPPDATA%\Programs\ueBar`)
- `ueBar-<version>-portable.exe` – eine einzelne Datei, nichts zu installieren
- für Plugin-Autoren: `UeBar.Sdk.<version>.nupkg` (das SDK) und `ueBar-plugin-<id>-<version>.zip`
  (Plugins, die nicht mitgeliefert werden, z. B. das Beispiel-Plugin)

**Windows SmartScreen:** Die Dateien sind nicht signiert, deshalb zeigt Windows eventuell „Der
Computer wurde durch Windows geschützt“. Klicke auf **Weitere Informationen → Trotzdem
ausführen**. Im Zweifel selbst aus dem Quellcode bauen (siehe unten).

Einstellungen, Layout und Logs liegen in `%APPDATA%\ueBar`.

## Erste Schritte

1. ue Bar starten. Beim ersten Start richtet ein Willkommens-Assistent das mitgelieferte Plugin
   ein – bei Home Assistant: Adresse und Token, dann die Bereiche, die in die Bar sollen
   ([Details](plugins/HomeAssistant/README.de.md#erste-einrichtung)).
2. Maus an den oberen Bildschirmrand: Die Pill erscheint; bleibst du darauf, öffnet sich das
   Panel. Mit der Nadel bleibt es offen.
3. Der Stift startet den Bearbeiten-Modus, das Zahnrad (oder ein Doppelklick auf das
   Tray-Symbol) öffnet die Einstellungen.

## Plugins

Jede Quelle von Kacheln ist ein Plugin: ein Ordner mit `plugin.json` und seinen DLLs, der beim
Start geladen wird, jedes in einem eigenen Ladekontext. Ein kaputtes oder nicht passendes Plugin
steht mit Grund unter **Einstellungen → Plugins** – die App läuft weiter, seine Kacheln zeigen
einen Platzhalter. Mitgelieferte Plugins werden gegen eine beim Bau erzeugte Prüfsummenliste
geprüft und laden ohne Nachfrage. Bei jedem anderen fragt ue Bar einmal (Name, Autor, Version,
Quelle und eine deutliche Warnung) und wieder, wenn sich seine Dateien ändern. Hinzufügen als ZIP
über **Einstellungen → Plugins → Plugin hinzufügen …**. Die **portable** exe lädt zusätzlich Plugins aus einem
Ordner `plugins\` neben sich – Plugin-ZIP dorthin entpacken, exe starten: nichts wird installiert, keine
Admin-Rechte, ein schreibgeschützter Stick geht (bestätigt wird das Plugin trotzdem einmal pro PC und Benutzer).

| Plugin | Was es macht | Mitgeliefert |
|---|---|---|
| [Home Assistant](plugins/HomeAssistant/README.de.md) | Lichter, Klima, Medien, Staubsauger, Sensoren u. v. m. aus Home Assistant; Benachrichtigungen, Assist, Layout-Sync | ja |
| [Windows](plugins/Windows/README.de.md) | Dieser PC: Lautstärke und Ausgabegerät, App-Lautstärke, Medien aller Apps (auch als Now Playing), WLAN, Bluetooth, Geräte-Akkus, Akku, CPU, Arbeitsspeicher, Datenträger, Helligkeit, Sperren/Energiesparen | ja |
| [Timer](plugins/Timer/README.de.md) | Timer mit Voreinstellungen und Namen, Stoppuhr, Pomodoro (kann Aktionen anderer Plugins auslösen); `t 10` in der Schnellsuche, Restzeit in der Pill | ja |
| [Schnellzugriff](plugins/QuickAccess/README.de.md) | Startmenü-Apps, Dateien, Office-Vorlagen (neues Dokument), Ordner, Webseiten und eigene Befehle als Kacheln mit echten Symbolen – auch bis zu sechs in einer Kachel; Ziehen und Ablegen, Schnellsuche | ja |
| [Screenshot](plugins/Screenshot/README.de.md) | Bereich, Fenster oder Bildschirm aufnehmen (`Win + Alt + S`), dann anpinnen, kopieren, speichern oder Text erkennen (Windows-OCR, offline) | ja |
| [Zwischenablage](plugins/Clipboard/README.de.md) | Zwischenablage-Verlauf mit `Win + V`: Text, Bilder, Dateien; suchen, ins vorherige Fenster einfügen, merken, anpinnen – verschlüsselt nur auf diesem PC, Passwortmanager ausgenommen | ja |
| [Test-Modul](plugins/Dummy/README.de.md) | Simulierte Geräte zum Entwickeln und Testen von ue Bar | nein – Debug-Builds und eigenes ZIP |
| [Hallo](samples/HelloPlugin) | Beispiel-Plugin: eine Zähler-Kachel, Ausgangspunkt für eigene | nein – Beispiel im Quellcode |

### Weitere Plugins

Plugins, die nicht jeder braucht, liegen in **[ue-bar-plugins](https://github.com/ueD0minik/ue-bar-plugins)** – jedes
mit eigener Version und einer ZIP in den Releases, nicht im Installer:

| Plugin | Was es kann |
|---|---|
| [Claude](https://github.com/ueD0minik/ue-bar-plugins/tree/main/claude) | Claude in der Bar: Chat, `c <Frage>`, Inhalts-Aktionen (zum Screenshot fragen, zusammenfassen, übersetzen, korrigieren) – mit deinem Claude-Abo über Claude Code oder einem API-Schlüssel; nichts wird gespeichert |

### Plugins von anderen

Noch keine. Selbst eins schreiben? Los geht’s mit [Plugin schreiben](docs/WRITING-A-PLUGIN.md)
(Englisch) – Plugins liegen in einem eigenen Repository und brauchen nur das SDK-Paket.

## Aus dem Quellcode bauen

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) unter Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/UeBar.App
```

Tests mit knapper Ausgabe (Zusammenfassung je Projekt, nur fehlgeschlagene Tests): `build/test.ps1` lässt die schnellen
laufen (ohne die Kategorien `Slow` und `Desktop`), `-Full` alle, `-Desktop` nur die mit echten Fenstern und der echten
Zwischenablage (sie zeigen kurz Fenster; Testtexte landen nie im Windows-Zwischenablage-Verlauf). Das CI lässt alle laufen.

Installer, portable Exe, SDK-Paket und Plugin-ZIPs (braucht
[Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

Die Ergebnisse landen in `dist/`. Zum Entwickeln die Umgebungsvariable `UEBAR_DATA_DIR` auf einen
Testordner setzen, dann bleiben die echten Einstellungen unberührt. `ueBar.exe --settings` (oder
`--settings=plugins`) öffnet die Einstellungen gleich nach dem Start; `UEBAR_CULTURE=en-US` setzt
das Zahlen- und Datumsformat, ohne die Windows-Region zu ändern. Mit eigenem `UEBAR_DATA_DIR`
läuft die ue Bar als eigene Instanz neben der normalen; `UEBAR_WINDOWS_DEMO=1` gibt dem
Windows-Plugin Demo-Daten (für Screenshots).

Aufbau des Repositorys: `src/` App, Bar, Kern und SDK · `plugins/<Name>/` die offiziellen Plugins
(`src/`, `tests/`, README) · `samples/` das Beispiel-Plugin · `docs/` Architektur und
Plugin-Anleitung.

Weiterlesen (Englisch): [Architektur](docs/ARCHITECTURE.md) · [Plugin schreiben](docs/WRITING-A-PLUGIN.md)
· [Mitmachen](CONTRIBUTING.md) · [Änderungen](CHANGELOG.md)

## Lizenz

Copyright (C) 2026 Dominik Poschinger

ue Bar ist freie Software: Du darfst sie unter den Bedingungen der
[GNU General Public License](LICENSE), Version 3 oder (nach deiner Wahl) jeder späteren Version,
weitergeben und ändern. Sie wird OHNE JEDE GEWÄHRLEISTUNG bereitgestellt. Fremdkomponenten:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

ue Bar ist **kein** offizielles Projekt von Home Assistant, Nabu Casa oder Samsung. Diese Namen
beschreiben nur, womit ue Bar zusammenarbeitet und woran sich das Design orientiert; es werden
keine Logos davon verwendet.
