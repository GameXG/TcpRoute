#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CPP_DIR="$SCRIPT_DIR/app/src/main/cpp"
JNI_LIBS_DIR="$SCRIPT_DIR/app/src/main/jniLibs"
API_LEVEL="${ANDROID_API_LEVEL:-26}"

find_android_sdk() {
  if [[ -n "${ANDROID_HOME:-}" && -d "$ANDROID_HOME" ]]; then
    printf '%s\n' "$ANDROID_HOME"
  elif [[ -n "${ANDROID_SDK_ROOT:-}" && -d "$ANDROID_SDK_ROOT" ]]; then
    printf '%s\n' "$ANDROID_SDK_ROOT"
  elif [[ -f "$SCRIPT_DIR/local.properties" ]]; then
    sed -n 's/^sdk\.dir=//p' "$SCRIPT_DIR/local.properties" | tail -1
  elif [[ -d "$HOME/Library/Android/sdk" ]]; then
    printf '%s\n' "$HOME/Library/Android/sdk"
  else
    return 1
  fi
}

SDK_DIR="$(find_android_sdk || true)"
if [[ -z "$SDK_DIR" || ! -d "$SDK_DIR" ]]; then
  echo "ERROR: Android SDK not found." >&2
  exit 1
fi

NDK_DIR="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
if [[ -z "$NDK_DIR" ]]; then
  NDK_DIR="$(find "$SDK_DIR/ndk" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort | tail -1)"
fi
if [[ -z "$NDK_DIR" || ! -d "$NDK_DIR" ]]; then
  echo "ERROR: Android NDK not found." >&2
  exit 1
fi

case "$(uname -s)" in
  Darwin) HOST_TAG="darwin-x86_64" ;;
  Linux) HOST_TAG="linux-x86_64" ;;
  *) echo "ERROR: unsupported host OS: $(uname -s)" >&2; exit 1 ;;
esac
TOOLCHAIN_BIN="$NDK_DIR/toolchains/llvm/prebuilt/$HOST_TAG/bin"

build_one() {
  local abi="$1"
  local clang_prefix="$2"
  local cxx="$TOOLCHAIN_BIN/${clang_prefix}${API_LEVEL}-clang++"
  local bridge_output="$JNI_LIBS_DIR/$abi/libtcproute_bridge.so"
  local runner_output="$JNI_LIBS_DIR/$abi/libtun2proxy_runner.so"

  if [[ ! -x "$cxx" ]]; then
    echo "ERROR: Android compiler not found: $cxx" >&2
    exit 1
  fi
  if [[ ! -f "$JNI_LIBS_DIR/$abi/libtun2proxy.so" ]]; then
    echo "ERROR: tun2proxy library missing for $abi." >&2
    exit 1
  fi

  "$cxx" \
    -fPIE \
    -pie \
    -O2 \
    -std=c++17 \
    -static-libstdc++ \
    '-Wl,-rpath,$ORIGIN' \
    -I"$CPP_DIR" \
    "$CPP_DIR/tun2proxy_runner.cpp" \
    -L"$JNI_LIBS_DIR/$abi" \
    -ltun2proxy \
    -o "$runner_output"

  "$cxx" \
    -shared \
    -fPIC \
    -O2 \
    -std=c++17 \
    -static-libstdc++ \
    -Wl,-soname,libtcproute_bridge.so \
    -I"$CPP_DIR" \
    "$CPP_DIR/tun2proxy_jni.cpp" \
    -L"$JNI_LIBS_DIR/$abi" \
    -llog \
    -o "$bridge_output"
  echo "Built isolated tun2proxy runner and JNI VPN bridge for $abi"
}

build_one arm64-v8a aarch64-linux-android
