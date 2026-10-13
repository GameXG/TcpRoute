#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ANDROID_BUILD_DIR="$ROOT_DIR/.build/android-core"
JNI_LIBS_DIR="$SCRIPT_DIR/app/src/main/jniLibs"

bash "$ROOT_DIR/scripts/fetch-geosite.sh"

copy_required() {
  local src="$1"
  local dest="$2"
  if [[ ! -f "$src" ]]; then
    echo "ERROR: missing required binary: $src" >&2
    return 1
  fi
  mkdir -p "$(dirname "$dest")"
  cp "$src" "$dest"
  chmod +x "$dest"
  echo "Copied $src -> $dest"
}

copy_required "$ANDROID_BUILD_DIR/TcpRoute2-android-arm64-v8a" "$JNI_LIBS_DIR/arm64-v8a/libtcproute.so"

bash "$SCRIPT_DIR/fetch-tun2proxy-android.sh"
bash "$SCRIPT_DIR/build-jni-bridge.sh"

echo "Android native binary staging complete."
