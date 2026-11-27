#!/bin/sh
# Builds OniMoono, the Linux player's executable (host_linux.c, crash_linux.c, crash_common.c,
# crash_window.c, crash_window_linux.c), in the current folder, which holds the
# sources, the crash window's fonts (fonts/*.ttf, copied flat) and Moo.ico
# (scripts/build-native-host-linux.sh stages them). C only: no C++ runtime to ship.
#   build-linux.sh
# CC: the C compiler (cc). SDL_CHECK=1: sdl_min.h checked against SDL's own headers too (the
# Steam Runtime SDK has them).
# SC2086: $cc and $flags are word lists on purpose.
# shellcheck disable=SC2086
set -eu
cc="${CC:-cc}"
flags="-std=gnu11 -O2 -g0 -fPIE -fstack-protector-strong -D_FORTIFY_SOURCE=2 -fvisibility=hidden -Wall -Wextra -Werror -Wno-missing-field-initializers -Wno-format-truncation"
if [ "${SDL_CHECK:-}" = 1 ]; then $cc $flags -fsyntax-only sdl_min_check.c; fi
$cc $flags -pie -o OniMoono host_linux.c crash_linux.c crash_common.c crash_window.c crash_window_linux.c fonts_linux.S \
    -Wl,--dynamic-list=exports_linux.list -Wl,-z,relro,-z,now,-z,noexecstack -Wl,--build-id=sha1 -Wl,--as-needed \
    -static-libgcc -ldl -lpthread -lm
