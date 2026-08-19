#!/usr/bin/env bash
# Regenerates the application icon from src/Grove.App/Controls/GroveIcon.cs.
#
# The mark is drawn in code, so this script only rasterises it and packs the results into the
# formats the platforms want: a PNG for the Avalonia window icon, an .ico for Windows, and an
# .icns for the macOS bundle. Run it after changing GroveIcon; the outputs are committed so a
# plain build or publish needs neither this script nor a Mac.
set -euo pipefail

cd "$(dirname "$0")/.."

ASSETS="src/Grove.App/Assets"
WORK="build/icon"

rm -rf "$WORK"
GROVE_ICON_DIR="$PWD/$WORK" dotnet test tests/Grove.App.Tests -v q --nologo --filter WriteIcon >/dev/null
echo "rendered $(ls "$WORK" | wc -l | tr -d ' ') sizes"

mkdir -p "$ASSETS"
cp "$WORK/icon-256.png" "$ASSETS/grove.png"

# Windows .ico. Every entry is a PNG payload, which Windows has read since Vista and which keeps
# the 256px entry from bloating the file the way a raw bitmap would.
python3 - "$WORK" "$ASSETS/grove.ico" <<'PY'
import pathlib, struct, sys

work, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [(s, (work / f"icon-{s}.png").read_bytes()) for s in sizes]

header = struct.pack("<HHH", 0, 1, len(images))
offset = len(header) + 16 * len(images)
entries, payloads = b"", b""
for size, data in images:
    # 0 means 256 in the directory entry: the field is one byte wide.
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
    payloads += data
    offset += len(data)

out.write_bytes(header + entries + payloads)
PY
echo "wrote $ASSETS/grove.ico"

# macOS .icns, when building on a Mac. iconutil is the only supported way to produce one, so on
# other platforms the committed .icns is left alone.
if command -v iconutil >/dev/null 2>&1; then
    ICONSET="$WORK/grove.iconset"
    mkdir -p "$ICONSET"
    cp "$WORK/icon-16.png"   "$ICONSET/icon_16x16.png"
    cp "$WORK/icon-32.png"   "$ICONSET/icon_16x16@2x.png"
    cp "$WORK/icon-32.png"   "$ICONSET/icon_32x32.png"
    cp "$WORK/icon-64.png"   "$ICONSET/icon_32x32@2x.png"
    cp "$WORK/icon-128.png"  "$ICONSET/icon_128x128.png"
    cp "$WORK/icon-256.png"  "$ICONSET/icon_128x128@2x.png"
    cp "$WORK/icon-256.png"  "$ICONSET/icon_256x256.png"
    cp "$WORK/icon-512.png"  "$ICONSET/icon_256x256@2x.png"
    cp "$WORK/icon-512.png"  "$ICONSET/icon_512x512.png"
    cp "$WORK/icon-1024.png" "$ICONSET/icon_512x512@2x.png"
    iconutil --convert icns --output "$ASSETS/grove.icns" "$ICONSET"
    echo "wrote $ASSETS/grove.icns"
else
    echo "iconutil not found: leaving $ASSETS/grove.icns as committed" >&2
fi
