#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v java >/dev/null 2>&1 || ! java -version >/dev/null 2>&1; then
  echo "ERROR: Java/JDK is required to generate and run the Gradle wrapper." >&2
  echo "Install JDK 17+ first, then re-run: bash android/generate-gradle-wrapper.sh" >&2
  exit 1
fi

if ! command -v gradle >/dev/null 2>&1; then
  echo "ERROR: gradle is required once to generate gradle/wrapper/gradle-wrapper.jar." >&2
  echo "Install Gradle, or run 'gradle wrapper --gradle-version 8.10.2' manually from android/." >&2
  exit 1
fi

cd "$SCRIPT_DIR"
gradle wrapper --gradle-version 8.10.2 --distribution-type bin
echo "Generated Gradle wrapper in $SCRIPT_DIR"
