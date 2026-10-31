#!/usr/bin/env bash
# In-game tests: two headless game instances (host + guest) play a round against a local
# test server, driven by the test mod in tests/OGMultiplayer.GameTests.
#
# Usage: scripts/game-test.sh "/path/to/Ostfriesland Games" [scenario ...]
#   scenario: online (default), pause, or online:<discipline> / pause:<discipline>
#   discipline: 0 Padstock, 1 Schlick, 2 Bosseln (default), 3 Teezeremonie, 4 Krabbenpulen,
#               all = every discipline
#
# The game folder needs BepInEx (scripts/install.sh). It is left untouched: both instances
# run from a temporary folder (game files hard-linked where possible, own BepInEx folder),
# so the test mod never ends up in your real install.
#
# Needs: dotnet (8), xvfb-run. Env: DOTNET, KEEP=1 keeps the temporary folder, SHOTS=dir
# saves screenshots of every step there.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 \"/path/to/Ostfriesland Games\" [online|pause][:discipline|:all] ..." >&2
  exit 2
fi
GAME_DIR="$(cd "$1" && pwd)"
shift
SCENARIOS=("${@:-online}")

if [[ ! -f "$GAME_DIR/OG.x86_64" || ! -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ]]; then
  echo "Fehler: '$GAME_DIR' ist kein Spielordner mit BepInEx (zuerst scripts/install.sh ausführen)." >&2
  exit 1
fi
for tool in xvfb-run "$DOTNET"; do
  command -v "$tool" >/dev/null || { echo "Fehler: $tool fehlt." >&2; exit 1; }
done

echo "==> Baue Plugin, Test-Mod und Server ..."
"$DOTNET" build "$REPO_DIR/tests/OGMultiplayer.GameTests" -c Release -p:GameDir="$GAME_DIR" -v quiet -nologo
"$DOTNET" build "$REPO_DIR/src/OGMultiplayer.Server" -c Release -v quiet -nologo
PLUGIN_DLL="$REPO_DIR/src/OGMultiplayer.Plugin/bin/Release/netstandard2.1/OGMultiplayer.dll"
TESTS_DLL="$REPO_DIR/tests/OGMultiplayer.GameTests/bin/Release/netstandard2.1/OGMultiplayer.GameTests.dll"
SERVER_DLL="$REPO_DIR/src/OGMultiplayer.Server/bin/Release/net8.0/OGMultiplayer.Server.dll"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/ogmp-gametest.XXXXXX")"
cleanup() {
  jobs -p | xargs -r kill 2>/dev/null || true
  if [[ "${KEEP:-0}" != 1 ]]; then rm -rf "$WORK"; else echo "Temporärer Ordner: $WORK"; fi
}
trap cleanup EXIT

# One instance per role: game files hard-linked (or copied), BepInEx as a real copy.
make_instance() {
  local dir="$WORK/$1"
  cp -al "$GAME_DIR" "$dir" 2>/dev/null || cp -a "$GAME_DIR" "$dir"
  rm -rf "$dir/BepInEx"
  cp -a "$GAME_DIR/BepInEx" "$dir/BepInEx"
  rm -rf "$dir/BepInEx/plugins" "$dir/BepInEx/config" "$dir/BepInEx/LogOutput.log"
  mkdir -p "$dir/BepInEx/plugins/OGMultiplayer"
  cp "$PLUGIN_DLL" "$TESTS_DLL" "$dir/BepInEx/plugins/OGMultiplayer/"
}
echo "==> Lege zwei Spielinstanzen an ..."
make_instance host
make_instance guest

PORT=$((27500 + RANDOM % 1000))
PASSWORD="test-$RANDOM"
printf '%s\n' "$PASSWORD" > "$WORK/password"

run_scenario() {
  local scenario="$1" disc="$2" name="$1:$2"
  local out="$WORK/$scenario-$disc" shots=""
  mkdir -p "$out"
  if [[ -n "${SHOTS:-}" ]]; then
    shots="$SHOTS/$scenario-$disc"
    mkdir -p "$shots"
  fi
  "$DOTNET" "$SERVER_DLL" --port "$PORT" --password-file "$WORK/password" --no-state --no-console \
    --pause-grace 8 > "$out/server.log" 2>&1 &
  local server=$!
  sleep 1
  local pids=()
  for role in host guest; do
    (
      cd "$WORK/$role"
      OGT_ROLE="$role" OGT_SCENARIO="$scenario" OGT_DISC="$disc" OGT_PORT="$PORT" OGT_PASS="$PASSWORD" \
      OGT_RESULT="$out/$role.txt" OGT_SHOTS="$shots" vblank_mode=0 \
        timeout 300 xvfb-run -a -s "-screen 0 1280x720x24" ./run_bepinex.sh ./OG.x86_64 \
          -force-glcore -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile "$out/$role-unity.log" \
          > /dev/null 2>&1 || true
      cp BepInEx/LogOutput.log "$out/$role-bepinex.log" 2>/dev/null || true
    ) &
    pids+=($!)
  done
  wait "${pids[@]}"
  kill "$server" 2>/dev/null || true
  wait "$server" 2>/dev/null || true

  local ok=1
  for role in host guest; do
    local file="$out/$role.txt"
    if [[ ! -s "$file" ]]; then
      echo "  [$name] $role: keine Ergebnisse (Spiel nicht gestartet? siehe $out/$role-unity.log)"
      ok=0
      continue
    fi
    sed "s/^/  [$name] $role: /" "$file"
    grep -q '^DONE ok$' "$file" || ok=0
  done
  return $((1 - ok))
}

failed=0
for spec in "${SCENARIOS[@]}"; do
  scenario="${spec%%:*}"
  disc="2"
  [[ "$spec" == *:* ]] && disc="${spec#*:}"
  discs=("$disc")
  [[ "$disc" == all ]] && discs=(0 1 2 3 4)
  for d in "${discs[@]}"; do
    echo "==> Szenario $scenario, Disziplin $d"
    run_scenario "$scenario" "$d" || failed=$((failed + 1))
  done
done

if [[ -n "${SHOTS:-}" ]]; then echo "Screenshots: $SHOTS"; fi
if [[ $failed -gt 0 ]]; then
  echo "==> $failed Szenario(s) fehlgeschlagen."
  KEEP=1
  exit 1
fi
echo "==> Alle Szenarien bestanden."
