#!/usr/bin/env bash
# Prints the declared API of Unity types from the game's original Unity assemblies.
root="$(cd "$(dirname "$0")/.." && pwd)"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
game="${ONI_GAME_DIR:-$HOME/projects/oni-linux-761211}"
exec dotnet "$root/out/bin/UnityApi/release/UnityApi.dll" "$game/OxygenNotIncluded_Data/Managed" "$@"
