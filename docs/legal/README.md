# Vorlagen für einen öffentlichen Server

`OGMultiplayer.Server --public` startet nur mit Impressum und Datenschutzhinweisen.
Die Dateien hier sind **Vorlagen**: Platzhalter in `[eckigen Klammern]` ersetzen und
an den eigenen Betrieb anpassen.

> **Keine Rechtsberatung.** Die Texte beschreiben nur, was die Server-Software technisch
> verarbeitet. Ob sie für deinen Betrieb ausreichen (z. B. Hoster, Logs des Betriebssystems,
> Reverse Proxy, Backups), musst du selbst prüfen oder prüfen lassen.

| Datei | Option |
| --- | --- |
| `impressum.example.txt` | `--imprint-file` |
| `datenschutz.example.txt` | `--privacy-file` |
| `regeln.example.txt` | `--rules-file` (optional) |

Die Texte sind reiner Text (UTF-8), Zeilenumbrüche bleiben erhalten, maximal ca. 60 000 Zeichen.
Ändert sich die Datenschutz-Datei, müssen Spieler beim nächsten Verbinden neu zustimmen
(`legal reload` in der Admin-Konsole lädt die Texte ohne Neustart neu).
