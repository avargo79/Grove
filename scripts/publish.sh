#!/usr/bin/env bash
#
# Produces a self-contained build — self-contained because the point of this project is that it
# runs where installing a .NET runtime may not be an option.
#
# Usage:  ./scripts/publish.sh [runtime-identifier]
#   e.g.  ./scripts/publish.sh osx-arm64
#         ./scripts/publish.sh win-x64
#         ./scripts/publish.sh linux-x64
#
# With no argument it builds for the machine it is running on.
#
# Windows and Linux get a single self-contained executable. macOS gets a .app bundle instead:
# recent macOS kills a bare adhoc-signed executable on launch (SIGKILL, no crash report and
# nothing in the log), and bundling then adhoc-signing is what actually runs.
#
set -euo pipefail

cd "$(dirname "$0")/.."

RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Linux-x86_64)  RID=linux-x64 ;;
    *) echo "Could not guess a runtime identifier; pass one explicitly." >&2; exit 1 ;;
  esac
fi

OUT="artifacts/$RID"
rm -rf "$OUT"

case "$RID" in
  osx-*)
    STAGE="$OUT/stage"

    # Trimming is deliberately off: Avalonia resolves a good deal by reflection, and a trimmed
    # build fails at runtime rather than at build time.
    dotnet publish src/Grove.App \
      --configuration Release \
      --runtime "$RID" \
      --self-contained true \
      -p:PublishSingleFile=false \
      -p:PublishTrimmed=false \
      --output "$STAGE"

    APP="$OUT/Grove.app"
    mkdir -p "$APP/Contents/MacOS"

    cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Grove</string>
  <key>CFBundleDisplayName</key><string>Grove</string>
  <key>CFBundleIdentifier</key><string>com.grove.app</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>Grove.App</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>CFBundleIconFile</key><string>grove</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

    mkdir -p "$APP/Contents/Resources"
    cp src/Grove.App/Assets/grove.icns "$APP/Contents/Resources/grove.icns"

    cp -R "$STAGE/." "$APP/Contents/MacOS/"
    rm -rf "$STAGE"

    # Clear inherited attributes before signing, or the signature is invalidated immediately.
    xattr -cr "$APP" 2>/dev/null || true
    codesign --force --deep --sign - "$APP"

    echo
    echo "Built $RID → $APP"
    echo "Run it with:  open '$APP' --args /path/to/repo"
    ;;

  *)
    dotnet publish src/Grove.App \
      --configuration Release \
      --runtime "$RID" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:PublishTrimmed=false \
      --output "$OUT"

    echo
    echo "Built $RID → $OUT"
    ls -lh "$OUT" | head -10
    ;;
esac
