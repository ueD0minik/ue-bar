# Screenshot-Plugin für die ue Bar

[English](README.md) · **Deutsch** · [zurück zur ue Bar](../../README.de.md)

Einen Bereich, ein Fenster oder einen ganzen Bildschirm aufnehmen – und dann neben die Arbeit
anpinnen, kopieren, speichern oder den Text erkennen. Die Texterkennung ist in Windows eingebaut und
läuft offline; die Bilder bleiben auf diesem PC. Das Plugin kommt mit der ue Bar (Plugin-ID
`screenshot`), ist standardmäßig an und braucht keine Einrichtung.

![Bereich wählen: die kleine Leiste darunter](images/screenshot-pick.png)

## Aufnehmen

- **Tastenkürzel** `Win + Alt + S` (in den Einstellungen des Plugins änderbar).
- **Kachel** „Screenshot“ (im Bearbeiten-Modus hinzufügen, „Screenshot“ suchen). Als 2x1 mit Knöpfen
  für Bereich, Fenster und Bildschirm.
- **Schnellsuche / Assist:** `shot`, `shot fenster`, `shot bildschirm`, `shot text` (auch englisch
  `shot window`, `shot screen`). Modus und Aktion in beliebiger Reihenfolge.

Der Bildschirm friert ein und wird abgedunkelt:

| Modus | So geht's |
|---|---|
| **Bereich** (Standard) | Rechteck ziehen – auch über mehrere Monitore, mit jeder Skalierung. Ein Klick ohne Ziehen nimmt das Fenster unter der Maus. |
| **Fenster** | Fenster anklicken (das unter der Maus ist umrahmt). |
| **Bildschirm** | Der Monitor unter der Maus wird sofort genommen; Klick auf einen anderen wechselt. |

`Esc` oder Rechtsklick bricht ab.

## Nach der Aufnahme

Am Bereich erscheint eine kleine Leiste: **Anpinnen · Kopieren · Speichern ˅ · Text erkennen**
– Tasten, solange sie zu sehen ist: `Enter` = Anpinnen (Standard), `Strg + C` = Kopieren, `Strg + S` =
Speichern unter …, `Esc` = abbrechen. In den Einstellungen lässt sich eine davon als Standard wählen – dann entfällt
die Leiste.

- **Anpinnen:** Der Screenshot wird ein [Pin](../../README.de.md#pins) genau dort, wo er auf dem
  Bildschirm war – mit dem Mausrad zoomen, verschieben, obenauf lassen und davon abschreiben.
- **Kopieren:** in die Zwischenablage (als Bild und PNG).
- **Speichern:** nach `Bilder\Screenshots\ue Bar` (änderbar), Name `Screenshot 2026-11-20 14-03-05.png`;
  überschrieben wird nie etwas.
- **Speichern unter …** (der kleine Pfeil neben „Speichern“): der Windows-Dialog – beliebiger Ordner,
  beliebiger Name. Er startet im zuletzt gewählten Ordner (auf diesem PC gemerkt).
- **Text erkennen:** Windows liest den Text (OCR) – in die Zwischenablage, als Text-Pin oder beides
  (Einstellung). Die Meldung bietet das jeweils andere als Knopf an.

Die Rückmeldung erscheint für ein paar Sekunden dort, wo die Leiste war – „233 Zeichen erkannt und
kopiert · Anpinnen“, „Screenshot gespeichert“ – auch bei Windows-„Nicht stören“, weil du es gerade selbst
ausgelöst hast (**Einstellungen → Benachrichtigungen → Rückmeldungen**, Standard an).

## Wo liegt was?

- **Die Aufnahme** gibt es nur im Arbeitsspeicher. Kopieren und Text erkennen schreiben keine Datei –
  auch keine temporäre.
- **Speichern / Speichern unter …** sind die einzigen Wege, auf denen ein Screenshot auf die Platte
  kommt – in den Screenshot-Ordner oder wohin du willst. Die Dateien bleiben dort; die ue Bar löscht in
  diesem Ordner nie etwas.
- **Pin:** Das Bild liegt in `%APPDATA%\ueBar\pins`, solange der Pin offen ist, und wird beim Schließen
  des Pins gelöscht. Anpinnen schreibt nie in den Screenshot-Ordner.

![Ein Screenshot und sein erkannter Text als Pins](images/screenshot-pins.png)

## Absätze und Textformat

Windows erkennt Zeilen. Die ue Bar gewinnt die Absätze daraus zurück, wo die Zeilen auf dem Bildschirm
stehen:

- Ein deutlich größerer Abstand als der übliche Zeilenabstand, eine Überschrift in größerer Schrift oder
  eine andere Einrückung beginnt einen neuen Absatz – dazwischen steht eine Leerzeile.
- Zeilen mit Aufzählungszeichen oder Nummer (`•`, `-`, `*`, `1.`, `a)`) beginnen immer eine neue Zeile;
  eine Liste bleibt zusammen.
- Zwei Spalten nebeneinander werden nacheinander gelesen.

**Format des erkannten Texts** (Einstellung) – für Kopieren und Text-Pins gleich:

- **Fließtext** (Standard): Die Zeilen eines Absatzes werden zusammengefügt, ein Trennstrich am
  Zeilenende fällt weg („Unter-“ + „nehmen“ → „Unternehmen“, „E-“ + „Mail“ bleibt „E-Mail“). Zeilen, die
  wie eine Tabelle oder Vokabelliste aufgebaut sind („to achieve - erreichen“), und Zeilen, die bewusst
  früher enden, bleiben Zeilen.
- **Zeilen wie im Bild:** Jede erkannte Zeile bleibt eine Zeile.

**Was wann?** Fließtext für alles, was man als Sätze liest: eine Antwort im Chat oder Terminal, eine
Webseite, einen Artikel, Folien mit Absätzen – auch Aufzählungen (ein umbrochener Punkt bleibt ein Punkt).
Zeilen wie im Bild für Code, Adressen, Tabellen, Einstellungslisten und alles, wo jede Zeile für sich steht.

Grenzen: Tabellen mit vielen Spalten, Text, der um Bilder fließt, und eine Überschrift über zwei Spalten
werden nicht genau nachgebaut – dann „Zeilen wie im Bild“ wählen oder eine Spalte nach der anderen
aufnehmen. Bei einer Liste kurzer Zeilen ohne Trennzeichen (z. B. Dateiangaben in einem Programm) können im
Fließtext nach der längsten Zeile zwei Zeilen zusammenrutschen – ob dort umbrochen wurde, sieht man nicht.
Die Erkennung selbst ist die von Windows: Auf 11 Test-Bildschirmfotos lag sie bei etwa 2–3 falschen Wörtern
pro 100 (typisch: „Start“ → „Statt“, „I“ → „l“, „0“ → „e“ in Code-Schriften). Das Bild vorher aufzubereiten
(dunkles Design umkehren, kleine Schrift vergrößern) wurde gemessen und war nicht besser.

## Sprachen der Texterkennung

Windows erkennt die Sprachen, für die das Paket „Optische Zeichenerkennung“ installiert ist – meist
die Anzeigesprachen. **Automatisch** nimmt deine Windows-Sprachen; du kannst auch eine wählen. Ist
keine installiert, sagt die ue Bar das und wie man eine hinzufügt: **Windows-Einstellungen → Zeit und
Sprache → Sprache und Region → … neben der Sprache → Sprachoptionen → Optische Zeichenerkennung**.

## Einstellungen

| Einstellung | Standard |
|---|---|
| Tastenkürzel | `Win + Alt + S` (Entf = Standard, oder „Kein Tastenkürzel“) |
| Modus | Bereich |
| Nach der Aufnahme | Fragen (die kleine Leiste) |
| Ordner für gespeicherte Screenshots | `Bilder\Screenshots\ue Bar` |
| Erkannter Text | In die Zwischenablage kopieren |
| Format des erkannten Texts | Fließtext |
| Erkennungssprache | Automatisch |

## Datenschutz

Screenshots werden nie hochgeladen und nie an andere Plugins weitergegeben. Gespeicherte Dateien und
angepinnte Bilder liegen nur auf diesem PC (Pins unter `%APPDATA%\ueBar\pins`, nie im synchronisierten
Layout).

## Modul-Bus

Andere Plugins dürfen mit deiner Freigabe (**Einstellungen → Plugins → … → Darf nutzen**):

| Art | ID | Inhalt |
|---|---|---|
| Aktion | `screenshot.capture` | `{ "mode"?: "region" \| "window" \| "screen", "action"?: "pin" \| "copy" \| "save" \| "saveas" \| "ocr" }` → `{ "started": true }` – den Bereich wählt trotzdem der Nutzer |
| Ereignis | `screenshot.taken` | `{ "mode", "action", "width", "height", "monitors" }` – nur Metadaten, nie das Bild |

## Technik

Das Plugin referenziert nur das SDK. Überlagerung, eingefrorener Bildschirm und Leiste sind die
Kern-Funktion **„Bildschirmbereich auswählen“** (`IScreenAreaPicker`), Pins kommen vom Pin-Dienst des
Kerns – das Plugin hat keinen eigenen Oberflächen-Code. Die Texterkennung nutzt `Windows.Media.Ocr`
(kleine Bilder werden vorher vergrößert, das hilft bei Bildschirmtext).
