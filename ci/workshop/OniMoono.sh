#!/bin/sh
# Moono's Workshop item, on Linux: run once with `sh OniMoono.sh` (docs/INSTALL-MODES.md). It
# copies this item's player into Moono's own install folder and starts the copy, which installs
# the version for your game, the entry point and a menu entry; this folder itself never runs, so
# Steam can always update it. Later starts go through the installed entry point.
set -u
src="$(cd "$(dirname "$0")" && pwd)"
# Steam in a sandbox sees only its own folder: install where it can start the player (as setup.sh).
case "$src" in
    "$HOME/.var/app/com.valvesoftware.Steam/"*) default="$HOME/.var/app/com.valvesoftware.Steam/data/oni-moono/versioned" ;;
    "$HOME/snap/steam/"*) default="$HOME/snap/steam/common/oni-moono/versioned" ;;
    *) default="${XDG_DATA_HOME:-$HOME/.local/share}/oni-moono/versioned" ;;
esac
root="${ONI_MOONO_INSTALL_ROOT:-$default}"
staging="$root/player/.bootstrap-$$"
mkdir -p "$root/player" || exit 1
rm -rf "$staging"
cp -R "$src/linux-x64/player" "$staging" || { echo "Moono: the player could not be copied to $staging" >&2; exit 1; }
chmod +x "$staging/OniMoono" "$staging/Restarter" 2>/dev/null
ONI_MOONO_INSTALL_ROOT="$root" ONI_MOONO_INSTALL_SOURCE="$src" "$staging/OniMoono" "$@"
status=$?
if [ "$status" -eq 77 ] && [ -f "$root/state/switch" ]; then
    rm -f "$root/state/switch"
    exec "$root/OniMoono" "$@"
fi
exit "$status"
