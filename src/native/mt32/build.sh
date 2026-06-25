#!/usr/bin/env bash
# Builds the EmuDOS MT-32 shim (libemudos_mt32.so) from emudos_mt32.cpp + the header-only munt
# synth (mt32emu.h, LGPL 2.1). Requires a C++17 compiler (g++ or clang++). Output: libemudos_mt32.so
# in this folder, which the app ships in the application directory next to the managed binaries.
set -euo pipefail
cd "$(dirname "$0")"

CXX="${CXX:-g++}"
OUT="libemudos_mt32.so"

echo "Building $OUT with $CXX…"
"$CXX" -shared -fPIC -O2 -std=c++17 -fvisibility=hidden \
    -Wno-unused-variable -Wno-unused-but-set-variable \
    emudos_mt32.cpp -o "$OUT"

echo "Built $(pwd)/$OUT"
