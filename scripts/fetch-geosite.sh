#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSET_DIR="$ROOT_DIR/assets"
VERSION="202608012248"
GEOSITE_SHA256="814a2028109e9e361d67efe9eadb18068c19a72a6eb8f3988da59617f4761fbb"
GEOIP_SHA256="6ba63d75f307d16a81ae09406ddcf2779fa75cb642d4aae59613370d62d33509"
BASE_URL="https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/$VERSION"

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

# Release packaging uses a private snapshot so parallel packages cannot race on assets/.
if [[ "${1:-}" == "--latest" ]]; then
  if [[ $# -ne 2 ]]; then
    echo "Usage: $0 --latest <snapshot-directory>" >&2
    exit 2
  fi
  ASSET_DIR="$2"
  mkdir -p "$ASSET_DIR"
  RELEASE_URL="$(curl --fail --location --silent --show-error --retry 3 --output /dev/null --write-out '%{url_effective}' https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest)"
  case "$RELEASE_URL" in
    https://github.com/Loyalsoldier/v2ray-rules-dat/releases/tag/*) VERSION="${RELEASE_URL##*/}" ;;
    *) echo "ERROR: unexpected Geo release URL: $RELEASE_URL" >&2; exit 1 ;;
  esac
  [[ "$VERSION" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "ERROR: invalid Geo release tag" >&2; exit 1; }
  BASE_URL="https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/$VERSION"
  for name in geosite.dat geoip.dat; do
    curl --fail --location --silent --show-error --retry 3 --output "$ASSET_DIR/$name.sha256sum" "$BASE_URL/$name.sha256sum"
    expected="$(awk 'NR == 1 {print $1}' "$ASSET_DIR/$name.sha256sum")"
    [[ "$expected" =~ ^[[:xdigit:]]{64}$ ]] || { echo "ERROR: invalid $name checksum" >&2; exit 1; }
    curl --fail --location --silent --show-error --retry 3 --output "$ASSET_DIR/$name" "$BASE_URL/$name"
    actual="$(sha256_file "$ASSET_DIR/$name")"
    [[ "$(printf '%s' "$expected" | tr 'A-F' 'a-f')" == "$actual" ]] || { echo "ERROR: $name checksum mismatch" >&2; exit 1; }
  done
  printf '%s\n' "$VERSION" > "$ASSET_DIR/release-version.txt"
  echo "Verified latest Geo pair: $VERSION"
  exit 0
fi

fetch_asset() {
  local name="$1"
  local expected="$2"
  local target="$ASSET_DIR/$name"
  local checksum_file="$target.sha256"
  if [[ -f "$target" ]] && [[ "$(sha256_file "$target")" == "$expected" ]]; then
    printf '%s\n' "$expected" > "$checksum_file"
    echo "$name is already up to date."
    return
  fi
  local temporary="$target.tmp"
  curl --fail --location --retry 5 --retry-all-errors --continue-at - --output "$temporary" "$BASE_URL/$name"
  local actual
  actual="$(sha256_file "$temporary")"
  if [[ "$actual" != "$expected" ]]; then
    echo "ERROR: $name checksum mismatch (expected $expected, got $actual)." >&2
    rm -f "$temporary"
    exit 1
  fi
  mv "$temporary" "$target"
  printf '%s\n' "$expected" > "$checksum_file"
  echo "Downloaded $name $VERSION to $target"
}

mkdir -p "$ASSET_DIR"
fetch_asset geosite.dat "$GEOSITE_SHA256"
fetch_asset geoip.dat "$GEOIP_SHA256"
