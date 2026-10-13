#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNTIME="${1:-win-x64}"
VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
DIST_DIR="$ROOT_DIR/dist"
PACKAGE_BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/tcproute-windows-package.XXXXXX")"
OUTPUT_DIR="$PACKAGE_BUILD_DIR/TcpRoute2-$RUNTIME"
ARCHIVE="$DIST_DIR/TcpRoute2-v$VERSION-$RUNTIME.zip"

trap 'rm -rf "$PACKAGE_BUILD_DIR"' EXIT

case "$RUNTIME" in
  win-x64) CORE_ARCH="amd64" ;;
  win-arm64) CORE_ARCH="arm64" ;;
  *) echo "ERROR: runtime must be win-x64 or win-arm64" >&2; exit 2 ;;
esac
CORE_EXE="${TCPROUTE_EXE:-$ROOT_DIR/.build/release-core/TcpRoute2-windows-$CORE_ARCH.exe}"

if [[ ! -f "$CORE_EXE" ]]; then
  echo "ERROR: TcpRoute2 core binary is missing: $CORE_EXE" >&2
  exit 1
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$DIST_DIR" "$OUTPUT_DIR"

GEO_ASSET_DIR="$PACKAGE_BUILD_DIR/geo"
bash "$ROOT_DIR/scripts/fetch-geosite.sh" --latest "$GEO_ASSET_DIR"
WINTUN_ASSET_DIR="$PACKAGE_BUILD_DIR/wintun"
bash "$ROOT_DIR/scripts/fetch-wintun.sh" "$CORE_ARCH" "$WINTUN_ASSET_DIR"

dotnet publish "$SCRIPT_DIR/TcpRouteTray/TcpRouteTray.csproj" \
  -c Release \
  -r "$RUNTIME" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:Version="$VERSION.0" \
  -p:AssemblyVersion="$VERSION.0.0" \
  -p:FileVersion="$VERSION.0.0" \
  -o "$OUTPUT_DIR"

mv "$OUTPUT_DIR/TcpRoute2.exe" "$OUTPUT_DIR/TcpRoute2_Windows_${RUNTIME#win-}.exe"

cp "$CORE_EXE" "$OUTPUT_DIR/TcpRoute2.exe"
cp "$WINTUN_ASSET_DIR/wintun.dll" "$OUTPUT_DIR/wintun.dll"
cp "$WINTUN_ASSET_DIR/WINTUN-LICENSE.txt" "$OUTPUT_DIR/WINTUN-LICENSE.txt"
cp "$GEO_ASSET_DIR/geosite.dat" "$OUTPUT_DIR/geosite.dat"
cp "$GEO_ASSET_DIR/geoip.dat" "$OUTPUT_DIR/geoip.dat"
cp "$ROOT_DIR/config.toml.example" "$OUTPUT_DIR/config.toml.example"
cp "$ROOT_DIR/LICENSE" "$OUTPUT_DIR/LICENSE.txt"

rm -f "$ARCHIVE"
(
  cd "$PACKAGE_BUILD_DIR"
  zip -qry "$(basename "$ARCHIVE")" "$(basename "$OUTPUT_DIR")"
)

mv "$PACKAGE_BUILD_DIR/$(basename "$ARCHIVE")" "$ARCHIVE"

echo "Built $ARCHIVE"
