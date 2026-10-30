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
- keine Verbindungen zur Online-Bestenliste des Spiels, solange der Online-Modus verbunden ist;
  Online-Runden landen nie in der weltweiten Bestenliste

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
(Server, Passwort, Raum, Initialen, Figur, Taste für das Fenster, Ecke für die Spielerliste,
`Privacy.BlockScoreApiWhileOnline`).

### Online-Bestenliste des Spiels

Das Spiel schickt Scores an eine eigene Score-API (`OnlineScoreSync`). Solange der Mod mit
einem Server verbunden ist oder eine Online-Runde offen ist, blockiert der Mod **jede**
Anfrage dorthin (Abruf der Bestenliste, Sitzung, Upload). Für das Spiel sieht das wie ein
Netzwerkfehler aus; nach dem Trennen läuft alles wieder normal. Scores aus Online-Runden
werden nur lokal gespeichert und nie zum Hochladen vorgemerkt.
Abschalten: `BlockScoreApiWhileOnline = false` (Online-Runden bleiben trotzdem lokal).

## Server

```bash
nix run github:AWildLeon/Ostfriesland-Games-Multiplayer#server -- --port 27420
```

oder ohne Nix:

```bash
dotnet run -c Release --project src/OGMultiplayer.Server -- --port 27420
```

Fertige Server-Binaries (Linux x64/arm64, Windows) gibt es unter
[Releases](https://github.com/AWildLeon/Ostfriesland-Games-Multiplayer/releases).

Optionen: `--port` (Standard 27420), `--bind` (Standard 0.0.0.0),
`--ready-timeout <s>` (wie lange auf langsame Spieler gewartet wird, Standard 60),
`--match-timeout <min>` (maximale Rundendauer, Standard 20),
`--password-file <pfad>` (Server-Passwort, erste Zeile der Datei),
`--state-file <pfad>` (Bans und Admin-Passwort, Standard `./ogmultiplayer-state.json`),
`--no-state` (nichts speichern), `--no-console` (keine Admin-Befehle von stdin).
Port, Bind-Adresse und Passwort gehen auch per `OGMP_PORT` / `OGMP_BIND` /
`OGMP_PASSWORD` bzw. `OGMP_PASSWORD_FILE`.

### Admin-Befehle

Der Server liest Befehle von seiner Standardeingabe – einfach ins Terminal tippen, in dem er läuft
(für Dauerbetrieb z. B. in `tmux`/`screen`):

| Befehl | Wirkung |
| --- | --- |
| `help` | alle Befehle |
| `status` | Uptime, Verbindungen, Räume, Passwort, Bans |
| `players` | alle Verbindungen mit ID, Raum, Status, IP |
| `rooms` | Räume mit Host und laufender Runde |
| `say <text>` / `sayroom <raum> <text>` | Nachricht, erscheint im Spiel |
| `kick <id> [grund]` | Spieler trennen |
| `ban <id\|ip> [grund]` / `unban <ip>` / `bans` | IP dauerhaft sperren / entsperren / auflisten |
| `endmatch <raum>` | laufende Runde beenden, Unfertige bekommen 0 |
| `closeroom <raum> [grund]` | alle Spieler eines Raums trennen |
| `password <neu\|off\|reset>` | Passwort dauerhaft ändern/abschalten; `reset` = wieder das aus `--password-file` |
| `shutdown [sekunden] [grund]` / `shutdown cancel` | Server beenden, optional mit Countdown im Spiel |

Bans und Passwort-Änderungen landen in der State-Datei (JSON, nur für den Besitzer lesbar,
atomar geschrieben) und überleben Neustarts. Ist die Datei kaputt, startet der Server nicht,
statt still alle Bans zu vergessen. Achtung: Ein per `password` gesetztes Passwort steht
im Klartext darin.

Getrennte/gekickte Spieler sehen den Grund im F8-Fenster. Strg+C und `SIGTERM`
(z. B. `systemctl stop`) beenden ebenfalls sauber und verabschieden alle Spieler.

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

Der Dienst läuft als `DynamicUser` mit systemd-Härtung; Bans liegen in
`/var/lib/ogmultiplayer/state.json`. Die Passwortdatei wird per
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

## Automatische Wartung (GitHub Actions)

| Workflow | Wann | Was |
| --- | --- | --- |
| `CI` | Push, PR | Server-Tests (xUnit), `nix flake check`, Nix-Builds, shellcheck, actionlint |
| `CI` → Dependabot-Automerge | Dependabot-PR | Patch/Minor-Updates nach grünem CI automatisch mergen |
| `Wartung` | montags + manuell | `nix flake update` und neueste BepInEx-5-Version (Version + Hashes) einspielen, alles prüfen, PR erstellen und mergen |
| `Release` | Tag `v*` | Tests, Server-Binaries bauen, GitHub-Release mit Changelog |
| Dependabot | wöchentlich | GitHub-Actions- und Test-Abhängigkeiten |

Einmalig in den Repo-Einstellungen nötig: *Settings → Actions → General → Workflow permissions*:
„Read and write permissions“ und „Allow GitHub Actions to create and approve pull requests“.
Wer `main` per Branch-Protection mit Pflicht-Checks schützt, muss die Wartungs-PRs von Hand
mergen (PRs mit dem Actions-Token lösen keine weiteren Workflows aus).

Neues Release: Version in `src/OGMultiplayer.Plugin/Plugin.cs` und `flake.nix` anheben, dann

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Nicht automatisierbar: ein Spiel-Update, das gepatchte Methoden ändert. Das Plugin meldet das
beim Start im BepInEx-Log; dann `GameBridge.cs`/`Patches.cs` anpassen.

## Entwicklung

```
src/Shared/Protocol.cs          gemeinsames Protokoll
src/OGMultiplayer.Server/       Server (.NET 8, keine Abhängigkeiten)
src/OGMultiplayer.Plugin/       BepInEx-5-Plugin (netstandard2.1)
tests/OGMultiplayer.Server.Tests/  Server-Tests (Auth, Rundenablauf, Admin-Befehle)
scripts/install.sh              BepInEx + Plugin installieren
scripts/update-bepinex.sh       BepInEx-Pin auf neueste 5.x-Version heben
flake.nix                       Pakete, Apps, NixOS-Modul, DevShell
```

```bash
nix develop
```

```bash
dotnet build src/OGMultiplayer.Plugin -c Release -p:GameDir="/pfad/zu/Ostfriesland Games"
```

```bash
dotnet test tests/OGMultiplayer.Server.Tests
```

Getestet mit der Spielversion auf Unity 6000.4.0f1 (Mono) und BepInEx 5.4.23.4 unter Linux.
Ändert ein Spiel-Update die gepatchten Methoden, meldet das Plugin beim Start im
BepInEx-Log, was fehlt.

## Lizenz

MIT – siehe [LICENSE](LICENSE). *Ostfriesland Games* selbst gehört seinen Entwicklern;
dieses Projekt enthält keine Inhalte des Spiels.
