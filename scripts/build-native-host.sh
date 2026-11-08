#!/usr/bin/env bash
# Builds the Windows player's executable, src/OniMoono.NativeHost (ARCHITECTURE §2), from WSL or
# from Git Bash on Windows (a GitHub Actions Windows runner): stages its sources, the icon and the
# .NET SDK's nethost in %TEMP% and runs its build.cmd there with the Visual Studio C++ build tools
# (VCVARS64, or what vswhere finds), then copies the exe to DEST, over the SDK's apphost.
#   scripts/build-native-host.sh DEST/OniMoono.exe
set -euo pipefail
source "$(dirname "$0")/env.sh"
dest="$1"
case "$(uname -s)" in MINGW*|MSYS*) native=1 ;; *) native= ;; esac
[ -n "$native" ] || command -v cmd.exe >/dev/null || { echo "build-native-host: needs Windows (WSL's cmd.exe, or Git Bash) and its Visual Studio C++ build tools" >&2; exit 3; }
# nethost from the SDK's Microsoft.NETCore.App.Host.win-x64 pack (restored by any win-x64 build).
version="$(dotnet msbuild "$root/src/OniMoono.Restarter" -getProperty:BundledNETCoreAppPackageVersion)"
pack=""
dotnet_root="${DOTNET_ROOT:-$HOME/.dotnet}"; nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
[ -z "$native" ] || { dotnet_root="$(cygpath -u "$dotnet_root")"; nuget="$(cygpath -u "$nuget")"; }
for dir in "$nuget/microsoft.netcore.app.host.win-x64/$version" \
           "$dotnet_root/packs/Microsoft.NETCore.App.Host.win-x64/$version"; do
    [ -f "$dir/runtimes/win-x64/native/libnethost.lib" ] && { pack="$dir/runtimes/win-x64/native"; break; }
done
[ -n "$pack" ] || { echo "build-native-host: no Microsoft.NETCore.App.Host.win-x64 $version pack (build the host for win-x64 first)" >&2; exit 1; }
if [ -n "$native" ]; then
    stage="$(cygpath -u "$TEMP")/oni-moono-nativehost-$$"
else
    win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
    stage="$(wslpath "$win_temp")/oni-moono-nativehost-$$"
fi
rm -rf "$stage"; mkdir -p "$stage"
trap 'rm -rf "$stage"' EXIT
cp "$root/src/OniMoono.NativeHost/"{host.c,crash.c,crash.h,crash_dialog.cpp,crash_dialog.h,host.rc,OniMoono.exe.manifest,crash_dialog.manifest,build.cmd} \
   "$root/src/OniMoono.NativeHost/fonts/"*.ttf "$root/src/OniMoono.Host/Moo.ico" \
   "$pack/"{nethost.h,hostfxr.h,libnethost.lib} "$stage/"
commit="$(git -C "$root" rev-parse HEAD 2>/dev/null || echo unknown)"
# (MSYS2_ARG_CONV_EXCL: Git Bash passes /c to cmd.exe as it is.)
(cd "$stage" && MSYS2_ARG_CONV_EXCL='*' cmd.exe /c build.cmd "$commit" 2>&1 | tr -d '\r') || true
[ -f "$stage/OniMoono.exe" ] || { echo "build-native-host: the build failed" >&2; exit 1; }
cp "$stage/OniMoono.exe" "$dest"
