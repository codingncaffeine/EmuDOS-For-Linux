#!/usr/bin/env bash
# Builds the two Linux release artifacts the in-app updater consumes:
#   EmuDOS-<ver>-linux-x64.tar.gz   self-contained, extract anywhere (portable mode)
#   emudos_<ver>_amd64.deb          system install (/usr/lib/emudos)
# Asset names are a CONTRACT with src/EmuDOS/Services/UpdateService.cs — change both
# together. README.txt (the bundled quick-start) ships in the tarball root and the
# deb's /usr/share/doc/emudos/.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' src/EmuDOS/EmuDOS.csproj)
OUT=packaging/out
PUB=$OUT/publish
rm -rf "$OUT" && mkdir -p "$PUB"

echo "── publish v$VER (self-contained linux-x64)"
dotnet publish src/EmuDOS/EmuDOS.csproj -c Release -r linux-x64 \
    --self-contained true -o "$PUB" -v q

# The MT-32 shim (libemudos_mt32.so) is built + copied by the csproj's
# BuildAndCopyMt32Shim target during publish; double-check it rode along.
# Rebuild the MT-32 shim against an OLD glibc so the .deb runs on mainstream Debian/Ubuntu, not just
# bleeding-edge distros. Building on the host (e.g. Arch glibc 2.43) makes log10f@GLIBC_2.43 a hard
# requirement that older glibc lacks. A debian:bullseye container (glibc 2.31) gives a broad baseline.
# Falls back to the host build if no container runtime is present (with a loud warning).
MT32_SO=src/native/mt32/libemudos_mt32.so
CONTAINER=$(command -v podman || command -v docker || true)
if [ -n "$CONTAINER" ]; then
    echo "── build MT-32 shim against old glibc ($CONTAINER, debian:bullseye)"
    "$CONTAINER" run --rm --network=host -v "$PWD/src/native/mt32":/mt32:Z debian:bullseye-slim \
        bash -c "apt-get update -qq >/dev/null && apt-get install -y -q g++ >/dev/null && cd /mt32 && rm -f libemudos_mt32.so && ./build.sh"
else
    echo "!! WARNING: no podman/docker — building MT-32 shim with the HOST g++."
    echo "!!          The .deb may then require this host's glibc; build on/with an old glibc for releases."
    ( cd src/native/mt32 && rm -f libemudos_mt32.so && ./build.sh )
fi
cp -f "$MT32_SO" "$PUB/"

# The librashader runtime (CRT shaders) is fetched + copied by the csproj's FetchAndCopyLibrashader
# target during publish; double-check it rode along so end users never install a package.
if [ ! -f "$PUB/librashader.so" ]; then
    echo "── fetch librashader runtime (was missing from publish)"
    ( cd src/native/librashader && ./fetch.sh )
    cp -f src/native/librashader/librashader.so "$PUB/"
fi

cp packaging/README.txt "$PUB/README.txt"
cp LICENSE "$PUB/LICENSE"
cp NOTICES.txt "$PUB/NOTICES.txt"   # LGPL/MPL attribution for bundled/linked components

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
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libxfixes3, libgl1, libegl1, libfontconfig1, libfreetype6, libpng16-16t64 | libpng16-16, libsdl3-0, libdbus-1-3, libudev1, zlib1g, libbz2-1.0, libbrotli1, libexpat1
Recommends: libvlc5, vlc-plugin-base, libpulse0, ffmpeg, xorriso
Maintainer: EmuDOS for Linux <stragee@gmail.com>
Description: A beautiful frontend for your classic DOS games
 Linux port of the EmuDOS frontend: a Boxer-style library manager for classic
 DOS gaming on the DOSBox Pure libretro core. Box art, save states, cheats,
 disc-image mounting, hardware 3dfx, CRT shaders and Roland MT-32 synthesis.
 .
 The librashader CRT-shader runtime ships bundled. Optionally install ffmpeg for
 recording, xorriso to build disc images, and libvlc for video previews — each
 feature degrades gracefully if absent.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/emudos_${VER}_amd64.deb" > /dev/null

rm -rf "$DEB"
echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
