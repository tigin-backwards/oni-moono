#!/usr/bin/env bash
# Builds the Linux player's executable, src/OniMoono.NativeHost (ARCHITECTURE §2, "The Linux
# executable"), in the Steam Runtime's sniper SDK container (Docker, the image pinned by its
# digest): glibc 2.31 and gcc 10, so it runs on every system the game does. Then checks the ELF
# file (scripts/check-linux-host.py) and copies it to DEST, over the SDK's apphost. With CC set,
# or no Docker, it builds with that compiler (cc) instead (setup.sh).
#   scripts/build-native-host-linux.sh DEST/OniMoono
set -euo pipefail
source "$(dirname "$0")/env.sh"
dest="$1"
image="registry.gitlab.steamos.cloud/steamrt/sniper/sdk@sha256:bb30053d3cd19d35c76ae69b2790dbaa1430448006f8240cb6d7e723f2d1940e"
src="$root/src/OniMoono.NativeHost"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp "$src/"*.c "$src/"*.h "$src/fonts_linux.S" "$src/exports_linux.list" "$src/build-linux.sh" "$src/fonts/"*.ttf \
   "$root/src/OniMoono.Host/Moo.ico" "$stage/"
if [ -z "${CC:-}" ] && command -v docker > /dev/null; then
    docker run --rm --network none --user "$(id -u):$(id -g)" -v "$stage:/build" -w /build -e SDL_CHECK=1 "$image" sh ./build-linux.sh
else
    (cd "$stage" && sh ./build-linux.sh)
fi
python3 -I "$root/scripts/check-linux-host.py" "$stage/OniMoono"
mkdir -p "$(dirname "$dest")"
cp "$stage/OniMoono" "$dest"
