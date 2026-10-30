#!/usr/bin/env bash
# Bumps the pinned BepInEx 5 (linux x64) release in scripts/install.sh and flake.nix
# to the newest 5.x release on GitHub. Prints the new version, or nothing if current.
# Needs: curl, jq, sha256sum, nix (for the SRI hash).
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR"

current="$(sed -n 's/^BEPINEX_VERSION="\(.*\)"$/\1/p' scripts/install.sh)"
auth=()
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  auth=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi
releases_url="${BEPINEX_RELEASES_URL:-https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=50}"
latest="$(curl -fsSL "${auth[@]}" "$releases_url" \
  | jq -r '[.[] | select(.prerelease == false and .draft == false) | .tag_name | ltrimstr("v") | select(startswith("5."))] | first // empty')"

if [[ -z "$latest" ]]; then
  echo "Keine BepInEx-5-Version gefunden" >&2
  exit 1
fi
if [[ "$latest" == "$current" ]]; then
  exit 0
fi
newest="$(printf '%s\n%s\n' "$current" "$latest" | sort -V | tail -n1)"
if [[ "$newest" != "$latest" ]]; then
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
url="https://github.com/BepInEx/BepInEx/releases/download/v${latest}/BepInEx_linux_x64_${latest}.zip"
curl -fsSL -o "$tmp/bepinex.zip" "$url"
unzip -l "$tmp/bepinex.zip" | grep -q "BepInEx/core/BepInEx.dll"
hex="$(sha256sum "$tmp/bepinex.zip" | cut -d' ' -f1)"
sri="$(nix --extra-experimental-features nix-command hash file --type sha256 --sri "$tmp/bepinex.zip")"

sed -i \
  -e "s/^BEPINEX_VERSION=\".*\"$/BEPINEX_VERSION=\"${latest}\"/" \
  -e "s/^BEPINEX_SHA256=\".*\"$/BEPINEX_SHA256=\"${hex}\"/" \
  scripts/install.sh
sed -i \
  -e "s/bepinexVersion = \".*\";/bepinexVersion = \"${latest}\";/" \
  -e "s|hash = \"sha256-[^\"]*\"; # bepinex|hash = \"${sri}\"; # bepinex|" \
  flake.nix
sed -i "s/BepInEx [0-9][0-9.]* (gepinnt/BepInEx ${latest} (gepinnt/" README.md

echo "$latest"
