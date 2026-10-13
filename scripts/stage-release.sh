#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
DIST_DIR="$ROOT_DIR/dist"
CHECKSUM_FILE="$DIST_DIR/SHA256SUMS.txt"

ASSETS=(
  "$DIST_DIR/TcpRoute2-v$VERSION-android.apk"
  "$DIST_DIR/TcpRoute2-v$VERSION-linux-amd64.tar.gz"
  "$DIST_DIR/TcpRoute2-v$VERSION-linux-arm64.tar.gz"
  "$DIST_DIR/TcpRoute2-v$VERSION-core-macos-arm64.tar.gz"
  "$DIST_DIR/TcpRoute2-v$VERSION-core-windows-amd64.zip"
  "$DIST_DIR/TcpRoute2-v$VERSION-core-windows-arm64.zip"
  "$DIST_DIR/TcpRoute2-v$VERSION-win-x64.zip"
  "$DIST_DIR/TcpRoute2-v$VERSION-win-arm64.zip"
)

for asset in "${ASSETS[@]}"; do
  if [[ ! -f "$asset" ]]; then
    echo "ERROR: release asset is missing: $asset" >&2
    exit 1
  fi
done

(
  cd "$DIST_DIR"
  : > "$CHECKSUM_FILE"
  for asset in "${ASSETS[@]}"; do
    shasum -a 256 "$(basename "$asset")" >> "$CHECKSUM_FILE"
  done
)

echo "Release assets and checksums are ready in $DIST_DIR"
