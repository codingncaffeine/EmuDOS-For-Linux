#!/usr/bin/env bash
# Builds librashader (the CRT/slang shader runtime EmuDOS bundles) from source, for release packages.
# fetch.sh pulls Arch's prebuilt binary, which needs glibc 2.38 and a GCC 13 C++ runtime — so shaders
# could not load on Debian 12 or Ubuntu 22.04. packaging/build-release.sh runs this inside the same
# old-glibc container as the MT-32 shim and SDL3, and ships the result instead. Same version and
# features as the fetched build (runtime-opengl + runtime-vulkan), same recipe as Arch's PKGBUILD.
# Needs git, a C/C++ toolchain, cmake, ninja and network access for the Rust toolchain and crates.
set -euo pipefail
cd "$(dirname "$0")"

TAG=librashader-v0.11.2
OUT=librashader.portable.so

export CARGO_HOME="$PWD/.cargo-home" RUSTUP_HOME="$PWD/.rustup-home" PATH="$PWD/.cargo-home/bin:$PATH"
# The toolchain the release build was verified with (a newer stable may change the output).
RUST=1.98.1
command -v rustup >/dev/null || curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --no-modify-path --default-toolchain "$RUST" >/dev/null
rustup toolchain install "$RUST" --profile minimal >/dev/null && rustup default "$RUST" >/dev/null

[ -d src-$TAG ] || git clone -q --depth 1 --branch "$TAG" https://github.com/SnowflakePowered/librashader.git "src-$TAG"
cd "src-$TAG"
cargo run -q -p librashader-build-script -- --stable --profile optimized -- \
    --no-default-features --features=runtime-vulkan,runtime-opengl
cp -f target/optimized/librashader.so "../$OUT"
echo "Built $(cd .. && pwd)/$OUT"
