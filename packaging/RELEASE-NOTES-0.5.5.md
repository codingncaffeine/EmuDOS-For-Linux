## What's New

- **Built-in game catalog** — Popular DOS games are recognised on import and start the right program with known-good settings. The catalog updates itself with each release.
- **Clearer imports** — The status bar says whether a game was recognised and which program it runs, and titles are tidied from folder names.
- **Folders of games** — Drop a folder full of games and each one is imported on its own.
- **Installers handled** — After a game's installer runs, the next launch starts the installed game.
- **Smarter guesses** — When EmuDOS has to guess, it briefly tries the likeliest programs and skips any that drop straight back to DOS.
- **Fullscreen** — Alt+Enter toggles fullscreen without releasing the mouse, remembered per game.
- **Automatic mouse lock** — The mouse locks as soon as you move it in the game window; middle-click releases it.

## What's Fixed

- **Debian and Ubuntu** — SDL3 (game audio and gamepads) and the CRT shader engine now ship with the app and load on Debian 12/13 and Ubuntu 22.04/24.04.
- **Program picking** — Installer helpers, repack launch scripts and Windows builds are no longer mistaken for the game, and multi-disc sets and cue/bin pairs import correctly.
- **AUR updates** — AUR installs are pointed to the package manager instead of being offered a .deb.
- **Hotkey reset** — Esc in a hotkey box now restores that key's own default.
