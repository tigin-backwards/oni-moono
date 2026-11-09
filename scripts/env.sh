# Common environment for scripts (sourced).
# shellcheck shell=bash disable=SC2034  # root, game, managed: read by the scripts that source it
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
game="${ONI_GAME_DIR:-$HOME/projects/oni-linux}"
managed="$game/OxygenNotIncluded_Data/Managed"
