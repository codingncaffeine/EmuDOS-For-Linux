#!/usr/bin/env bash
# Builds the SDL3 runtime EmuDOS ships next to its binary (libSDL3.so.0) from the pinned upstream
# release. EmuDOS uses SDL3 for audio output and gamepads only, so the build keeps the audio backends
# (PulseAudio and ALSA, loaded at run time, so the library links against little beyond libc) and the
# joystick/gamepad/HIDAPI support with udev hotplug, and leaves out every video backend. Debian and
# Ubuntu LTS have no libsdl3-0 (or only the runtime without the libSDL3.so name), so shipping it is
# what makes game audio and gamepads work there. packaging/build-release.sh runs this inside an
# old-glibc container so the result loads on mainstream distros. SDL is zlib-licensed (NOTICES.txt).
set -euo pipefail
cd "$(dirname "$0")"

VER=3.4.16
SHA256=7322236cd12090c3eb40b9728be4d49c76f66ad17d04369584d4ecad5cf77c68
TGZ="SDL3-$VER.tar.gz"

[ -f "$TGZ" ] || curl -fsSL -o "$TGZ" "https://github.com/libsdl-org/SDL/releases/download/release-$VER/$TGZ"
echo "$SHA256  $TGZ" | sha256sum -c -

rm -rf build "SDL3-$VER" libSDL3.so.0
tar -xzf "$TGZ"
cmake -S "SDL3-$VER" -B build -DCMAKE_BUILD_TYPE=Release \
    -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF \
    -DSDL_INSTALL=OFF -DSDL_INSTALL_DOCS=OFF \
    -DSDL_PULSEAUDIO=ON -DSDL_PULSEAUDIO_SHARED=ON -DSDL_ALSA=ON -DSDL_ALSA_SHARED=ON \
    -DSDL_PIPEWIRE=OFF -DSDL_JACK=OFF -DSDL_SNDIO=OFF -DSDL_OSS=OFF \
    -DSDL_X11=OFF -DSDL_WAYLAND=OFF -DSDL_KMSDRM=OFF -DSDL_OPENGL=OFF -DSDL_OPENGLES=OFF \
    -DSDL_VULKAN=OFF -DSDL_RENDER_GPU=OFF -DSDL_RENDER_VULKAN=OFF -DSDL_OPENVR=OFF \
    -DSDL_RPI=OFF -DSDL_ROCKCHIP=OFF -DSDL_VIVANTE=OFF \
    -DSDL_HIDAPI=ON -DSDL_HIDAPI_JOYSTICK=ON -DSDL_HIDAPI_LIBUSB=OFF -DSDL_LIBUDEV=ON \
    -DSDL_IBUS=OFF -DSDL_FRIBIDI=OFF -DSDL_LIBTHAI=OFF -DSDL_LIBURING=OFF \
    -DSDL_UNIX_CONSOLE_BUILD=ON # no windows: EmuDOS draws with Avalonia, SDL3 only plays audio + reads pads
cmake --build build -j"$(nproc)"
cp -L build/libSDL3.so.0 libSDL3.so.0
rm -rf build "SDL3-$VER"
echo "Built $(pwd)/libSDL3.so.0"
