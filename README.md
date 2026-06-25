<p align="center">
  <img src="src/EmuDOS/Assets/EmuDOS-Logo.png" alt="EmuDOS" width="420">
</p>

<p align="center">
  <a href="https://www.gnu.org/licenses/gpl-3.0"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
</p>

# EmuDOS for Linux

A native **Linux** port of [EmuDOS](https://github.com/codingncaffeine/EmuDOS) — a good-looking,
[Boxer](http://boxerapp.com/)-style DOS gaming frontend. Drop your games in and they appear as boxes
on a shelf: art downloaded automatically, sensible settings applied for you, and Roland MT-32 music
(with a working LCD) when you supply the ROMs.

The original is Windows/WPF/.NET 10; this port is rebuilt on **.NET 10 + Avalonia**. The goal is a
**1:1 clone** — aesthetically and functionally identical to the Windows app, with only the platform
plumbing swapped underneath:

| Windows | Linux |
|---|---|
| WPF | Avalonia (X11/Skia) |
| Direct3D 11 (3dfx render + librashader) | OpenGL / EGL |
| WASAPI / NAudio | SDL3 audio |
| XInput | SDL3 gamepad |
| WebView2 PDF viewer | system PDF handler |
| `LoadLibrary` core loading | `dlopen` |

Emulation is handled by the [DOSBox Pure](https://github.com/schellingb/dosbox-pure) libretro core,
downloaded at runtime — no core is bundled.

> **Legal notice:** This project is a frontend only. It does not include, distribute, or facilitate the
> acquisition of any copyrighted software, game files, BIOS images, or Roland firmware ROMs. You are
> solely responsible for ensuring you have the legal right to use any software you load.

---

## Highlights

- **Bookshelf library** — your games as box art on a shelf; hover a box to preview its gameplay video
  in a retro monitor, and press <kbd>Ctrl</kbd>+<kbd>F</kbd> to search and filter. Drop a folder,
  `.zip`, or CD image to import.
- **2D & 3D box art** — downloaded automatically (ScreenScraper, with a SteamGridDB fallback); choose
  2D or 3D per game or library-wide, or drop in your own cover. Logos, marquees, maps and screenshots
  download from the Manage window's Extras tab.
- **Just-works settings** — a curated catalog (seeded from [eXoDOS](https://www.retro-exo.com/exodos.html))
  applies known-good DOSBox options on import; everything is overridable per game and survives updates.
- **Discs & Windows** — multi-disc games, disc swapping, and installing/booting a full Windows 9x.
- **Roland MT-32** — drop the ROMs in and MT-32 games use them, with an on-screen dot-matrix LCD.
- **Save states**, **screenshots/recording**, **mouse lock**, and a **smart launcher** that picks the
  right program.
- **Cloud save sync** — back up your save states and notes to your own private GitHub repo, with
  optional passphrase encryption, synced automatically at launch (see [Cloud Sync](#cloud-sync)).
- **CRT shaders** — download a CRT-focused slice of the libretro slang shader collection (CRT,
  scanlines, monochrome monitors); GPU-accelerated, switched live in-game and remembered per game, and
  captured in screenshots and recordings.
- **Hardware 3dfx** — Voodoo/Glide games render through hardware OpenGL for a sharp, accelerated picture.

---

## Requirements

- A modern 64-bit Linux desktop (X11 or Wayland)
- Runtime libraries (most desktops already have these; the `.deb` declares them):
  `libsdl3-0` (audio + controllers), `libegl1`/`libgl1` + Mesa drivers (game rendering),
  `ffmpeg` (recording encodes), `libx11-6` + `libfontconfig1` (UI).
  Recommended: `libvlc5` + `vlc-plugin-base` + `vlc-plugin-ffmpeg` (the game-card video snaps).
- The **DOSBox Pure** core `.so` (fetched on demand — Preferences → Downloads)
- **Roland MT-32 / CM-32L ROMs** (optional; supply your own — see [MT-32](#mt-32-and-the-roms))

The published packages bundle the .NET 10 runtime (self-contained), so no separate .NET install is needed.

---

## Install

1. Grab the latest build from the **[Releases page](https://github.com/codingncaffeine/EmuDOS-For-Linux/releases/latest)**:
   - `emudos_<ver>_amd64.deb` — system install (`emudos` on PATH, desktop entry).
   - `EmuDOS-<ver>-linux-x64.tar.gz` — self-contained; extract anywhere writable and run `./EmuDOS`.
   - Arch users: `emudos-bin` on the AUR.
2. On first launch, open **Preferences → Downloads** and get the **DOSBox Pure core** (fetched on
   demand from the Linux libretro build servers, not bundled).
3. Drag a game folder, `.zip`, or disc image onto the window to add it.

The core is downloaded as `dosbox_pure_libretro.so` from
`buildbot.libretro.com/nightly/linux/x86_64` — the same core as upstream, as `.so` instead of `.dll`.

---

## MT-32 and the ROMs

EmuDOS plays MT-32 music with its own synth — a small library built from
[munt](https://github.com/munt/munt) and shipped with the app (`libemudos_mt32.so`). It needs the
**Roland MT-32 (or CM-32L) ROMs**, which are **Roland's copyrighted firmware** — we can't and don't
distribute them. Supply your own by dragging the `.rom` files (or a folder containing them) onto
EmuDOS; the Downloads tab shows whether they're detected.

---

## Cloud Sync

Sign in with your GitHub account (**Preferences → Downloads** — device flow, no password stored) and
your save states, in-game saves, and notes sync through a private repository on your account
(`emudos-saves` by default).

<details>
<summary><strong>How it works</strong> (click to expand)</summary>

Saves pull automatically before a game launches and upload when the session ends (configurable: on
game close / every 15 minutes / manual), or sync everything on demand.

- **Cross-platform** — the same repository serves the Windows app and this port: save on one machine,
  pick up on the other. Saves are keyed by game, so both installs must import the same game.
- **Per-machine library snapshot** — the library database is backed up per machine
  (`db/library.<hostname>.db.gz`) rather than to one shared file, so two machines sharing the repo can
  never overwrite each other's library. Per-game save states are additive and never overwrite an
  existing cloud copy; in-game saves follow a newest-wins, no-clobber rule.
- **Optional encryption** — AES-256-GCM with a passphrase you choose; the same passphrase is required
  on every PC that shares the repository.

Sync activity is logged to the data folder's `Logs/`.

</details>

---

## Folder Layout

Follows the XDG Base Directory spec:

```
~/.config/EmuDOS/                config.json
~/.local/share/EmuDOS/           (or your custom data folder)
    library.db
    Cores/                       (DOSBox Pure .so — downloaded in-app)
    Gameboxes/                   (one folder per imported game: content, saves, notes, art)
    Shaders/                     (CRT slang preset pack — downloaded in-app; librashader runtime ships bundled)
    System/                      (Roland MT-32 / CM-32L ROMs)
    Screenshots/ / Recordings/ / Logs/ / ...
```

---

## Building

Requires the **.NET 10 SDK** and **Avalonia 12**. The MT-32 synth shim builds automatically from
vendored source (`src/native/mt32`, LGPL munt) via an MSBuild target, so the C toolchain is needed to
build from source:

```sh
sudo apt install build-essential pkg-config
```

```sh
git clone git@github.com:codingncaffeine/EmuDOS-For-Linux.git
cd EmuDOS-For-Linux
dotnet build EmuDOS.slnx -c Release
```

> The toolchain is **only needed to build from source** — it compiles `libemudos_mt32.so`. End users
> running a packaged release (`.deb` or tarball) don't need it: the compiled `.so` is bundled.

---

## Credits

EmuDOS stands on the work of others, with thanks. **Emulation** is the
[DOSBox Pure](https://github.com/schellingb/dosbox-pure) libretro core by Bernhard Schelling (and the
[DOSBox](https://www.dosbox.com/) project it builds on) — downloaded at runtime, never bundled.

- **[Boxer](http://boxerapp.com/)** by Alun Bestor — the Mac DOS frontend that inspired EmuDOS, and the
  reference for the Roland MT-32 LCD.
- **[munt / mt32emu](https://github.com/munt/munt)** — the Roland MT-32 emulation behind our synth.
- **[eXoDOS](https://www.retro-exo.com/exodos.html)** — the DOS configuration set our catalog is seeded from.
- **[libretro](https://www.libretro.com/)** — the core API EmuDOS hosts.

**Frameworks & libraries** (the Linux port swaps several of the Windows ones):

| Library | Purpose | License |
|---|---|---|
| [Avalonia](https://avaloniaui.net/) | Cross-platform UI (replaces WPF) | MIT |
| [SDL3](https://www.libsdl.org/) | Audio output + controllers (replaces NAudio/WASAPI + XInput) | Zlib |
| [librashader](https://github.com/SnowflakePowered/librashader) by [SnowflakePowered](https://github.com/SnowflakePowered) | runs the slang CRT shaders on the GPU (OpenGL backend) — **bundled** with EmuDOS so shaders work out of the box | MPL-2.0 / GPL-3.0 |
| [libretro slang shaders](https://github.com/libretro/slang-shaders) | the downloadable CRT shader collection | per-shader (see repo) |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) | game-card video snaps | LGPL-2.1 |
| [FFmpeg](https://ffmpeg.org/) | optional gameplay recording | GPL |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | library database | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | archive import | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |

Box art and manuals come from [ScreenScraper](https://www.screenscraper.fr/),
[SteamGridDB](https://www.steamgriddb.com/), and the [Internet Archive](https://archive.org/) via their
APIs. Full license texts in `NOTICES.txt`.

This is a community Linux port of [EmuDOS](https://github.com/codingncaffeine/EmuDOS) by the same author.

---

## License

[GNU General Public License v3.0](LICENSE)
