#!/usr/bin/env bash
# Fetches the prebuilt librashader runtime (CRT/slang shader engine) and drops it next to this
# script as librashader.so, so EmuDOS can bundle it — users never install a separate package.
#
# librashader (https://github.com/SnowflakePowered/librashader) ships NO Linux release binary, so
# we pull the version-pinned build from the Arch Linux Archive (durable; keeps old versions forever)
# and verify it by SHA-256. The library is dual-licensed MPL-2.0 / GPL-3.0 — both compatible with
# EmuDOS's GPLv3 — and is credited in NOTICES.txt / the README. Mirror the MT-32 shim pattern.
set -euo pipefail
cd "$(dirname "$0")"

VER=0.11.2-1
PKG="librashader-${VER}-x86_64.pkg.tar.zst"
PKG_SHA256=f0e18daa10f6dd8556b9a76011d91768d6225a0e9fc6c5369507a905d193fbff
SO_SHA256=67c59dbb48fbaac817b8649fb84b518bc9ed23e908bf5c866eb0f3a1145ac040
MEMBER=usr/lib/librashader.so.0.11.2

# Durable archive first, then a live geo mirror as a fallback (same file, same hash).
URLS=(
  "https://archive.archlinux.org/packages/l/librashader/${PKG}"
  "https://geo.mirror.pkgbuild.com/extra/os/x86_64/${PKG}"
)

if [ -f librashader.so ] && echo "${SO_SHA256}  librashader.so" | sha256sum -c --status -; then
    echo "librashader.so already present and verified."
    exit 0
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

ok=0
for url in "${URLS[@]}"; do
    echo "── fetching $url"
    if curl -fsSL -o "$tmp/$PKG" "$url"; then ok=1; break; fi
    echo "   (failed, trying next mirror)"
done
[ "$ok" = 1 ] || { echo "ERROR: could not download $PKG from any mirror." >&2; exit 1; }

echo "${PKG_SHA256}  $tmp/$PKG" | sha256sum -c --status - \
    || { echo "ERROR: package SHA-256 mismatch — refusing to use it." >&2; exit 1; }

tar --use-compress-program=unzstd -xf "$tmp/$PKG" -C "$tmp" "$MEMBER"
cp -f "$tmp/$MEMBER" librashader.so

echo "${SO_SHA256}  librashader.so" | sha256sum -c --status - \
    || { echo "ERROR: extracted librashader.so SHA-256 mismatch." >&2; rm -f librashader.so; exit 1; }

echo "librashader.so ready ($(du -h librashader.so | cut -f1))."
