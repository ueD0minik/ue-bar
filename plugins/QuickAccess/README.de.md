# Schnellzugriff-Plugin für ue Bar

[English](README.md) · **Deutsch** · [zurück zu ue Bar](../../README.de.md)

Apps aus dem Startmenü, Dateien, Office-Vorlagen, Ordner, Webseiten und eigene Befehle als
Kacheln – einzeln oder bis zu sechs in einer Sammel-Kachel (z. B. „Schule“ mit den Ordnern deiner
Fächer). Mit den echten Symbolen aus Windows, in der Schnellsuche und per Ziehen und Ablegen. Das
Plugin kommt mit der ue Bar (Plugin-ID `quickaccess`), ist standardmäßig an und braucht keine
Einrichtung.

![Ein Schnellzugriff-Tab](images/quickaccess-tab.png)

## Kacheln anlegen

- **Bearbeiten-Modus → Kachel hinzufügen:** nach einer App aus dem Startmenü suchen (klassische und
  Store-Apps), nach einem deiner Einträge (unten) oder nach „Sammlung“.
- **Ziehen und Ablegen:** Im Bearbeiten-Modus eine Datei, einen Ordner, eine Verknüpfung (`.lnk`),
  eine Internetverknüpfung (`.url`) oder einen Link aus der Adresszeile des Browsers aufs Panel
  ziehen – daraus wird eine Kachel im aktuellen Tab. Eine Verknüpfung auf einen Ordner wird der
  Ordner, jede andere bleibt die Verknüpfung (mit ihren Argumenten).
- **Sammlung:** „Sammlung“ (2x2) hinzufügen und bis zu sechs Einträge in ihre Felder wählen – Apps,
  Dateien, Ordner, Webseiten. Im Bearbeiten-Modus einen Namen geben (z. B. „Schule“).

## Was ein Tippen macht

| Art | Öffnet |
|---|---|
| App | die App, wahlweise als Administrator (Windows fragt wie gewohnt nach) |
| Datei | mit der Standard-App; **Office-Vorlagen** (`.dotx`, `.xltx`, `.potx` …) öffnen ein **neues Dokument** aus der Vorlage (kennt keine App „Neu“, die Vorlage selbst) |
| Ordner | im Explorer |
| Webseite | im Standardbrowser (nur `https://`, `http://`, `mailto:`) |
| Befehl | ein eigenes Skript (`.ps1`, `.bat`) oder eine Befehlszeile – **für Fortgeschrittene** |

Fehlt ein Ziel (Datei verschoben, USB-Stick nicht da, App deinstalliert), zeigt die Kachel **„Nicht
gefunden“** und bleibt – die ue Bar schaut bei jedem Öffnen des Panels erneut nach.

## Einträge (Einstellungen)

**Einstellungen → Plugins → Schnellzugriff → Einträge:** deine eigenen Verknüpfungen auf diesem PC
mit Name, Art und Ziel (Datei und Ordner mit „Durchsuchen …“). Sie erscheinen in der Suche – auch
zum Anlegen von Kacheln – und andere Plugins können sie über ihre ID öffnen (Modul-Bus, unten).

![Einstellungen des Schnellzugriff-Plugins](images/quickaccess-settings.png)

**Befehle** laufen mit deinen Rechten, genau so, wie du sie einträgst (PowerShell-Skripte in
PowerShell, die Ausführungsrichtlinie deines PCs gilt). Trag nur ein, was du kennst. Eine
Befehls-Kachel im Layout verweist nur auf den Eintrag dieses PCs: Ein Layout, das von woanders
synchronisiert wird, kann nie einen Befehl mitbringen. Auf einem PC ohne diesen Eintrag zeigt die
Kachel „Nicht gefunden“.

## Mehrere PCs (Layout-Sync)

Pfade unter deinem Benutzerordner werden als `%USERPROFILE%\…` gespeichert – ein synchronisiertes
Layout öffnet `%USERPROFILE%\Documents\Schule` auf jedem PC, egal wie der Benutzerordner dort heißt.

## Schnellsuche

Win+Alt+Leertaste, einen Teil des Namens tippen, **Enter** – die App (Datei, Ordner …) startet und
bekommt den Fokus, nicht die Bar.

## Modul-Bus

| Art | ID | Inhalt |
|---|---|---|
| Aktion | `quickaccess.open` | `{ "id": "<Eintrags-ID>" }` – öffnet einen Eintrag der Einstellungen |

Wie alles über den Bus nur mit Freigabe (Einstellungen → Plugins → das andere Plugin → „Darf nutzen“).

## Ressourcen

Eingeklappt läuft nichts. Die Startmenü-Apps werden einmal gelesen, wenn sie gebraucht werden
(Suche, eine Kachel mit einer App), und beim Suchen höchstens alle 10 Minuten neu; Symbole werden
gelesen, wenn eine Kachel sichtbar wird, und behalten (höchstens 128).

## Für Entwickler

Das Plugin spricht mit Windows nur über `Platform/IQuickShell` (Startmenü-Apps über den
„Apps“-Ordner der Shell, Starten über `ShellExecuteEx`, Verknüpfungen über `IShellLink`, Symbole über
`IShellItemImageFactory` als PNG). Die Tests (`plugins/QuickAccess/tests`) laufen mit einer
Fake-Shell: jede Art von Ziel, Vorlagen, `%USERPROFILE%`-Pfade hin und zurück, „Nicht gefunden“,
Ablegen von Dateien/Ordnern/`.lnk`/`.url`/Links, die Sammel-Kachel, die Suche und der Bus.
