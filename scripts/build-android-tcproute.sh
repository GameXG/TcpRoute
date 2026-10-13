#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT_DIR/.build/android-core"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
API_LEVEL="${ANDROID_API_LEVEL:-23}"

if ! command -v go >/dev/null 2>&1; then
  echo "ERROR: Go is required to build TcpRoute2 Android binaries." >&2
  echo "Install Go 1.25+ or provide prebuilt binaries in .build/android-core/." >&2
  exit 1
fi

mkdir -p "$BUILD_DIR"

find_android_sdk() {
  if [[ -n "${ANDROID_HOME:-}" && -d "$ANDROID_HOME" ]]; then
    printf '%s\n' "$ANDROID_HOME"
    return 0
  fi
  if [[ -n "${ANDROID_SDK_ROOT:-}" && -d "$ANDROID_SDK_ROOT" ]]; then
    printf '%s\n' "$ANDROID_SDK_ROOT"
    return 0
  fi
  if [[ -f "$ROOT_DIR/android/local.properties" ]]; then
    local sdk_dir
    sdk_dir="$(sed -n 's/^sdk\.dir=//p' "$ROOT_DIR/android/local.properties" | tail -1)"
    if [[ -n "$sdk_dir" && -d "$sdk_dir" ]]; then
      printf '%s\n' "$sdk_dir"
      return 0
    fi
  fi
  if [[ -d "$HOME/Library/Android/sdk" ]]; then
    printf '%s\n' "$HOME/Library/Android/sdk"
    return 0
  fi
  return 1
}

find_ndk_toolchain_bin() {
  local sdk_dir="$1"
  local ndk_dir="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
  if [[ -z "$ndk_dir" ]]; then
    ndk_dir="$(find "$sdk_dir/ndk" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort | tail -1)"
  fi
  if [[ -z "$ndk_dir" || ! -d "$ndk_dir" ]]; then
    echo "ERROR: Android NDK is required for Android cross builds." >&2
    echo "Install it with sdkmanager 'ndk;28.2.13676358' or set ANDROID_NDK_HOME." >&2
    exit 1
  fi
  local host_tag
  case "$(uname -s)" in
    Darwin) host_tag="darwin-x86_64" ;;
    Linux) host_tag="linux-x86_64" ;;
    *) echo "ERROR: unsupported host OS for Android NDK: $(uname -s)" >&2; exit 1 ;;
  esac
  local toolchain_bin="$ndk_dir/toolchains/llvm/prebuilt/$host_tag/bin"
  if [[ ! -d "$toolchain_bin" ]]; then
    echo "ERROR: Android NDK LLVM toolchain not found: $toolchain_bin" >&2
    exit 1
  fi
  printf '%s\n' "$toolchain_bin"
}

SDK_DIR="$(find_android_sdk || true)"
if [[ -z "$SDK_DIR" ]]; then
  echo "ERROR: Android SDK not found. Set ANDROID_HOME or ANDROID_SDK_ROOT." >&2
  exit 1
fi
TOOLCHAIN_BIN="$(find_ndk_toolchain_bin "$SDK_DIR")"

build_one() {
  local abi="$1"
  local goarch="$2"
  local goarm="${3:-}"
  local clang_prefix="$4"
  local output="$BUILD_DIR/TcpRoute2-android-$abi"

  echo "==> Building TcpRoute2 for Android $abi"
  if [[ -n "$goarm" ]]; then
    GOOS=android GOARCH="$goarch" GOARM="$goarm" CGO_ENABLED=1 \
      CC="$TOOLCHAIN_BIN/${clang_prefix}${API_LEVEL}-clang" \
      CXX="$TOOLCHAIN_BIN/${clang_prefix}${API_LEVEL}-clang++" \
      go build -trimpath -ldflags="-s -w -X main.version=$VERSION" -o "$output" .
  else
    GOOS=android GOARCH="$goarch" CGO_ENABLED=1 \
      CC="$TOOLCHAIN_BIN/${clang_prefix}${API_LEVEL}-clang" \
      CXX="$TOOLCHAIN_BIN/${clang_prefix}${API_LEVEL}-clang++" \
      go build -trimpath -ldflags="-s -w -X main.version=$VERSION" -o "$output" .
  fi
  chmod +x "$output"
}

cd "$ROOT_DIR"
build_one arm64-v8a arm64 "" aarch64-linux-android

echo "Android TcpRoute2 binaries written to $BUILD_DIR"
