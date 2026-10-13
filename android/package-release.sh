#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
SDK_DIR="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
BUILD_TOOLS_VERSION="${ANDROID_BUILD_TOOLS_VERSION:-37.0.0}"
ZIPALIGN="$SDK_DIR/build-tools/$BUILD_TOOLS_VERSION/zipalign"
APKSIGNER="$SDK_DIR/build-tools/$BUILD_TOOLS_VERSION/apksigner"
KEYSTORE="${ANDROID_KEYSTORE_PATH:-$HOME/.android/debug.keystore}"
KEY_ALIAS="${ANDROID_KEY_ALIAS:-androiddebugkey}"
export ANDROID_KEYSTORE_PASSWORD="${ANDROID_KEYSTORE_PASSWORD:-android}"
export ANDROID_KEY_PASSWORD="${ANDROID_KEY_PASSWORD:-android}"
OUTPUT_DIR="$ROOT_DIR/dist"
UNSIGNED_APK="$SCRIPT_DIR/app/build/outputs/apk/release/app-release-unsigned.apk"
ALIGNED_APK="$OUTPUT_DIR/TcpRoute2-v$VERSION-android-aligned.apk"
OUTPUT_APK="$OUTPUT_DIR/TcpRoute2-v$VERSION-android.apk"

for command_path in "$ZIPALIGN" "$APKSIGNER" "$KEYSTORE"; do
  if [[ ! -f "$command_path" ]]; then
    echo "ERROR: required release signing file is missing: $command_path" >&2
    exit 1
  fi
done

if [[ "$KEYSTORE" == "$HOME/.android/debug.keystore" ]]; then
  echo "WARNING: signing the direct-distribution APK with the local Android debug keystore." >&2
fi

bash "$SCRIPT_DIR/prepare-native-binaries.sh"
cd "$SCRIPT_DIR"
./gradlew assembleRelease

mkdir -p "$OUTPUT_DIR"
"$ZIPALIGN" -f -p 4 "$UNSIGNED_APK" "$ALIGNED_APK"
"$APKSIGNER" sign \
  --ks "$KEYSTORE" \
  --ks-key-alias "$KEY_ALIAS" \
  --ks-pass env:ANDROID_KEYSTORE_PASSWORD \
  --key-pass env:ANDROID_KEY_PASSWORD \
  --out "$OUTPUT_APK" \
  "$ALIGNED_APK"
"$APKSIGNER" verify --verbose --print-certs "$OUTPUT_APK"

rm -f "$ALIGNED_APK" "$OUTPUT_APK.idsig"
echo "Built $OUTPUT_APK"
