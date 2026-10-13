#!/usr/bin/env bash
set -euo pipefail

ARCH="${1:?architecture is required}"
DESTINATION="${2:?temporary snapshot directory is required}"
case "$ARCH" in amd64|arm64) ;; *) echo "Unsupported Wintun architecture: $ARCH" >&2; exit 2 ;; esac
VERSION="0.14.1"
EXPECTED="07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
mkdir -p "$DESTINATION"
ARCHIVE="$DESTINATION/wintun.zip"
curl --fail --location --silent --show-error --retry 3 --output "$ARCHIVE" "https://www.wintun.net/builds/wintun-$VERSION.zip"
if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
else
  ACTUAL="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
fi
[[ "$ACTUAL" == "$EXPECTED" ]] || { echo "Wintun archive checksum mismatch" >&2; exit 1; }
unzip -p "$ARCHIVE" "wintun/bin/$ARCH/wintun.dll" > "$DESTINATION/wintun.dll"
unzip -p "$ARCHIVE" "wintun/LICENSE.txt" > "$DESTINATION/WINTUN-LICENSE.txt"
[[ -s "$DESTINATION/wintun.dll" && -s "$DESTINATION/WINTUN-LICENSE.txt" ]] || { echo "Wintun archive is incomplete" >&2; exit 1; }
echo "Verified Wintun $VERSION ($ARCH)"
