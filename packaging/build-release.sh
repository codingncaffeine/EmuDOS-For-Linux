#!/usr/bin/env bash
# Builds the Linux release artifacts:
#   EmuDOS-<ver>-linux-x64.tar.gz   self-contained, extract anywhere
#   emudos_<ver>_amd64.deb          system install (/usr/lib/emudos)
#   catalog.db                      the curated game catalog (the app fetches it from the latest release)
# Asset names are a CONTRACT with src/EmuDOS/Services/UpdateService.cs and
# src/EmuDOS.Core/Downloads/AssetManifest.cs — change them together. README.txt (the bundled
# quick-start) ships in the tarball root and the deb's /usr/share/doc/emudos/.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' src/EmuDOS/EmuDOS.csproj)
OUT=packaging/out
PUB=$OUT/publish

# A gitignored Secrets.cs still holding the template builds green and ships dead ScreenScraper
# scraping and cloud-save login. Refuse to package it.
SECRETS=src/EmuDOS.Metadata/Secrets.cs
if [ ! -f "$SECRETS" ] || grep -qE '"YOUR_[A-Z_]+"|= "";' "$SECRETS"; then
    if [ "${EMUDOS_ALLOW_TEMPLATE_SECRETS:-}" != 1 ]; then
        echo "!! $SECRETS is missing or still the template: refusing to build release artifacts." >&2
        exit 3
    fi
    # CI packages every push to test it; those builds are never released.
    echo "!! WARNING: building with TEMPLATE credentials (EMUDOS_ALLOW_TEMPLATE_SECRETS=1) — NOT FOR RELEASE." >&2
    [ -f "$SECRETS" ] || cp src/EmuDOS.Metadata/Secrets.cs.template "$SECRETS"
fi

rm -rf "$OUT" && mkdir -p "$PUB"

# Stale obj/ output can publish XAML that was never compiled; start every release from clean trees.
rm -rf src/EmuDOS/bin src/EmuDOS/obj src/EmuDOS.Core/bin src/EmuDOS.Core/obj \
       src/EmuDOS.Metadata/bin src/EmuDOS.Metadata/obj

echo "── publish v$VER (self-contained linux-x64)"
dotnet publish src/EmuDOS/EmuDOS.csproj -c Release -r linux-x64 \
    --self-contained true -o "$PUB" -v q

# Native pieces built against an OLD glibc so the artifacts run on mainstream Debian/Ubuntu, not
# just bleeding-edge distros (building on the host, e.g. Arch glibc 2.43, makes symbols like
# log10f@GLIBC_2.43 a hard requirement). Ubuntu 20.04 (glibc 2.31) is the baseline: Debian 11's
# security pool is gone since its LTS ended, which breaks package installs in that image.
#   libemudos_mt32.so  the MT-32 synth shim
#   libSDL3.so.0       SDL3 for game audio + gamepads (Debian/Ubuntu LTS have no usable package)
#   librashader.so     the CRT shader runtime, built from source (Rust; the prebuilt one needs glibc 2.38)
BUILD_IMAGE=docker.io/library/ubuntu:20.04
CONTAINER=$(command -v podman || command -v docker || true)
if [ -z "$CONTAINER" ]; then
    echo "!! no podman/docker: cannot build the native libraries against an old glibc." >&2
    exit 4
fi
echo "── build MT-32 shim + SDL3 + librashader against old glibc ($CONTAINER, $BUILD_IMAGE)"
"$CONTAINER" run --rm --network=host -e DEBIAN_FRONTEND=noninteractive \
    -v "$PWD/src/native/mt32":/mt32:Z -v "$PWD/src/native/sdl3":/sdl3:Z \
    -v "$PWD/src/native/librashader":/lr:Z "$BUILD_IMAGE" \
    bash -c "set -e
        apt-get update -qq >/dev/null
        apt-get install -y -q --no-install-recommends g++ gcc make cmake ninja-build pkg-config python3 git \
            ca-certificates curl libpulse-dev libasound2-dev libudev-dev libdbus-1-dev >/dev/null
        cd /mt32 && rm -f libemudos_mt32.so && ./build.sh
        cd /sdl3
        if ! ./build.sh > /tmp/sdl3-build.log 2>&1; then tail -40 /tmp/sdl3-build.log; exit 1; fi
        cd /lr
        if ! ./build.sh > /tmp/librashader-build.log 2>&1; then tail -40 /tmp/librashader-build.log; exit 1; fi"
cp -f src/native/mt32/libemudos_mt32.so "$PUB/"
cp -f src/native/sdl3/libSDL3.so.0 "$PUB/"
# The CRT shader runtime built from source replaces the fetched Arch binary, which needs glibc 2.38.
cp -f src/native/librashader/librashader.portable.so "$PUB/librashader.so"

cp packaging/README.txt "$PUB/README.txt"
cp LICENSE "$PUB/LICENSE"
cp NOTICES.txt "$PUB/NOTICES.txt"   # attribution for bundled/linked components

echo "── tarball"
tar -C "$PUB" -czf "$OUT/EmuDOS-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/emudos" "$DEB/usr/bin" \
         "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/512x512/apps" \
         "$DEB/usr/share/doc/emudos" "$DEB/usr/share/metainfo"
cp -a "$PUB/." "$DEB/usr/lib/emudos/"
rm -f "$DEB/usr/lib/emudos/README.txt"
cp packaging/README.txt "$DEB/usr/share/doc/emudos/README.txt"
cp LICENSE "$DEB/usr/share/doc/emudos/copyright"
cp NOTICES.txt "$DEB/usr/share/doc/emudos/NOTICES.txt"
cp packaging/io.github.codingncaffeine.EmuDOS.metainfo.xml "$DEB/usr/share/metainfo/"
cat > "$DEB/usr/bin/emudos" <<'WRAP'
#!/bin/sh
exec /usr/lib/emudos/EmuDOS "$@"
WRAP
chmod 755 "$DEB/usr/bin/emudos"
cp "src/EmuDOS/Assets/emudos-linux.png" \
   "$DEB/usr/share/icons/hicolor/512x512/apps/emudos.png"
cat > "$DEB/usr/share/applications/io.github.codingncaffeine.EmuDOS.desktop" <<DESK
[Desktop Entry]
Name=EmuDOS
Comment=A beautiful frontend for your classic DOS games
Exec=emudos
Icon=emudos
Terminal=false
Type=Application
Categories=Game;Emulator;
DESK
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: emudos
Version: $VER
Section: games
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libxfixes3, libgl1, libegl1, libfontconfig1, libfreetype6, libpng16-16t64 | libpng16-16, libdbus-1-3, libudev1, zlib1g, libbz2-1.0, libbrotli1, libexpat1
Recommends: libpulse0, libasound2t64 | libasound2, libvlc5, vlc-plugin-base, ffmpeg, xorriso
Maintainer: EmuDOS for Linux <codingncaffeine@users.noreply.github.com>
Description: A beautiful frontend for your classic DOS games
 Linux port of the EmuDOS frontend: a Boxer-style library manager for classic
 DOS gaming on the DOSBox Pure libretro core. Box art, save states, cheats,
 disc-image mounting, hardware 3dfx, CRT shaders and Roland MT-32 synthesis.
 .
 SDL3 (game audio and gamepads) and the librashader CRT-shader runtime ship
 bundled. Optionally install ffmpeg for recording, xorriso to build disc images,
 and libvlc for video previews — each feature degrades gracefully if absent.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/emudos_${VER}_amd64.deb" > /dev/null

rm -rf "$DEB"

echo "── catalog"
cp src/EmuDOS.Core/Catalog/catalog.db "$OUT/catalog.db"

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
