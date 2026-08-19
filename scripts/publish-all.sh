#!/usr/bin/env bash
#
# Builds every supported platform in one go. Cross-building works from any host: the .NET SDK
# ships the target runtimes, so a Mac can produce the Windows build.
#
set -euo pipefail

cd "$(dirname "$0")/.."

for rid in win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64; do
  echo "=== $rid ==="
  ./scripts/publish.sh "$rid"
done
