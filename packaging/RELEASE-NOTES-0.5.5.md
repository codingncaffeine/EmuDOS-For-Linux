# EmuDOS for Linux — v0.5.5

## What's New

- **A built-in game catalog** — EmuDOS now ships a catalog of popular DOS games. A game it recognises starts the right program straight away and gets known-good settings. The catalog updates itself from each new release, and Preferences → Downloads shows how many games it knows.
- **Imports that explain themselves** — after an import the status bar says whether the catalog recognised the game and which program it runs, or which program EmuDOS guessed, so you know when Choose program… is needed. Titles come from a tidied folder or archive name: underscores and tags such as "(1994)" or "[!]" are dropped.
- **Drop a whole folder of games** — a folder with one game per subfolder, archive or disc image imports every game on its own, nested collections included. MT-32 ROMs and SoundFonts kept alongside still install.
- **Installed games start by themselves** — after you run a game's installer and close the window, EmuDOS picks the installed program, so the next launch starts the game instead of the installer. For CD games it pins the installed program as the game's auto-start.
- **Smarter first guesses** — when EmuDOS has to guess a game's program, it now boots the likeliest candidates for a moment behind the scenes and skips any that drop straight back to the DOS prompt.
- **Fullscreen** — Alt+Enter switches the game window to borderless fullscreen and back without releasing the mouse. Each game remembers whether it was fullscreen, and the key can be rebound under Preferences → Hotkeys.
- **Mouse lock that just works** — the mouse locks as soon as you move it in the game window, middle-click releases it, and a click into the game locks it again. Locked movement uses raw, unaccelerated input and stays inside the window even during fast flicks.

## What's Fixed

- **Game audio, gamepads and CRT shaders on Debian and Ubuntu** — SDL3 and the CRT shader engine now ship inside the tarball and the .deb, built to run on Debian 12 and 13 and Ubuntu 22.04 and 24.04. Before, the .deb required an SDL3 package those releases don't have, game audio looked for an SDL3 library name Debian and Ubuntu packages don't provide, and CRT shaders needed a newer system than Debian 12 or Ubuntu 22.04.
- **The right program more often** — import and launch now agree on the program to run, and installer helpers, DOSBox launch scripts from repackaged games, Windows builds and graphics-mode variants are no longer mistaken for the game. Installers inside subfolders are recognised as installers.
- **Multi-disc and cue/bin imports** — "CD1of4"-style disc names now group into one game, and a .cue dropped together with its .bin imports as one disc.
- **AUR installs** — EmuDOS installed from the AUR no longer offers to install a .deb update; it points you to your package manager instead.
- **Hotkey reset** — pressing Esc in a Preferences hotkey box resets it to that hotkey's own default instead of F12.

## Install

- **Debian / Ubuntu** — `emudos_0.5.5_amd64.deb` (`sudo apt install ./emudos_0.5.5_amd64.deb`). Debian 12 or newer, Ubuntu 22.04 or newer.
- **Arch** — `emudos-bin` on the AUR.
- **Any distro** — the portable `EmuDOS-0.5.5-linux-x64.tar.gz`: extract it anywhere and run `./EmuDOS`.

The DOSBox Pure core downloads by itself the first time you add or start a game. EmuDOS ships no games, BIOS files, or copyrighted system software.
