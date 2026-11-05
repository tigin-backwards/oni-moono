#!/usr/bin/env bash
# Regenerates the Unity API shapes (enums, interfaces, delegates, type headers, abstract members)
# compiled into OniMoono.UnityEngine, and the module map used by FacadeGen.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
game="${ONI_GAME_DIR:-$HOME/projects/oni-linux-762107}"
managed="$game/OxygenNotIncluded_Data/Managed"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build "$root/tools/SurfaceGen" -c Release -v q
dotnet build "$root/tools/FacadeGen" -c Release -v q
dotnet "$root/out/bin/SurfaceGen/release/SurfaceGen.dll" "$managed" "$root/out/surface" "$root/src/OniMoono.UnityEngine/Generated" "$root/src/OniMoono.UnityEngine/engine-required-types.tsv"
dotnet "$root/out/bin/FacadeGen/release/FacadeGen.dll" map "$managed" "$root/src/Facades/unity-module-map.tsv"
