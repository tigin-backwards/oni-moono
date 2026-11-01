# Common environment for scripts (sourced).
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
game="${ONI_GAME_DIR:-$HOME/projects/oni-linux-761211}"
managed="$game/OxygenNotIncluded_Data/Managed"
