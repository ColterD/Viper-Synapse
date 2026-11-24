<p align="center"><img src="assets/icon-128.png" alt="Viper Relay"></p>

# Viper Relay

This is [ColterD’s fork](https://github.com/ColterD/Viper-Synapse) of
[Viper Synapse by codingncaffeine](https://github.com/codingncaffeine/Viper-Synapse).
This fork ships as Viper Relay. The executable, package, and settings directory use the name `viper-synapse`.

The current candidate, `v0.0.10-rc.1`, adds configurable Naga Chroma DPI steps and tilt fallback, improves BlackWidow Chroma V2 macro recovery, and makes the remap editor easier to use with a keyboard. The device changes were tested in emulation. [Using Viper Relay](docs/using-viper-synapse.md) · [Device support](docs/device-support.md)

A Razer Synapse–style control centre for Razer keyboards, keypads and mice on Linux: key and button
remapping, Hypershift, macros, lighting, sensitivity stages, polling rate and power settings, in a window
that feels like the one you know.

Viper Relay talks to devices directly over hidraw using Razer's control protocol, so it needs no kernel
module and no other daemon. Remaps and macros run in user space through evdev and uinput, so they work the
same on Wayland and X11.

<p align="center"><img src="assets/screenshots/customize.png" alt="Customize: the key list, the keypad and the binding flyout" width="860"></p>

## Documentation

[Documentation index](docs/README.md)

- [Getting started](docs/getting-started.md) covers building from source, Linux permissions, and common setup problems.
- [Device support](docs/device-support.md) explains what catalogue entries and hardware tests tell you.
- [Development](docs/development.md) maps the projects and describes testing and GitHub checks.

## What it does

- **Customize** — rebind any key or button: keyboard functions with modifiers, mouse functions, turbo,
  macros, Razer Hypershift (a second layer while a key is held), switch profile or keymap, launch a
  program, type text, multimedia keys, or disable the key. Up to eight keymaps per profile.
- **Macros** — record with Synapse's three delay settings (record delay, default delay, no delay) and an
  optional start countdown; edit the action list (keystrokes, delays, text, commands, reorder); play once,
  a set number of times, while held, or toggle on and off.
- **Lighting** — brightness and quick effects per zone: static, breathing, spectrum cycling, wave,
  reactive, starlight, wheel, and effects Viper Relay draws itself on any device, Synapse's Fire and
  Ripple and iCUE-style Color Shift, Color Pulse, Color Wave, Visor, Rain, Watercolor, Marquee,
  Sequential, Strobing, Gradient, Heartbeat and Temperature (follows the CPU); per-key colours on keypads.
- **Performance** — up to five sensitivity stages with optional separate X and Y, and the polling rate.
- **Power** — sleep timer and low-battery threshold on wireless mice.
- **Profiles** — per device, switched by hand, by a key, or automatically while a linked program is in
  front (KDE Plasma, Hyprland and X11 desktops).
- **Starts at sign-in** if you want it to (off by default), and lives in the tray.

<p align="center">
  <img src="assets/screenshots/macros.png" alt="The macro editor" width="420">
  <img src="assets/screenshots/performance.png" alt="Sensitivity stages and polling rate" width="420">
</p>

## Devices

The device catalogue covers 270+ Razer models (from OpenRazer's device data) plus our own entries.
Device support and validation:

| Device | Status |
| --- | --- |
| Tartarus V2 Pro | key bindings, macros, lighting and per-key colours — tested |
| Naga Left-Handed Edition 2020 | protocol, sensitivity and polling tested on the device; buttons and lighting in testing |
| Basilisk V4 Pro | Wired and HyperSpeed configuration, with provisional Bluetooth input support. [Connection and feature coverage](docs/basilisk-v4-pro.md) |
| Naga Chroma (`RZ01-0161`) | Configurable DPI steps, tilt, and thumb-button macros verified in emulation. |
| BlackWidow Chroma V2 (`RZ03-0203`) | Native M1–M5 macros, held-key input, and startup recovery verified in emulation. |

Check **Health** for feature and connection details, including whether a feature has been tested in emulation or on hardware.

## Install

Download a [stable release](https://github.com/ColterD/Viper-Synapse/releases/latest), or check [release history](https://github.com/ColterD/Viper-Synapse/releases) for the `v0.0.10-rc.1` prerelease. Both Linux x64 package formats are self-contained. See [Getting started](docs/getting-started.md) for checksum verification, installation, and building from source.

## Building

Requires the .NET 10 SDK.

```sh
./run.sh                            # build the current source and start it
dotnet test ViperSynapse.slnx -c Release
packaging/build-release.sh          # tarball and .deb in packaging/out
```

**Settings → General → About** shows the running version and build date. **Releases** on the same page links to notes and downloads for that version.

`viper-cli list` shows the Razer devices it can see; `viper-cli probe /dev/hidrawN` reads a device's
serial, firmware and settings without changing anything.

For a wired Naga Chroma (`1532:0053`), run `viper-cli naga-dpi-watch /dev/hidrawN` with its interface 0
control node from `viper-cli list`. It prints the native DPI button press and release reports and live X/Y DPI
samples without grabbing input or changing mouse settings. See [DPI diagnostic timing](docs/legacy-chroma-research.md#read-only-dpi-diagnostic).

For Basilisk V4 Pro firmware slots and buttons, see [onboard CLI usage](docs/basilisk-v4-pro.md#onboard-profiles-and-buttons).

### Profile exchange

With the Viper Relay service running, export or import a local profile archive using its
four-digit settings key (`00e3` for Basilisk V4 Pro):

```sh
viper-cli profile export 00e3 basilisk-profile.json
viper-cli profile import 00e3 basilisk-profile.json
```

Export selects the active profile by default and includes the macros it uses. To export a different
profile, pass its UUID after the file path. Import keeps the current profile selected. Add
`--replace` to update a profile with the same UUID. A conflicting global macro is reported
before import. For a trusted archive that launches a command, application, file or URL,
add `--allow-launch`. Refresh the app to reload its macro list after a CLI import.

## Acknowledgements

The [OpenRazer](https://openrazer.github.io/) project documented the Razer control protocol that this work
builds on, and its device data seeds the catalogue.

Viper Relay is an independent project. It is not affiliated with or endorsed by Razer Inc. "Razer" and
"Synapse" are trademarks of Razer Inc. Device photos are fetched from Razer's own servers at run time and
are not part of this repository.

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).
