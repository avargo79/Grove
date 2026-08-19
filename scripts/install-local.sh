#!/usr/bin/env bash
#
# Builds Grove and installs it for the current user only.
#
# Nothing here needs administrator rights, a package manager, or a system directory: the whole
# point is a machine where you may not have any of those. The build is self-contained, so the
# only thing the installed app needs from the system is `git` on PATH.
#
# Usage:  ./scripts/install-local.sh [--launch]
#
set -euo pipefail

cd "$(dirname "$0")/.."

LAUNCH=0
for arg in "$@"; do
  case "$arg" in
    --launch) LAUNCH=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

# --------------------------------------------------------------------- prerequisites

if ! command -v dotnet >/dev/null 2>&1; then
  cat >&2 <<'MISSING'
The .NET SDK is not on PATH.

It installs per-user, no administrator rights involved:

    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
    export PATH="$HOME/.dotnet:$PATH"

Then run this script again.
MISSING
  exit 1
fi

# Git is a runtime dependency, not a build one: Grove drives your own git binary rather than
# reimplementing it, which is what makes it honour your config, credential helpers and hooks.
if ! command -v git >/dev/null 2>&1; then
  echo "warning: git is not on PATH — Grove will build, but it cannot open a repository without it." >&2
fi

# The runtime identifier is resolved here and passed to the build, rather than read back from
# artifacts/ afterwards: that directory keeps every build ever made, so guessing from it installs
# whichever one happens to sort first.
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)   PLATFORM=mac;     RID=osx-arm64 ;;
  Darwin-x86_64)  PLATFORM=mac;     RID=osx-x64 ;;
  Linux-aarch64)  PLATFORM=linux;   RID=linux-arm64 ;;
  Linux-x86_64)   PLATFORM=linux;   RID=linux-x64 ;;
  MINGW*|MSYS*|CYGWIN*)
    PLATFORM=windows
    case "$(uname -m)" in
      aarch64|arm64) RID=win-arm64 ;;
      *)             RID=win-x64 ;;
    esac
    ;;
  *) echo "Unsupported platform: $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

# --------------------------------------------------------------------- build

./scripts/publish.sh "$RID"

BUILT="artifacts/$RID"

# --------------------------------------------------------------------- install

case "$PLATFORM" in
  mac)
    # ~/Applications, not /Applications: the user's own Applications folder needs no admin and
    # is indexed by Spotlight and Launchpad just the same. macOS creates it on demand.
    TARGET="$HOME/Applications/Grove.app"
    mkdir -p "$HOME/Applications"
    rm -rf "$TARGET"
    cp -R "$BUILT/Grove.app" "$TARGET"

    # A locally built app carries no quarantine flag — that comes from downloading — but a
    # checkout that arrived as a zip can, and the signature has to cover the copy that runs.
    xattr -cr "$TARGET"
    codesign --force --deep --sign - "$TARGET" >/dev/null 2>&1

    echo "Installed → $TARGET"
    echo "Open it from Launchpad, or:  open -a '$TARGET'"
    [ "$LAUNCH" = "1" ] && open -a "$TARGET"
    ;;

  linux)
    TARGET="$HOME/.local/share/grove"
    rm -rf "$TARGET"
    mkdir -p "$TARGET" "$HOME/.local/bin" "$HOME/.local/share/applications"
    cp -R "$BUILT/." "$TARGET/"
    chmod +x "$TARGET/Grove.App"
    ln -sf "$TARGET/Grove.App" "$HOME/.local/bin/grove"

    # A desktop entry so it appears in the launcher alongside everything else.
    cp src/Grove.App/Assets/grove.png "$TARGET/grove.png"
    cat > "$HOME/.local/share/applications/grove.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Grove
Comment=A visual Git client
Exec=$TARGET/Grove.App %f
Icon=$TARGET/grove.png
Categories=Development;RevisionControl;
Terminal=false
DESKTOP

    echo "Installed → $TARGET"
    echo "Run it with:  grove   (ensure ~/.local/bin is on PATH)"
    [ "$LAUNCH" = "1" ] && "$TARGET/Grove.App" &
    ;;

  windows)
    # %LOCALAPPDATA%\Programs is where per-user installers put things, so it is the path most
    # likely to already be allowed if execution is restricted by policy.
    TARGET="${LOCALAPPDATA:-$HOME/AppData/Local}/Programs/Grove"
    rm -rf "$TARGET"
    mkdir -p "$TARGET"
    cp -R "$BUILT/." "$TARGET/"

    echo "Installed → $TARGET"
    echo "Run it with:  '$TARGET/Grove.App.exe'"
    echo
    echo "If it will not start, execution policy is the likely cause rather than the build:"
    echo "AppLocker and WDAC commonly allow only C:\\Program Files and C:\\Windows, which would"
    echo "rule out any per-user directory. Check with:  Get-AppLockerPolicy -Effective -Xml"
    [ "$LAUNCH" = "1" ] && "$TARGET/Grove.App.exe" &
    ;;
esac
