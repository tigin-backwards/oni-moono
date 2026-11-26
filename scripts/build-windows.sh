#!/usr/bin/env bash
# Builds the Windows player into out/win (framework-dependent, win-x64): the host (OniMoono.exe,
# our native host: scripts/build-native-host.sh, which needs the Visual Studio C++ build tools)
# with the engine facades and the GLFW/OpenGL natives (vma.dll and shaderc_shared.dll built from
# source with the static C runtime: scripts/build-native-libs.sh), the Restarter.exe the game's App.Restart
# starts, and the offline Steam test double (steam_api64.dll, test runs only).
#   scripts/build-windows.sh [debug|release]
set -euo pipefail
source "$(dirname "$0")/env.sh"
config="${1:-release}"
dotnet build "$root/src/OniMoono.Host" -c "$config" -r win-x64 --self-contained false -v q -nologo
dotnet build "$root/src/OniMoono.Restarter" -c "$config" -r win-x64 --self-contained false -v q -nologo
out="$root/out/win"
rm -rf "$out"; mkdir -p "$out/host" "$out/steamstub"
cp -r "$root/out/bin/OniMoono.Host/${config}_win-x64/." "$out/host/"
# The host from before it was named OniMoono (OniBrutal.exe), should the build folder still hold it.
rm -f "$out/host/Restarter" "$out/host/FacadeGen."* "$out/host/OniBrutal."{exe,dll,pdb,deps.json,runtimeconfig.json}
cp "$root/out/bin/OniMoono.Restarter/${config}_win-x64/Restarter."{exe,dll,runtimeconfig.json} "$out/host/"
# OniMoono.exe: our native host (src/OniMoono.NativeHost) in place of the SDK apphost, built
# with MSVC on the Windows side; it exports the GPU hints hybrid-graphics drivers read.
"$root/scripts/build-native-host.sh" "$out/host/OniMoono.exe"
check=$(python3 -I "$root/scripts/check-windows-host.py" "$out/host/OniMoono.exe") || { echo "$check"; exit 1; }
# vma.dll and shaderc_shared.dll with the static C runtime, in place of the packages' (#126).
"$root/scripts/build-native-libs.sh" "$out/host"
python3 -I "$root/scripts/check-windows-natives.py" "$out/host"
python3 -I "$root/tools/SteamStub/gen.py" "$root/src/ThirdParty/Steamworks.NET/Runtime/autogen/NativeMethods.cs" "$out/steamstub/steam_api64.dll"
echo "$out"
