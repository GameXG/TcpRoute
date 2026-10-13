#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ ! -x "$SCRIPT_DIR/gradlew" ]]; then
  echo "ERROR: Gradle wrapper is missing." >&2
  echo "Run: bash android/generate-gradle-wrapper.sh" >&2
  exit 1
fi

if [[ ! -f "$ROOT_DIR/.build/android-core/TcpRoute2-android-arm64-v8a" ]]; then
  echo "ERROR: Android TcpRoute2 binaries are missing." >&2
  echo "Run: bash scripts/build-android-tcproute.sh" >&2
  exit 1
fi

bash "$SCRIPT_DIR/prepare-native-binaries.sh"
cd "$SCRIPT_DIR"
./gradlew assembleDebug

OUTPUT_APK="$ROOT_DIR/.build/android/TcpRoute2-debug-android-arm64.apk"
mkdir -p "$ROOT_DIR/.build/android"
cp "$SCRIPT_DIR/app/build/outputs/apk/debug/app-debug.apk" "$OUTPUT_APK"
echo "Built $OUTPUT_APK"
