#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"
CORE_BUILD_DIR="$ROOT_DIR/.build/release-core"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
PACKAGE_BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/tcproute-cli-package.XXXXXX")"

trap 'rm -rf "$PACKAGE_BUILD_DIR"' EXIT

GEO_ASSET_DIR="$PACKAGE_BUILD_DIR/geo"
bash "$ROOT_DIR/scripts/fetch-geosite.sh" --latest "$GEO_ASSET_DIR"
mkdir -p "$DIST_DIR"

package_linux() {
  local arch="$1"
  local binary="$CORE_BUILD_DIR/TcpRoute2-linux-$arch"
  local package_name="TcpRoute2-v$VERSION-linux-$arch"
  local package_dir="$PACKAGE_BUILD_DIR/$package_name"

  if [[ ! -x "$binary" ]]; then
    echo "ERROR: release binary is missing: $binary" >&2
    exit 1
  fi

  rm -rf "$package_dir"
  mkdir -p "$package_dir"
  cp "$binary" "$package_dir/TcpRoute2"
  cp "$GEO_ASSET_DIR/geosite.dat" "$package_dir/geosite.dat"
  cp "$GEO_ASSET_DIR/geoip.dat" "$package_dir/geoip.dat"
  cp "$ROOT_DIR/config.toml.example" "$package_dir/config.toml.example"
  cp "$ROOT_DIR/README.md" "$package_dir/README.md"
  cp "$ROOT_DIR/LICENSE" "$package_dir/LICENSE"
  tar -C "$PACKAGE_BUILD_DIR" -czf "$DIST_DIR/$package_name.tar.gz" "$package_name"
  echo "Built $DIST_DIR/$package_name.tar.gz"
}

package_linux amd64
package_linux arm64

package_standalone_core() {
  local goos="$1" arch="$2" platform="$3" extension="$4"
  local binary="$CORE_BUILD_DIR/TcpRoute2-$goos-$arch$extension"
  local package_name="TcpRoute2-v$VERSION-core-$platform-$arch"
  local package_dir="$PACKAGE_BUILD_DIR/$package_name"
  [[ -x "$binary" ]] || { echo "ERROR: release binary is missing: $binary" >&2; exit 1; }
  mkdir -p "$package_dir"
  cp "$binary" "$package_dir/TcpRoute2$extension"
  cp "$GEO_ASSET_DIR/geosite.dat" "$package_dir/geosite.dat"
  cp "$GEO_ASSET_DIR/geoip.dat" "$package_dir/geoip.dat"
  cp "$ROOT_DIR/config.toml.example" "$package_dir/config.toml.example"
  cp "$ROOT_DIR/LICENSE" "$package_dir/LICENSE"
  cp "$ROOT_DIR/README.md" "$package_dir/README.md"
  if [[ "$goos" == "windows" ]]; then
    (cd "$PACKAGE_BUILD_DIR" && zip -qry "$package_name.zip" "$package_name")
    mv "$PACKAGE_BUILD_DIR/$package_name.zip" "$DIST_DIR/$package_name.zip"
  else
    tar -C "$PACKAGE_BUILD_DIR" -czf "$DIST_DIR/$package_name.tar.gz" "$package_name"
  fi
}

package_standalone_core darwin arm64 macos ""
package_standalone_core windows amd64 windows ".exe"
package_standalone_core windows arm64 windows ".exe"
