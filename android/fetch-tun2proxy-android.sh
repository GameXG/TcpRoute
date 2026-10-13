#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="0.8.2"
ARCHIVE_SHA256="903f8b780743df5a22ba44e50e1b02cbb0a190ac252ffb03c84d75ef9c0754a9"
ARCHIVE_NAME="tun2proxy-android-libs.zip"
DOWNLOAD_URL="https://github.com/tun2proxy/tun2proxy/releases/download/v${VERSION}/${ARCHIVE_NAME}"
CACHE_DIR="$SCRIPT_DIR/.gradle/tun2proxy-v${VERSION}"
ARCHIVE_PATH="$CACHE_DIR/$ARCHIVE_NAME"
JNI_LIBS_DIR="$SCRIPT_DIR/app/src/main/jniLibs"

sha256_file() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    sha256sum "$1" | awk '{print $1}'
  fi
}

mkdir -p "$CACHE_DIR"
if [[ ! -f "$ARCHIVE_PATH" ]] || [[ "$(sha256_file "$ARCHIVE_PATH")" != "$ARCHIVE_SHA256" ]]; then
  echo "Downloading tun2proxy v${VERSION} Android libraries..."
  curl --fail --location --retry 3 --output "$ARCHIVE_PATH" "$DOWNLOAD_URL"
fi

actual_sha256="$(sha256_file "$ARCHIVE_PATH")"
if [[ "$actual_sha256" != "$ARCHIVE_SHA256" ]]; then
  echo "ERROR: tun2proxy archive checksum mismatch." >&2
  echo "Expected: $ARCHIVE_SHA256" >&2
  echo "Actual:   $actual_sha256" >&2
  exit 1
fi

for abi in arm64-v8a; do
  destination="$JNI_LIBS_DIR/$abi/libtun2proxy.so"
  mkdir -p "$(dirname "$destination")"
  unzip -p "$ARCHIVE_PATH" "tun2proxy-android-libs/$abi/libtun2proxy.so" > "$destination"
  chmod +x "$destination"
  echo "Prepared tun2proxy v${VERSION} for $abi"
done
