# Getting started

Viper Relay runs on Linux. The desktop app starts its own user-space service when needed. You need the .NET 10 SDK to build from source and a Linux desktop session to use the app.

## Build from source

```sh
git clone https://github.com/ColterD/Viper-Synapse.git
cd Viper-Synapse
./run.sh
```

`run.sh` builds and launches the desktop app from the current checkout. **Settings → General → About** shows the running version and build date.

## Set up device permissions

For a source or tarball installation, install the supplied udev rule once. The packaged `.deb` installs the rule as part of setup. Run these commands from the source checkout.

```sh
sudo install -m 0644 packaging/udev/70-viper-synapse.rules /etc/udev/rules.d/
sudo modprobe uinput
sudo udevadm control --reload
sudo udevadm trigger
```

For a tarball, use the `70-viper-synapse.rules` file in the extracted directory. The rule lets the logged-in session access Razer devices and `/dev/uinput`. Launch the app from your normal desktop session.

## Install a packaged build

Use the [stable download page](https://github.com/ColterD/Viper-Synapse/releases/latest) for stable builds and [release history](https://github.com/ColterD/Viper-Synapse/releases) for prereleases such as `v0.0.10-rc.1`. The Linux x64 archives include .NET. Only source builds need the SDK.

Download `SHA256SUMS` from the same release as your package and put it beside the download. Verify the package before extracting or installing it.

```sh
sha256sum --check --ignore-missing SHA256SUMS
```

Extract the Linux x64 tarball and run `./viper-synapse` in the extracted directory. For Debian, use the downloaded filename. For example, run `sudo apt install ./viper-synapse_0.0.10-rc.1_amd64.deb` from its directory for this candidate.

## Connect your first Razer device

1. Install the package or set up device permissions as described above.
2. Launch Viper Relay from your normal desktop session. Connect the device over USB or through its wireless receiver. For Bluetooth, pair and connect it in your Linux desktop settings.
3. Viper opens on **Devices** and scans automatically. Select a device when it appears. If the scan completes with no devices, check the connection and use **Refresh devices**. If Viper could not scan for devices, use **Retry scan** to reconnect to the service.
4. Open the device’s **Health** tab to see its model, connection, and what features Viper can use on that connection. Open **Customize** for button mappings and use the other device tabs for features such as lighting or sensitivity where available. For a device with several connection modes, check Health again after switching modes.

Installations from a tarball or source may need to load the `uinput` module again after a reboot for software remapping. Device-specific support and diagnostic commands are listed in [Device support](device-support.md).

## Troubleshoot

- **The app cannot see the device.** Reconnect it after installing the udev rule, then follow [device diagnostics](device-support.md#collect-device-diagnostics).
- **The device appears, but remapping is unavailable.** Check that the `uinput` module is loaded and that your desktop session can access `/dev/uinput`, then refresh Devices. You can prepare software profiles and use supported firmware controls while remapping is unavailable.
- **Linked profiles do not switch with the focused app.** Linked-program switching uses KDE Plasma, Hyprland, or X11. GNOME on Wayland does not expose the active window to this app.

User settings live under `${XDG_CONFIG_HOME:-$HOME/.config}/viper-synapse`. The directory includes per-device profiles, macros, shortcuts, and app preferences.

[Documentation index](README.md)
