#!/usr/bin/env bash
# One-time setup for playing Oxygen Not Included on Moono (Linux).
#
# Finds the game through Steam's libraries, builds the player into out/player when run from a
# clone of the repository (a release has it in player/ beside this script), and prints the line
# to paste into the game's Steam launch options unless every Steam account here already has it
# (read from Steam's localconfig.vdf, never written). Run it again after updating.
# It offers to add Moono to the applications menu (docs/INSTALL-MODES.md, Linux launcher):
# --menu adds it without asking, --no-menu doesn't ask, --remove-menu removes it and does
# nothing else.
#   ./setup.sh [--menu | --no-menu | --remove-menu] [GAME_DIR]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

say() { printf '%s\n' "$*"; }
die() { printf 'setup: %s\n' "$*" >&2; exit 1; }

# The game: the argument, or the first Steam library holding it. Steam lives in one of these
# depending on how it was installed (package, Valve's installer, snap, Flatpak).
find_game()
{
    local steam lib
    for steam in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam" \
                 "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
        [ -f "$steam/steamapps/libraryfolders.vdf" ] || continue
        while IFS= read -r lib; do
            [ -d "$lib/steamapps/common/OxygenNotIncluded/OxygenNotIncluded_Data" ] && { echo "$lib/steamapps/common/OxygenNotIncluded"; return; }
        done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$steam/steamapps/libraryfolders.vdf")
    done
}

# The applications menu's entry (XDG): its own name, apart from the versioned install's
# oni-moono.desktop (docs/INSTALL-MODES.md), and the icon it shows.
data="${XDG_DATA_HOME:-$HOME/.local/share}"
menu_entry="$data/applications/oni-moono-player.desktop"
menu_icon="$data/icons/hicolor/96x96/apps/oni-moono.png"
menu=ask; given=""
for arg in "$@"; do
    case "$arg" in
        --menu) menu=yes ;;
        --no-menu) menu=no ;;
        --remove-menu)
            rm -f "$menu_entry" "$menu_icon"
            say "Moono is no longer in the applications menu."
            exit 0 ;;
        -*) die "unknown option $arg (./setup.sh [--menu | --no-menu | --remove-menu] [GAME_DIR])" ;;
        *) given=$arg ;;
    esac
done

say "Moono: Oxygen Not Included on its own engine (unofficial). Setup."
game="${given:-$(find_game)}"
[ -n "$game" ] || die "Oxygen Not Included wasn't found in any Steam library; install it, or pass its directory: ./setup.sh /path/to/OxygenNotIncluded"
[ -d "$game/OxygenNotIncluded_Data/Managed" ] || die "$game isn't an Oxygen Not Included install (no OxygenNotIncluded_Data/Managed)"
[ -x "$game/OxygenNotIncluded" ] || die "$game is the Windows build; on Linux, Steam has to install the Linux one"
say "Game: $game"

if [ -x "$here/player/OniMoono" ]; then
    player="$here/player" # a release
    # Extracted over a release from before the Moono name, player/ still holds that player
    # (OniBrutal and its OniBrutal.* assemblies), which this one doesn't use.
    rm -f "$player/OniBrutal" "$player"/OniBrutal.*
else
    # A .NET SDK of the version global.json pins (a later patch release works too): one already
    # installed, or one installed into ~/.dotnet (no root needed).
    want=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9]*\.[0-9]*\.[0-9]\)[0-9]*".*/\1/p' "$here/global.json")
    dotnet=""
    for candidate in "$HOME/.dotnet/dotnet" "$(command -v dotnet || true)"; do
        [ -n "$candidate" ] && [ -x "$candidate" ] && "$candidate" --list-sdks 2>/dev/null | grep -q "^$want" && { dotnet=$candidate; break; }
    done
    if [ -z "$dotnet" ]; then
        say "Installing the .NET SDK ${want}xx into ~/.dotnet..."
        installer=$(mktemp)
        curl -sSfL https://dot.net/v1/dotnet-install.sh -o "$installer" || die "couldn't download the .NET installer"
        bash "$installer" --version "$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$here/global.json")" --install-dir "$HOME/.dotnet" >/dev/null
        rm -f "$installer"
        dotnet="$HOME/.dotnet/dotnet"
    fi
    DOTNET_ROOT="$(dirname "$dotnet")"
    export DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

    say "Building the player (a few minutes the first time)..."
    # Self-contained: it carries its own .NET runtime, so it runs where Steam starts it (a snap's
    # Steam can't see ~/.dotnet).
    # The output is shown only if the build fails.
    if ! output=$("$dotnet" build "$here/src/OniMoono.Host" -c Release -r linux-x64 --self-contained true -p:OniGameDir="$game" -v q -nologo 2>&1); then
        printf '%s\n' "$output" >&2
        die "the build failed"
    fi
    player="$here/out/player"
    rm -rf "$player"
    cp -r "$here/out/bin/OniMoono.Host/release_linux-x64" "$player"
    rm -f "$player"/FacadeGen*
fi

# Steam in a sandbox runs the game where only some of the home folder can be read: a Flatpak
# Steam sees its own ~/.var/app folder, a snap Steam no hidden folders. The player is installed
# into the sandbox's own folder then (ONI_STEAM_SANDBOX=flatpak|snap|none overrides the guess
# made from where the game is installed).
sandbox="${ONI_STEAM_SANDBOX:-}"
if [ -z "$sandbox" ]; then
    case "$game" in
        "$HOME/.var/app/com.valvesoftware.Steam/"*) sandbox=flatpak ;;
        "$HOME/snap/steam/"*) sandbox=snap ;;
        *) sandbox=none ;;
    esac
fi
case "$sandbox" in
    flatpak) inside="$HOME/.var/app/com.valvesoftware.Steam/data/oni-moono/player" ;;
    snap) inside="$HOME/snap/steam/common/oni-moono/player" ;;
    *) inside="" ;;
esac
if [ -n "$inside" ]; then
    rm -rf "$inside"; mkdir -p "$(dirname "$inside")"
    cp -r "$player" "$inside"
    player="$inside"
    say "Steam runs as a $sandbox here: the player is installed where it can see it ($player)."
fi

launch="\"$player/OniMoono\" -- %command%"

# desktop_arg STRING: STRING as one argument of a desktop entry's Exec, as the specification
# quotes it: in double quotes with \ " ` $ escaped, then the key file's own \ escaped, % doubled.
desktop_arg()
{
    local s=$1
    s=${s//\\/\\\\}; s=${s//\"/\\\"}; s=${s//\`/\\\`}; s=${s//\$/\\\$}
    s=${s//\\/\\\\}; s=${s//%/%%}
    printf '"%s"' "$s"
}

# The applications menu: asked once on a terminal; an entry made before is kept up to date.
if [ "$menu" = ask ]; then
    if [ -f "$menu_entry" ]; then menu=yes
    elif [ -t 0 ]; then
        read -r -p "Add Moono to the applications menu? [Y/n] " answer || answer=n
        case "$answer" in [nN]*) menu=no ;; *) menu=yes ;; esac
    else menu=no
    fi
fi
if [ "$menu" = yes ]; then
    mkdir -p "$(dirname "$menu_entry")" "$(dirname "$menu_icon")"
    for icon in "$here/player/Moo.png" "$here/src/OniMoono.Desktop/Moo.png"; do
        [ -f "$icon" ] && { cp "$icon" "$menu_icon"; break; }
    done
    printf '%s\n' "[Desktop Entry]" "Type=Application" "Name=Oxygen Not Included (Moono)" "Comment=Oxygen Not Included on Moono" \
        "Exec=$(desktop_arg "$player/OniMoono")" "Icon=$menu_icon" "Terminal=false" "Categories=Game;" \
        "StartupWMClass=OxygenNotIncluded" > "$menu_entry"
    say "In the applications menu: Oxygen Not Included (Moono). To remove it: ./setup.sh --remove-menu"
fi

# What each Steam account here has as the game's launch options, read (never written) from its
# localconfig.vdf: Steam keeps them under Software/Valve/Steam/apps/457140, and the account's
# name under friends/PersonaName. Prints "persona<TAB>options" per account.
read_launch_options()
{
    awk '
    BEGIN { RS = "\0" }
    {
        n = 0; depth = 0; key = ""; haskey = 0; persona = ""; options = ""; i = 1; len = length($0)
        while (i <= len) {
            c = substr($0, i, 1)
            if (c == "{") { path[++depth] = tolower(key); haskey = 0; i++; continue }
            if (c == "}") { if (depth > 0) depth--; haskey = 0; i++; continue }
            if (c != "\"") { i++; continue }
            v = ""; i++
            while (i <= len) {
                c = substr($0, i, 1)
                if (c == "\\") { v = v substr($0, i + 1, 1); i += 2; continue }
                if (c == "\"") { i++; break }
                v = v c; i++
            }
            if (!haskey) { key = v; haskey = 1; continue }
            where = ""
            for (d = 1; d <= depth; d++) where = where (d > 1 ? "/" : "") path[d]
            if (where == "userlocalconfigstore/friends" && key == "PersonaName") persona = v
            if (where == "userlocalconfigstore/software/valve/steam/apps/457140" && key == "LaunchOptions") options = v
            haskey = 0
        }
        printf "%s\t%s\n", persona, options
    }' "$1"
}

accounts=""
for steam in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    for config in "$steam"/userdata/*/config/localconfig.vdf; do
        [ -f "$config" ] || continue
        accounts+="$(read_launch_options "$config")"$'\n'
    done
done
# One Steam install can be reached through several of those paths (~/.steam/steam is a link).
accounts=$(printf '%s' "$accounts" | sort -u)
# Set: the options start this player for the game (other options of the user's may be there too).
is_set() { [[ "$1" == *"\"$player/OniMoono\""* && "$1" == *"%command%"* ]]; }
# Old: the options start the player from before the Moono name (oni-brutal's OniBrutal).
old_player() { [[ "$1" == *"/OniBrutal\""* && "$1" == *"%command%"* ]]; }
found=0; unset_count=0
while IFS=$'\t' read -r persona options; do
    [ -n "$persona$options" ] || continue
    found=1
    is_set "$options" || unset_count=$((unset_count + 1))
done <<< "$accounts"

say ""
while IFS=$'\t' read -r persona options; do
    [ -n "$persona$options" ] || continue
    if is_set "$options"; then
        say "Steam already starts Oxygen Not Included on Moono for $persona."
        # Options from before the Moono Tweaks screen: they still work, and win over the screen.
        if [[ "$options" == *"--game-tweak"* || "$options" == *"--splash"* || "$options" == *"--frame-stats"* ]]; then
            say "  Its launch options choose the tweaks, so the game's Moono Tweaks screen can't change those."
            say "  To choose them in the game instead, set the launch options to: $launch"
        fi
    elif old_player "$options"; then say "$persona: the launch options point at the old oni-brutal install; paste the new line."
    elif [ -n "$options" ]; then say "$persona: the launch options are set to something else: $options"
    else say "$persona: no launch options yet."
    fi
done <<< "$accounts"
if [ $found -eq 1 ] && [ $unset_count -eq 0 ]; then
    say ""
    say "Done. Nothing else to do: press Play in Steam. The optional tweaks are on the main menu: Moono Tweaks."
    say "To play the original game again, clear the launch options (right-click Oxygen Not Included → Properties)."
    exit 0
fi

# On the clipboard where a clipboard tool exists: copied from a terminal, a long line can pick
# up its wrapping.
copied=""
for tool in "wl-copy" "xclip -selection clipboard" "xsel --clipboard --input"; do
    command -v "${tool%% *}" >/dev/null 2>&1 && printf '%s' "$launch" | $tool 2>/dev/null && { copied=1; break; }
done
[ $found -eq 1 ] && say ""
if [ -n "$copied" ]; then
    say "Done. The launch options are on your clipboard. In Steam: right-click Oxygen Not Included →"
    say "Properties → Launch Options, and paste them. They are:"
else
    say "Done. In Steam: right-click Oxygen Not Included → Properties → Launch Options, and paste:"
fi
say ""
say "    $launch"
say ""
say "Then press Play as usual. The optional tweaks are on the main menu: Moono Tweaks."
say "To play the original game again, clear the launch options."
