#!/usr/bin/env bash
# Runs INSIDE a clean Debian/Ubuntu container (packaging/deb-install-test.sh mounts it with the .deb
# and smoke-test.sh): install the package with apt, check every shipped binary resolves its libraries,
# then launch it under a virtual X server with smoke-test.sh. Exit 0 = everything passed.
set -uo pipefail
apt-get update -qq >/dev/null
if ! apt-get install -y -q /tmp/emudos.deb xvfb xauth > /tmp/apt.log 2>&1; then
    tail -25 /tmp/apt.log
    exit 1
fi
. /etc/os-release
echo "installed $(dpkg-query -W -f='${Package} ${Version}' emudos) on $PRETTY_NAME"

problems=0
[ -x /usr/bin/emudos ] || { echo "FAIL /usr/bin/emudos missing"; problems=1; }

# Every shipped binary must resolve its libraries, symbol versions included, on this distro.
# Exception: .NET's LTTng trace provider, which the runtime loads only when LTTng is installed.
for f in /usr/lib/emudos/EmuDOS /usr/lib/emudos/*.so*; do
    case $(basename "$f") in libcoreclrtraceptprovider.so) continue ;; esac
    bad=$(ldd "$f" 2>&1 | grep "not found" || true)
    if [ -n "$bad" ]; then
        echo "FAIL $(basename "$f") does not load here:"
        echo "$bad" | sed 's/^/        /'
        problems=1
    fi
done
[ $problems = 1 ] || echo "ok   every shipped library resolves"

xvfb-run -a /tmp/smoke-test.sh /tmp/emudos.deb || problems=1
exit $problems
