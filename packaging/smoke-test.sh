#!/usr/bin/env bash
# Smoke-test a built EmuDOS artifact: unpack it, verify the payload inventory, then LAUNCH it and
# require it to stay running.
#
# Why this exists: emudos-bin 0.5.0-1 shipped to the AUR without a single managed assembly. makepkg
# exited 0, pacman installed it without a complaint, and the app died instantly with
#   "The application to execute does not exist: '/usr/lib/emudos/EmuDOS.dll'"
# No build-time or install-time signal exists for that failure. Only running the app reveals it, so
# the release process has to run the app.
#
# Usage: packaging/smoke-test.sh <artifact> [...]
#   <artifact>: a release .tar.gz, a .deb, or an Arch .pkg.tar.zst
# Exit: 0 all checks passed | 1 a check failed | 2 could not run the launch test
set -uo pipefail

MIN_ASSEMBLIES=150
LAUNCH_SECONDS=15

fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILED=1; }
ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }

smoke_one() {
    local artifact="$1"
    local tmp payload
    FAILED=0
    printf '\n== %s ==\n' "$(basename "$artifact")"

    [[ -f $artifact ]] || { fail "no such file"; return 1; }
    tmp=$(mktemp -d) || return 1
    trap 'rm -rf "$tmp"; trap - RETURN' RETURN

    case $artifact in
        *.tar.gz|*.tgz|*.pkg.tar.zst|*.pkg.tar.xz)
            bsdtar -xf "$artifact" -C "$tmp" 2>/dev/null ;;
        *.deb)
            if command -v dpkg-deb >/dev/null; then
                dpkg-deb -x "$artifact" "$tmp"
            else
                bsdtar -xOf "$artifact" 'data.tar.*' | bsdtar -xf - -C "$tmp"
            fi ;;
        *)  fail "unrecognised artifact type"; return 1 ;;
    esac
    [[ $? -eq 0 ]] || { fail "could not unpack"; return 1; }

    # The apphost anchors the payload wherever the artifact happens to root it.
    payload=$(find "$tmp" -name EmuDOS -type f -perm -u+x -printf '%h\n' 2>/dev/null | head -1)
    [[ -n $payload ]] || { fail "no EmuDOS apphost found in the artifact"; return 1; }
    ok "payload at ${payload#$tmp}/"

    # --- inventory -------------------------------------------------------------
    local n
    n=$(find "$payload" -maxdepth 1 -name '*.dll' | wc -l)
    if (( n < MIN_ASSEMBLIES )); then
        fail "only $n managed assemblies (expected >= $MIN_ASSEMBLIES)"
    else
        ok "$n managed assemblies"
    fi

    local f
    for f in EmuDOS.dll EmuDOS.Core.dll EmuDOS.Metadata.dll \
             EmuDOS.runtimeconfig.json EmuDOS.deps.json \
             System.Private.CoreLib.dll Avalonia.Base.dll \
             libcoreclr.so libhostfxr.so libhostpolicy.so \
             libemudos_mt32.so librashader.so; do
        [[ -f "$payload/$f" ]] && ok "$f" || fail "$f is missing"
    done

    # --- launch ----------------------------------------------------------------
    # The check that would have caught the AUR bug. Everything above is inference; this is the only
    # step that observes the app actually working.
    if [[ -z ${DISPLAY:-} && -z ${WAYLAND_DISPLAY:-} ]]; then
        printf '  \033[33mSKIPPED\033[0m  launch test: no DISPLAY/WAYLAND_DISPLAY.\n'
        printf '           Inventory alone is NOT a pass. Re-run on a graphical session.\n'
        return 2
    fi

    # EmuDOS has no portable mode: its data folder is $XDG_DATA_HOME/EmuDOS (settings, library,
    # logs, crash.log). Point every XDG base dir into $tmp so the launch never opens the real
    # profile, and check afterwards that the app really used the scratch one.
    local xdg=$tmp/xdg
    local real_data=${XDG_DATA_HOME:-$HOME/.local/share}/EmuDOS
    local real_existed=0
    [[ -e $real_data ]] && real_existed=1
    mkdir -p "$xdg/data" "$xdg/config" "$xdg/cache" "$xdg/state"

    local log=$tmp/launch.log rc start elapsed
    start=$SECONDS
    ( cd "$payload" &&
      exec env XDG_DATA_HOME="$xdg/data" XDG_CONFIG_HOME="$xdg/config" \
               XDG_CACHE_HOME="$xdg/cache" XDG_STATE_HOME="$xdg/state" \
               timeout -k 5 "$LAUNCH_SECONDS" ./EmuDOS ) > "$log" 2>&1
    rc=$?
    elapsed=$((SECONDS - start))

    # timeout exits 124 when it had to stop the app (137 if TERM was ignored): the app was still up.
    if (( rc == 124 || rc == 137 )) && (( elapsed >= LAUNCH_SECONDS )); then
        ok "still running after ${LAUNCH_SECONDS}s"
    else
        fail "exited after ${elapsed}s (code $rc)"
        sed 's/^/           | /' "$log" | head -20
    fi

    # A .NET host failure prints to stdout and can still exit 0 in some paths.
    if grep -qiE 'does not exist|A fatal error|Failed to load|FileNotFoundException|XamlLoadException' "$log"; then
        fail "host/runtime error in output:"
        grep -iE 'does not exist|A fatal error|Failed to load|FileNotFoundException|XamlLoadException' "$log" |
            sed 's/^/           | /' | head -5
    fi

    # The app creates its data folders during startup, so their presence proves it got that far AND
    # honoured the scratch profile. CrashLog records unhandled and unobserved exceptions there.
    if [[ -d $xdg/data/EmuDOS/Catalog ]]; then
        ok "started against the scratch profile"
    else
        fail "no data folder under the scratch profile (startup never reached AppServices)"
    fi
    if [[ -s $xdg/data/EmuDOS/crash.log ]]; then
        fail "the app recorded exceptions in crash.log:"
        sed 's/^/           | /' "$xdg/data/EmuDOS/crash.log" | head -20
    fi
    if (( ! real_existed )) && [[ -e $real_data ]]; then
        fail "the launch created the REAL data folder $real_data"
    fi

    return $FAILED
}

(( $# )) || { echo "usage: $(basename "$0") <artifact> [...]" >&2; exit 2; }

# A failure outranks an incomplete run, whatever order the artifacts come in.
rc=0
for a in "$@"; do
    smoke_one "$a"
    case $? in
        0) ;;
        2) (( rc == 0 )) && rc=2 ;;
        *) rc=1 ;;
    esac
done

printf '\n'
case $rc in
    0) printf '\033[32mSMOKE TEST PASSED\033[0m — artifact launches.\n' ;;
    2) printf '\033[33mSMOKE TEST INCOMPLETE\033[0m — inventory only, app never launched.\n' ;;
    *) printf '\033[31mSMOKE TEST FAILED\033[0m — do NOT release this artifact.\n' ;;
esac
exit $rc
