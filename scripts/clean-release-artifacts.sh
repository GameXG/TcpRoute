#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"

if [[ -z "$VERSION" ]]; then
  echo "ERROR: VERSION is empty" >&2
  exit 1
fi

mkdir -p "$DIST_DIR"
shopt -s nullglob

for artifact in \
  "$DIST_DIR"/TcpRoute-v*.zip \
  "$DIST_DIR"/TcpRoute-v*.tar.gz \
  "$DIST_DIR"/TcpRoute-v*-android.apk \
  "$DIST_DIR"/TcpRoute-v*-android.apk.idsig \
  "$DIST_DIR"/TcpRoute2-v*.zip \
  "$DIST_DIR"/TcpRoute2-v*.tar.gz \
  "$DIST_DIR"/TcpRoute2-v*-android.apk \
  "$DIST_DIR"/TcpRoute2-v*-android.apk.idsig \
  "$DIST_DIR"/TcpRoute2-v*-linux-amd64.tar.gz \
  "$DIST_DIR"/TcpRoute2-v*-linux-arm64.tar.gz \
  "$DIST_DIR"/TcpRoute2-v*-macos-arm64.zip \
  "$DIST_DIR"/TcpRoute2-v*-core-macos-arm64.tar.gz \
  "$DIST_DIR"/TcpRoute2-v*-core-windows-amd64.zip \
  "$DIST_DIR"/TcpRoute2-v*-core-windows-arm64.zip \
  "$DIST_DIR"/TcpRoute2-v*-win-x64.zip \
  "$DIST_DIR"/TcpRoute2-v*-win-arm64.zip; do
  case "$(basename "$artifact")" in
    "TcpRoute2-v$VERSION-android.apk"|\
    "TcpRoute2-v$VERSION-linux-amd64.tar.gz"|\
    "TcpRoute2-v$VERSION-linux-arm64.tar.gz"|\
    "TcpRoute2-v$VERSION-core-macos-arm64.tar.gz"|\
    "TcpRoute2-v$VERSION-core-windows-amd64.zip"|\
    "TcpRoute2-v$VERSION-core-windows-arm64.zip"|\
    "TcpRoute2-v$VERSION-win-x64.zip"|\
    "TcpRoute2-v$VERSION-win-arm64.zip")
      ;;
    *)
      rm -f "$artifact"
      echo "Removed old release artifact: $artifact"
      ;;
  esac
done

for expanded_dir in "$DIST_DIR"/TcpRoute2-v*-linux-amd64 "$DIST_DIR"/TcpRoute2-v*-linux-arm64; do
  rm -rf "$expanded_dir"
  echo "Removed expanded package directory: $expanded_dir"
done

for legacy_dir in "$DIST_DIR/android" "$DIST_DIR"/release-v* "$ROOT_DIR/macos/dist" "$ROOT_DIR/windows/dist"; do
  if [[ -e "$legacy_dir" ]]; then
    rm -rf "$legacy_dir"
    echo "Removed legacy artifact directory: $legacy_dir"
  fi
done

rm -f \
  "$DIST_DIR/client-packages-sha256.txt" \
  "$DIST_DIR/TcpRoute2-android-arm64-v8a" \
  "$DIST_DIR/TcpRoute2-android-arm64-v8a.sha256" \
  "$DIST_DIR/TcpRoute2-darwin-amd64" \
  "$DIST_DIR/TcpRoute2-darwin-arm64" \
  "$DIST_DIR/TcpRoute2-linux-amd64" \
  "$DIST_DIR/TcpRoute2-linux-arm64" \
  "$DIST_DIR/TcpRoute2-windows-amd64.exe" \
  "$DIST_DIR/TcpRoute2-windows-arm64.exe" \
  "$DIST_DIR/TcpRoute2Android-debug.apk" \
  "$DIST_DIR/version.txt" \
  "$DIST_DIR/geosite.dat" \
  "$DIST_DIR/.DS_Store"
rm -rf "$DIST_DIR/TcpRoute2.app"
echo "Release artifact cleanup complete; retained TcpRoute2 v$VERSION packages in $DIST_DIR"
