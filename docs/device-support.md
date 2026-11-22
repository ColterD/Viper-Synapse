# Device support

Start with the [hardware table in the README](../README.md#devices). The catalogue identifies many Razer models from OpenRazer data. A catalogue entry indicates that the app knows about a model. Hardware testing establishes which features work on a specific model and connection mode.

For a mouse that supports multiple connections, record results separately for wired, wireless dongle, and Bluetooth where applicable. Detection, remapping, lighting, sensitivity, polling, and power settings can have different coverage on the same device.

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
