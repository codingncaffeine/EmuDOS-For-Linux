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
if [ ! -f "$PUB/libemudos_mt32.so" ]; then
    echo "── build MT-32 shim (was missing from publish)"
    ( cd src/native/mt32 && ./build.sh )
    cp -f src/native/mt32/libemudos_mt32.so "$PUB/"
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
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72, libx11-6, libfontconfig1, libegl1, libgl1, libsdl3-0, libpng16-16t64 | libpng16-16
Recommends: ffmpeg, xorriso, libvlc5, vlc-plugin-base
Suggests: librashader
Maintainer: EmuDOS for Linux <stragee@gmail.com>
Description: A beautiful frontend for your classic DOS games
 Linux port of the EmuDOS frontend: a Boxer-style library manager for classic
 DOS gaming on the DOSBox Pure libretro core. Box art, save states, cheats,
 disc-image mounting, hardware 3dfx, CRT shaders and Roland MT-32 synthesis.
 .
 Install librashader for CRT shaders, ffmpeg for recording, xorriso to build
 disc images, and libvlc for video previews — each degrades gracefully if absent.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/emudos_${VER}_amd64.deb" > /dev/null

rm -rf "$DEB"
echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
