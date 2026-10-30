# Ostfriesland Games – Online-Mehrspieler

Ein [BepInEx](https://github.com/BepInEx/BepInEx)-Mod plus kleiner Server, der den
lokalen Hot-Seat-Modus von *Ostfriesland Games* ins Netz bringt.

Alle Spieler eines Raums spielen **gleichzeitig dieselbe Disziplin auf ihrem eigenen
Rechner**. Der Server sorgt für einen gemeinsamen Start, zeigt live, wer noch spielt
(bei Padstockspringen, Bosseln und Krabbenpulen inkl. Live-Punkten), sammelt die
Ergebnisse ein und schickt sie an alle zurück. Das Ergebnis erscheint im
Original-Ergebnisbildschirm des Spiels.

- 2–4 Spieler pro Raum, beliebig viele Räume pro Server
- alle fünf Disziplinen: Padstockspringen, Schlickschlitten, Bosseln, Teezeremonie, Krabbenpulen
- der erste Spieler im Raum ist Host und wählt Disziplin bzw. Revanche
- wer abbricht oder die Verbindung verliert, bekommt 0 Punkte – die Runde läuft für die anderen weiter
- lokale Bestenliste: nur der eigene Score wird auf dem eigenen Rechner gespeichert

> **Keine Spieldateien im Repo.** Der Mod wird gegen deine eigene Spielinstallation
> kompiliert; BepInEx wird beim Installieren direkt von dessen GitHub-Releases geladen.

## Installation

Du brauchst eine Linux-Version des Spiels (Ordner mit `OG.x86_64` und `OG_Data/`).

### NixOS / Nix

```bash
nix run github:AWildLeon/Ostfriesland-Games-Multiplayer#install -- "/pfad/zu/Ostfriesland Games"
```

Das entpackt BepInEx 5.4.23.4 (gepinnt per Hash) in den Spielordner und baut das
Plugin nach `BepInEx/plugins/OGMultiplayer/`.

Spiel starten (FHS-Umgebung via `steam-run-free`):

```bash
nix run github:AWildLeon/Ostfriesland-Games-Multiplayer#play -- "/pfad/zu/Ostfriesland Games"
```

Voraussetzung: `hardware.graphics.enable = true;` in deiner NixOS-Konfiguration.
Über Steam gestartet: Startoption `./run_bepinex.sh %command%`.

### Andere Linux-Distributionen

Benötigt: .NET SDK 8, `unzip`, `curl`.

```bash
git clone https://github.com/AWildLeon/Ostfriesland-Games-Multiplayer
```

```bash
Ostfriesland-Games-Multiplayer/scripts/install.sh "/pfad/zu/Ostfriesland Games"
```

```bash
cd "/pfad/zu/Ostfriesland Games" && ./run_bepinex.sh
```

## Spielen

1. Spiel mit BepInEx starten, im Hauptmenü **F8** drücken.
2. Server, Port, ggf. Server-Passwort, Raumname, deine drei Initialen und deine Figur
   eintragen → **Verbinden**.
   Alle Mitspieler nutzen denselben Server und denselben Raumnamen.
3. Sobald mindestens zwei Spieler im Raum sind, wählt der Host im F8-Fenster eine Disziplin.
4. Alle Spiele laden die Disziplin; gestartet wird erst, wenn alle bereit sind.
5. Nach deinem Lauf wartest du auf die anderen, danach kommt der gemeinsame Ergebnisbildschirm.
   Dort startet der Host per Revanche-Taste (Leertaste) die nächste Runde oder wählt eine andere Disziplin.

Unten rechts siehst du während einer Runde alle Spieler mit Status und Punkten.

Einstellungen landen in `BepInEx/config/de.onlh.ostfriesland.multiplayer.cfg`
(Server, Passwort, Raum, Initialen, Figur, Taste für das Fenster, Ecke für die Spielerliste).

## Server

```bash
nix run github:AWildLeon/Ostfriesland-Games-Multiplayer#server -- --port 27420
```

oder ohne Nix:

```bash
dotnet run -c Release --project src/OGMultiplayer.Server -- --port 27420
```

Optionen: `--port` (Standard 27420), `--bind` (Standard 0.0.0.0),
`--ready-timeout <s>` (wie lange auf langsame Spieler gewartet wird, Standard 60),
`--match-timeout <min>` (maximale Rundendauer, Standard 20),
`--password-file <pfad>` (Server-Passwort, erste Zeile der Datei).
Port, Bind-Adresse und Passwort gehen auch per `OGMP_PORT` / `OGMP_BIND` /
`OGMP_PASSWORD` bzw. `OGMP_PASSWORD_FILE`.

### Passwort

Ohne Passwort ist der Server offen. Mit Passwort trägt jeder Spieler es im F8-Fenster ein.
Das Passwort wird **nie im Klartext übertragen**: Der Server schickt pro Verbindung eine
zufällige Nonce, der Client antwortet mit `HMAC-SHA256(Passwort, Nonce)`. Nach 5 falschen
Versuchen innerhalb von 5 Minuten wird die IP vorübergehend gesperrt.

Im Spiel steht das Passwort im Klartext in der BepInEx-Konfigurationsdatei. Wer eine
Verbindung mitschneidet, kann ein schwaches Passwort offline durchprobieren – also ein
langes, zufälliges Passwort nehmen.

### NixOS-Modul

```nix
{
  inputs.og-multiplayer.url = "github:AWildLeon/Ostfriesland-Games-Multiplayer";

  outputs = { nixpkgs, og-multiplayer, ... }: {
    nixosConfigurations.meinserver = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        og-multiplayer.nixosModules.default
        {
          services.ogmultiplayer = {
            enable = true;
            port = 27420;
            openFirewall = true;
            passwordFile = "/run/secrets/ogmultiplayer-password"; # optional, z. B. via agenix/sops-nix
          };
        }
      ];
    };
  };
}
```

Der Dienst läuft als `DynamicUser` mit systemd-Härtung; die Passwortdatei wird per
`LoadCredential` eingelesen und landet nicht im Nix-Store.

> Das Passwort schützt den Zugang, verschlüsselt aber nicht die Spieldaten (Initialen,
> Punkte). Wer mehr will: VPN (z. B. WireGuard, Tailscale).

## Wie es funktioniert

Das Spiel hat einen lokalen Hot-Seat-Modus (`HotSeatFlowController`): Spieler wechseln
sich ab, jede Disziplin meldet am Ende `TryCompleteTurn(score)`. Der Mod erzeugt für eine
Online-Runde einen solchen Controller mit allen Spielern des Raums – der lokale Spieler
immer auf Platz 1 – und patcht ihn mit Harmony:

| Spielmethode | Online-Verhalten |
| --- | --- |
| `TryAwaitTurnStart` | meldet dem Server `READY` |
| `StartTurn` | erst nach `GO` vom Server (alle bereit) |
| `TryCompleteTurn` | eigener Score → Server, kein Wechsel zum nächsten Spieler |
| `BeginMatch` / `Rematch` / `ChooseDiscipline` | nur der Host, geht als `START` an den Server |
| `SaveHighscores` | speichert nur den eigenen Score |
| `Abort` | meldet `LEAVE` (0 Punkte) |

Sobald `RESULT` ankommt, werden die Punkte der anderen eingetragen und der normale
Ergebnisbildschirm geöffnet.

Protokoll: eine Nachricht pro Zeile, Felder per Tab getrennt – siehe
[`src/Shared/Protocol.cs`](src/Shared/Protocol.cs).

## Entwicklung

```
src/Shared/Protocol.cs          gemeinsames Protokoll
src/OGMultiplayer.Server/       Server (.NET 8, keine Abhängigkeiten)
src/OGMultiplayer.Plugin/       BepInEx-5-Plugin (netstandard2.1)
scripts/install.sh              BepInEx + Plugin installieren
flake.nix                       Pakete, Apps, NixOS-Modul, DevShell
```

```bash
nix develop
```

```bash
dotnet build src/OGMultiplayer.Plugin -c Release -p:GameDir="/pfad/zu/Ostfriesland Games"
```

Getestet mit der Spielversion auf Unity 6000.4.0f1 (Mono) und BepInEx 5.4.23.4 unter Linux.
Ändert ein Spiel-Update die gepatchten Methoden, meldet das Plugin beim Start im
BepInEx-Log, was fehlt.

## Lizenz

MIT – siehe [LICENSE](LICENSE). *Ostfriesland Games* selbst gehört seinen Entwicklern;
dieses Projekt enthält keine Inhalte des Spiels.
