EmuDOS for Linux — quick start
==============================

A Boxer-style frontend for your classic DOS games, built on the DOSBox Pure
libretro core. This is the self-contained Linux build — the .NET runtime is
bundled, so there is nothing else to install to launch it.

Running
-------
From this folder:

    ./EmuDOS

Or install the .deb (system-wide, adds an "EmuDOS" entry to your app menu and an
`emudos` command), or the AUR package `emudos-bin` on Arch.

Optional components
-------------------
Every one of these is optional — the matching feature simply stays dark if the
component isn't present:

  * librashader   — CRT / scanline shaders
  * ffmpeg        — record gameplay to video
  * xorriso       — build an ISO from a game folder (Add Disc)
  * libvlc        — animated cover / video previews

On Debian/Ubuntu:  sudo apt install ffmpeg xorriso libvlc5 vlc-plugin-base
On Arch:           sudo pacman -S librashader ffmpeg libisoburn vlc
                   (librashader is on the AUR)

Where your data lives
---------------------
Library, save states, screenshots and downloaded cores live under
  ~/.local/share/EmuDOS   (or $XDG_DATA_HOME/EmuDOS)
Settings live under
  ~/.config/EmuDOS        (or $XDG_CONFIG_HOME/EmuDOS)

Updating
--------
EmuDOS checks GitHub for new releases and can update itself in place (portable
tarball self-replace, or `pkexec dpkg -i` for the system .deb).

EmuDOS ships no games, BIOS files, or copyrighted system software.

Project: https://github.com/codingncaffeine/EmuDOS-For-Linux
Licensed under the GNU GPL v3. See LICENSE and NOTICES.txt.
