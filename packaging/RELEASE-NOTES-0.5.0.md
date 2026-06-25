# EmuDOS for Linux — v0.5.0

The first public release of **EmuDOS for Linux** — a native port of EmuDOS, the
Boxer-style frontend for your classic DOS games, built on the DOSBox Pure libretro
core. It aims to look and feel exactly like the Windows app.

## Highlights

- **Your games as a shelf.** Drop in a folder or disc image and EmuDOS identifies the
  game, fetches box art, metadata and video snaps, and lays your collection out as a
  browsable bookshelf with a detail card for each title.
- **One-click play** through the bundled DOSBox Pure core (downloaded on first run).
- **Save states, screenshots, cheats**, per-game notes, and shell-opened manuals.
- **Disc games** — mount from images, or build an ISO straight from a folder.
- **Hardware 3dfx / Voodoo** rendering through OpenGL, with a software fallback.
- **CRT shaders** via librashader and **Roland MT-32** synthesis with an on-screen LCD.
- **Gamepad support**, true in-game mouse lock, per-game machine tuning, and in-app updates.

## Install

- **Debian / Ubuntu** — download `emudos_0.5.0_amd64.deb` and install it
  (`sudo apt install ./emudos_0.5.0_amd64.deb`). All dependencies are pulled
  automatically; the CRT-shader runtime and MT-32 synth ship inside the package.
- **Arch** — `emudos-bin` on the AUR.
- **Any distro** — the portable `EmuDOS-0.5.0-linux-x64.tar.gz`: extract it anywhere
  and run `./EmuDOS`. Nothing else to install.

After first launch, open **Preferences → Downloads** to grab the DOSBox Pure core, and
optionally FFmpeg (gameplay recording) and the CRT shader preset pack.

## Notes

- Requires a reasonably current distribution (Debian 13 / recent Ubuntu or newer). On
  older systems the app still runs, but CRT shaders may be unavailable.
- EmuDOS ships no games, BIOS files, or copyrighted system software.

Built with credit to [DOSBox Pure](https://github.com/schellingb/dosbox-pure),
[librashader](https://github.com/SnowflakePowered/librashader), [munt](https://github.com/munt/munt),
and [SDL](https://www.libsdl.org/). See `NOTICES.txt` for full attribution.
