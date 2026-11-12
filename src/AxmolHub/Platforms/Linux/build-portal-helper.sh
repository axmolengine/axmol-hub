#!/bin/sh
# Build a small dynamically linked client; never bundle GIO, PipeWire or desktop portal services.
set -eu
out=$1
rid=${2:-}
case "$rid" in
    ''|linux-x64|linux-arm64) ;;
    *) echo "The portal helper is Linux-only." >&2; exit 1 ;;
esac
case "$(uname -m):$rid" in
    x86_64:linux-arm64|aarch64:linux-x64)
        echo "Build the portal helper on the target architecture's native runner." >&2; exit 1 ;;
esac
mkdir -p "$(dirname "$out")"
# HUB_PORTAL_CFLAGS is a development-only override for headers extracted under the workspace.
# Release runners use the system's pkg-config development packages.
if [ -n "${HUB_PORTAL_CFLAGS:-}" ]; then
    flags="$(pkg-config --cflags --libs gio-unix-2.0) $HUB_PORTAL_CFLAGS -Wl,-l:libpipewire-0.3.so.0"
else
    flags=$(pkg-config --cflags --libs gio-unix-2.0 libpipewire-0.3) || {
        echo "Install gcc, pkg-config, libglib2.0-dev, libpipewire-0.3-dev and libspa-0.2-dev to build Wayland support." >&2
        exit 1
    }
fi
# Intentional word splitting: pkg-config emits compiler arguments, never shell commands.
${CC:-cc} -std=gnu11 -O2 -Wall -Wextra -Werror "$(dirname "$0")/portal-helper.c" $flags -pthread -o "$out"
