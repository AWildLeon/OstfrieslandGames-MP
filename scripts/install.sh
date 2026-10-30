#!/usr/bin/env bash
# Installs BepInEx 5 and the OGMultiplayer plugin into a local copy of Ostfriesland Games.
# Nothing from the game is copied anywhere; the plugin is compiled against your own install.
#
# Usage: scripts/install.sh "/path/to/Ostfriesland Games"
#
# Env:
#   BEPINEX_ZIP   use this BepInEx_linux_x64 zip instead of downloading it
#   DOTNET        dotnet executable (default: dotnet)
set -euo pipefail

BEPINEX_VERSION="5.4.23.4"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_linux_x64_${BEPINEX_VERSION}.zip"
BEPINEX_SHA256="2722c37c69251e1fe8d25707c8ecf41d9b8c92d8311c6832aa4777c9db9ac0f6"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 \"/path/to/Ostfriesland Games\"" >&2
  exit 2
fi
GAME_DIR="$(cd "$1" && pwd)"

if [[ ! -f "$GAME_DIR/OG.x86_64" || ! -f "$GAME_DIR/OG_Data/Managed/Assembly-CSharp.dll" ]]; then
  echo "Fehler: '$GAME_DIR' sieht nicht wie der Spielordner aus (OG.x86_64 / OG_Data fehlt)." >&2
  exit 1
fi

# 1) BepInEx
if [[ ! -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  zip="${BEPINEX_ZIP:-}"
  if [[ -z "$zip" ]]; then
    echo "==> Lade BepInEx ${BEPINEX_VERSION} ..."
    zip="$tmp/bepinex.zip"
    curl -fL --retry 3 -o "$zip" "$BEPINEX_URL"
    echo "${BEPINEX_SHA256}  $zip" | sha256sum -c -
  fi
  echo "==> Entpacke BepInEx nach $GAME_DIR"
  unzip -oq "$zip" -d "$GAME_DIR"
else
  echo "==> BepInEx ist bereits installiert"
fi

# run_bepinex.sh: point it at the game binary so it can be started without arguments
if [[ -f "$GAME_DIR/run_bepinex.sh" ]]; then
  sed -i 's/^executable_name=""/executable_name="OG.x86_64"/' "$GAME_DIR/run_bepinex.sh"
  chmod +x "$GAME_DIR/run_bepinex.sh"
fi
chmod +x "$GAME_DIR/OG.x86_64"

# 2) Plugin
echo "==> Baue Plugin"
"$DOTNET" build "$REPO_DIR/src/OGMultiplayer.Plugin/OGMultiplayer.Plugin.csproj" \
  -c Release -nologo -v quiet \
  -p:GameDir="$GAME_DIR" \
  -o "$REPO_DIR/build/plugin"

mkdir -p "$GAME_DIR/BepInEx/plugins/OGMultiplayer"
cp "$REPO_DIR/build/plugin/OGMultiplayer.dll" "$GAME_DIR/BepInEx/plugins/OGMultiplayer/"

echo
echo "Fertig. Spiel mit BepInEx starten:"
echo "  cd \"$GAME_DIR\" && ./run_bepinex.sh"
echo "NixOS:  steam-run ./run_bepinex.sh   (oder: nix run .#play -- \"$GAME_DIR\")"
echo "Steam-Startoption:  ./run_bepinex.sh %command%"
echo "Im Spiel: F8 oeffnet das Online-Fenster."
