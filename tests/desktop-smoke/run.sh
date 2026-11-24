#!/usr/bin/env bash
set -euo pipefail

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
dotnet=${DOTNET:-dotnet}
for command in "$dotnet" Xvfb xvfb-run xauth xdotool dbus-run-session fc-cache; do
    if ! command -v "$command" >/dev/null; then
        printf 'Desktop smoke requires %s. See docs/development.md.\n' "$command" >&2
        exit 127
    fi
done
atspi_service=/usr/share/dbus-1/services/org.a11y.Bus.service
if [[ ! -r "$atspi_service" ]]; then
    printf 'Desktop smoke requires the AT-SPI D-Bus service. See docs/development.md.\n' >&2
    exit 127
fi
if [[ ${VIPER_DESKTOP_SMOKE_BACKTRACE:-0} == 1 ]] && ! command -v gdb >/dev/null; then
    printf 'Desktop smoke backtraces require gdb.\n' >&2
    exit 127
fi
scratch=$(mktemp -d "${TMPDIR:-/tmp}/viper-desktop-smoke.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT

# Start with no connection details from the caller's desktop.
unset DISPLAY WAYLAND_DISPLAY XAUTHORITY SESSION_MANAGER AT_SPI_BUS_ADDRESS
unset DBUS_SESSION_BUS_ADDRESS DBUS_SESSION_BUS_PID DBUS_STARTER_ADDRESS DBUS_STARTER_BUS_TYPE
unset XDG_SESSION_TYPE
export XDG_CONFIG_HOME="$scratch/config" XDG_CACHE_HOME="$scratch/cache"
export XDG_DATA_HOME="$scratch/data" XDG_STATE_HOME="$scratch/state" XDG_RUNTIME_DIR="$scratch/runtime"
mkdir -m 700 -p "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$XDG_DATA_HOME" "$XDG_STATE_HOME" "$XDG_RUNTIME_DIR"
export AVALONIA_GLOBAL_SCALE_FACTOR=1.5 VIPER_HARDWARE_TESTS=0 VIPER_UINPUT_TESTS=0
export VIPER_DESKTOP_SMOKE_ROOT="$scratch" VIPER_SYNAPSE_DEBUG=1
unset VIPER_SYNAPSE_OPEN VIPER_SYNAPSE_HEIGHT

# Build the fixture's font cache before Avalonia and Skia open the first window.
# Read installed fonts and keep generated cache files under the scratch directory.
export FONTCONFIG_FILE="$scratch/fonts.conf"
cat > "$FONTCONFIG_FILE" <<'FONTCONFIG'
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig>
  <dir>/usr/share/fonts</dir>
  <cachedir prefix="xdg">fontconfig</cachedir>
  <alias><family>sans-serif</family><prefer><family>DejaVu Sans</family></prefer></alias>
</fontconfig>
FONTCONFIG
fc-cache -f

# Allow only the private accessibility bus launcher to activate. Desktop
# portals and other session services are outside the smoke fixture.
mkdir "$scratch/services"
cp "$atspi_service" "$scratch/services/"
cat > "$scratch/session.conf" <<'DBUS'
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <type>session</type>
  <listen>unix:tmpdir=/tmp</listen>
  <auth>EXTERNAL</auth>
  <servicedir>services</servicedir>
  <policy context="default">
    <allow send_destination="*"/>
    <allow receive_sender="*"/>
    <allow own="*"/>
  </policy>
</busconfig>
DBUS

"$dotnet" build "$repo/tests/desktop-smoke/DesktopSmoke.csproj" -c Release "$@"
launch=("$dotnet" "$repo/tests/desktop-smoke/bin/Release/net10.0/ViperSynapse.DesktopSmoke.dll")
if [[ ${VIPER_DESKTOP_SMOKE_BACKTRACE:-0} == 1 ]]; then
    launch=(gdb --batch --quiet -return-child-result -ex 'set debuginfod enabled off'
        -ex 'set disable-randomization off' -ex 'set startup-with-shell off'
        -ex 'handle SIGPIPE nostop noprint pass' -ex 'handle SIG34 nostop noprint pass'
        -ex run -ex 'thread apply all bt 12' --args "${launch[@]}")
fi
if xvfb-run -a -e "$scratch/xvfb.log" -s '-screen 0 2304x1440x24 -nolisten tcp -extension GLX' \
        dbus-run-session --config-file="$scratch/session.conf" -- "${launch[@]}"; then
    exit 0
else
    status=$?
    cat "$scratch/xvfb.log" >&2
    exit "$status"
fi
