#!/usr/bin/env bash
set -euo pipefail

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
dotnet=${DOTNET:-dotnet}
scratch=$(mktemp -d "${TMPDIR:-/tmp}/viper-desktop-smoke.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT

# Start with no connection details from the caller's desktop.
unset DISPLAY WAYLAND_DISPLAY XAUTHORITY SESSION_MANAGER AT_SPI_BUS_ADDRESS
unset DBUS_SESSION_BUS_ADDRESS DBUS_SESSION_BUS_PID DBUS_STARTER_ADDRESS DBUS_STARTER_BUS_TYPE
unset XDG_SESSION_TYPE
export XDG_CONFIG_HOME="$scratch/config" XDG_CACHE_HOME="$scratch/cache"
export XDG_DATA_HOME="$scratch/data" XDG_STATE_HOME="$scratch/state" XDG_RUNTIME_DIR="$scratch/runtime"
mkdir -m 700 -p "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$XDG_DATA_HOME" "$XDG_STATE_HOME" "$XDG_RUNTIME_DIR"
export AVALONIA_GLOBAL_SCALE_FACTOR=1.5 VIPER_HARDWARE_TESTS=0
export VIPER_DESKTOP_SMOKE_ROOT="$scratch" VIPER_SYNAPSE_DEBUG=1
unset VIPER_SYNAPSE_OPEN VIPER_SYNAPSE_HEIGHT

# The fixture owns the only application service on this bus. Desktop portals and
# other session services should not be activated by the smoke test.
cat > "$scratch/session.conf" <<'DBUS'
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <type>session</type>
  <listen>unix:tmpdir=/tmp</listen>
  <auth>EXTERNAL</auth>
  <policy context="default">
    <allow send_destination="*"/>
    <allow receive_sender="*"/>
    <allow own="*"/>
  </policy>
</busconfig>
DBUS

"$dotnet" build "$repo/tests/desktop-smoke/DesktopSmoke.csproj" -c Release "$@"
xvfb-run -a -s '-screen 0 1920x1280x24 -nolisten tcp -extension GLX' \
    dbus-run-session --config-file="$scratch/session.conf" -- \
    "$dotnet" "$repo/tests/desktop-smoke/bin/Release/net10.0/ViperSynapse.DesktopSmoke.dll"
