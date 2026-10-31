# Sicherheit

Ostfriesland Games Multiplayer ist ein **inoffizieller** Fan-Mod. Sicherheitslücken im Mod,
im Server oder in den Installationsskripten bitte hier melden – **nicht** beim Support des
Spiels. Lücken im Spiel selbst gehören nicht in dieses Repository.

## Unterstützte Versionen

| Version | Unterstützt |
| --- | --- |
| `main` und das jeweils neueste Release | ✅ |
| ältere Releases | ❌ – bitte zuerst aktualisieren |

Server und Plugin sprechen das Protokoll v1; Fixes landen immer auf `main` und im nächsten Release.

## Eine Lücke melden

Bitte **nicht** als öffentliches Issue. Stattdessen:

1. Auf GitHub im Reiter **Security** → **Report a vulnerability** (private Meldung an die
   Maintainer), oder
2. falls das nicht geht: ein Issue nur mit der Bitte um einen privaten Kontakt, ohne Details.

Hilfreich sind: betroffene Komponente (Server, Plugin, Installationsskript, NixOS-Modul),
Version/Commit, Schritte zum Nachstellen und die Auswirkung (z. B. Absturz, Zugriff ohne
Passwort, Datenabfluss).

Ablauf: Eingangsbestätigung in der Regel innerhalb von 7 Tagen, danach gemeinsame Einschätzung
und ein Fix auf `main` mit Release. Wer meldet, wird – wenn gewünscht – in den Release-Notes
genannt. Bitte gib uns Zeit für einen Fix, bevor du Details veröffentlichst (Richtwert 90 Tage).

## Im Rahmen

- **Server** (`src/OGMultiplayer.Server`): Authentifizierung, Raum-Passwörter, Bans,
  State-Datei, Admin-Konsole/-Socket, Limits (Zeilenlänge, Nachrichten pro Sekunde,
  Verbindungen pro IP, Raumanzahl), Absturz oder Hänger durch präparierte Nachrichten.
- **Plugin** (`src/OGMultiplayer.Plugin`): alles, was ein bösartiger Server oder Mitspieler
  beim Client auslösen kann (z. B. über `DOCTEXT`, `MSG`, `LOBBY`, `VIEW`).
- **Installationsskripte** (`scripts/`) und das **NixOS-Modul** (`flake.nix`).

## Nicht im Rahmen

- Das Spiel Ostfriesland Games selbst und seine Online-Bestenliste.
- Schummeln bei Punkten: Punkte meldet jeder Client selbst; der Server prüft sie nicht.
  Das ist bewusst so (Spaß-Runden unter Bekannten), keine Sicherheitslücke.
- Angriffe, die Zugriff auf den Rechner des Opfers oder den Server-Host voraussetzen.

## Sicherheitsmodell – was du wissen solltest

- **Keine Transportverschlüsselung.** Das Protokoll ist Klartext-TCP. Initialen, Figuren,
  Punkte, Raumnamen und der Szenen-Spiegel beim Zuschauen sind für jeden auf dem Netzweg
  lesbar. Für vertrauliche Runden den Server nur über VPN/WireGuard o. Ä. erreichbar machen.
- **Passwörter gehen nie im Klartext über das Netz.** Server und Räume nutzen einen
  Challenge-Response: Nachweis = HMAC-SHA256(PBKDF2-SHA256(Passwort, 30 000 Runden), Nonce).
  Jede Verbindung bekommt eine neue Nonce, mitgeschnittene Nachweise taugen nicht für eine
  zweite Anmeldung. Der Server speichert nur den abgeleiteten Schlüssel, der Betreiber sieht
  Passwörter nie im Klartext. Wer den abgeleiteten Schlüssel (State-Datei) hat, kann sich
  allerdings anmelden – die State-Datei ist daher nur für den Server-Nutzer lesbar (0600).
- **Fehlversuche** sperren die Adresse vorübergehend; Bans laufen ab (Standard 30 Tage).
- **Public-Modus:** Rechtstexte und Zustimmung vor dem Beitritt, Limits pro IP und für Räume,
  gekürzte IPs im Log.
- **Zuschauen** überträgt nur den Zustand der Spielszene (Positionen, Sprite-Namen, Texte der
  Spielszene), kein Bildschirm- oder Kamerabild; Spieler und Server-Betreiber können es
  abschalten.
- Das Plugin wird bei der Installation lokal gegen das Spiel gebaut; es werden keine fremden
  Binärdateien des Spiels verteilt. BepInEx wird mit fest hinterlegter SHA-256-Prüfsumme
  heruntergeladen.
