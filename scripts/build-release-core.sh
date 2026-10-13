#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT_DIR/.build/release-core"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"

if [[ -z "$VERSION" ]]; then
  echo "ERROR: VERSION is empty" >&2
  exit 1
fi

mkdir -p "$BUILD_DIR"
cd "$ROOT_DIR"

build_one() {
  local goos="$1"
  local goarch="$2"
  local output="$3"

  echo "==> Building TcpRoute2 v$VERSION for $goos/$goarch"
  CGO_ENABLED=0 GOOS="$goos" GOARCH="$goarch" \
    go build -trimpath -ldflags="-s -w -X main.version=$VERSION" -o "$BUILD_DIR/$output" .
}

build_one darwin arm64 TcpRoute2-darwin-arm64
build_one linux arm64 TcpRoute2-linux-arm64
build_one linux amd64 TcpRoute2-linux-amd64
build_one windows arm64 TcpRoute2-windows-arm64.exe
build_one windows amd64 TcpRoute2-windows-amd64.exe

echo "Release core binaries written to $BUILD_DIR"
