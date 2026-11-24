# Device support

Start with the [hardware table in the README](../README.md#devices). The catalogue identifies many Razer models from OpenRazer data. A catalogue entry indicates that the app knows about a model. Hardware testing establishes which features work on a specific model and connection mode.

For a mouse that supports multiple connections, record results separately for wired, wireless dongle, and Bluetooth where applicable. Detection, remapping, lighting, sensitivity, polling, and power settings can have different coverage on the same device.

## Legacy Chroma in v0.0.10-rc.1

| Wired model | Verified in emulation | Still needs physical confirmation |
| --- | --- | --- |
| Naga Chroma (`RZ01-0161`, `1532:0053`) | Managed DPI steps, write and readback recovery, tilt through native or Linux input, and thumb-button macros. | Which button and wheel events the firmware emits, how far apart mirror events arrive, and whether DPI changes persist. |
| BlackWidow Chroma V2 (`RZ03-0203`, `1532:0221`) | M1–M5 native macros, held-key repeat, native startup recovery, and F13–F17 fallback when exposed. | The device's report descriptor, mode behavior, and whether the Linux driver releases ordinary keys early. |

Use **Health** to check which route is available, and see the [setup steps](using-viper-synapse.md#configure-naga-chroma-dpi-and-tilt) and [report research](legacy-chroma-research.md) for details.

## Collect device diagnostics

From the source checkout, list the detected Razer devices.

```sh
dotnet run --project src/ViperSynapse.Cli -- list
```

If the output identifies a control node and you need firmware or settings details, probe that node. Replace the example node with the path shown by `list`.

```sh
dotnet run --project src/ViperSynapse.Cli -- probe /dev/hidraw17
```

A packaged installation can use `viper-cli list` and `viper-cli probe /dev/hidraw17` directly. Probe output can include the device serial number, so remove it before sharing.

Include the model, connection mode, Linux distribution, desktop session, firmware version if available, and the feature you tried. Record what worked, what failed, and whether you reproduced it on hardware or in a test.

[Documentation index](README.md)
