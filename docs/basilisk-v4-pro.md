# Basilisk V4 Pro support

Viper Relay uses the same local profile for the wired, HyperSpeed, and Bluetooth versions of the Basilisk V4 Pro. The wired and HyperSpeed connections support the vendor control reports. Pair Bluetooth in your desktop's Bluetooth settings. Once paired, Viper uses the Linux input events the mouse exposes.

| Feature | Wired USB `1532:00E2` | HyperSpeed `1532:00E3` | Bluetooth |
| --- | --- | --- | --- |
| Discovery and reconnect | Scripted Linux node and report replay | Scripted replay and a prior Viper hardware probe | Provisional recognition for the reported `1532:00E4` on the Bluetooth bus. Tested with synthetic descriptors and input events. |
| Buttons and wheel | Software remapping of standard controls. The Onboard page and CLI edit five firmware buttons on the normal or Hypershift layer. | Same, with protocol replay coverage | Software remapping of controls Linux reports |
| DPI, polling and power | Core commands and UI, with active-slot DPI staging | Same, with prior Viper DPI capture and HyperPolling read | Battery percentage appears if BlueZ exposes it for the connected mouse. Vendor settings are unavailable in Viper. |
| Lighting | Three groups for quick effects and a seven-pixel advanced frame | Same, with earlier Viper hardware measurements for the three quick-effect groups | Firmware lighting control unavailable in Viper |
| Wheel settings | Tactile, free-spin and precision tactile, with on/off controls for acceleration and Smart-Reel | Same, with prior Viper readback of the two toggles | Software scroll speed and remaps |
| Onboard profile | List, create, activate, and delete slots from the Onboard page or CLI. Slot names are stored locally by mouse serial. | Same, with protocol replay coverage | Local profile only |
| Calibration | Basic lift-off commands. Viper has no Basilisk-specific calibration capture. | Same | Razer documents calibration as unavailable over Bluetooth. |

The checked-in replay tests exercise the feature-report envelope and exact command bytes. They use the previously captured Basilisk DPI table and synthetic responses for connection transitions, failures, active slots, wheel modes, and onboard operations. No replay test requires a mouse. Polling cadence, RF stability, physical button and sensor behavior, LED output, and firmware persistence require hardware evidence.

Open **Health** on the device tab to see the detected model, connection, and feature availability. The **Access** column shows whether Viper has the interface it needs. **Verification** states what was tested for that feature and connection, such as a physical DPI-table read or an emulated restore. **Detected** means Linux listed an input interface and the service will check access when it starts remapping.

Use **Inspect unit** to select which connected mouse supplies Health and live hardware readings. Local profile edits apply to every connected unit that shares the model profile. The **Onboard** page has a separate serial selector for operations that write to one mouse.

## Onboard profiles and buttons

Open **Onboard** in the Basilisk device tab over wired USB or HyperSpeed. Choose the mouse by serial if several are connected. The page shows the active slot separately from the slot you are inspecting. Activate a slot before changing its buttons. To edit a binding, select the button and layer, then choose a key and any modifiers and select **Assign key**. **Read assignment** shows the stored function and indicates when Hypershift inherits it from the normal layer.

### Back up and restore a slot

Activate the slot and select **Back up slot** to save a versioned JSON file. To restore, activate the destination slot, select **Open backup**, and review the source slot, destination, and coverage before confirming. You can restore into a different active slot on a Basilisk V4 Pro.

| Included | Read and write evidence |
| --- | --- |
| DPI stage list, including separate X and Y, and the active stage | The paired DPI-table reports preserve the Basilisk's known stage layout. Viper rejects tables it cannot reconstruct exactly. |
| Supported assignments for the five firmware buttons | The paired button reports echo the slot, button, and layer. The backup includes explicit keyboard shortcuts, disabled buttons, and documented Normal-layer defaults. |
| Logo, scroll-wheel, and underglow brightness | Reports `0F/84` and `0F/04` address the active slot and LED. Viper stores the exact 0–255 value from a reply that identifies both. |
| Fixed logo, scroll-wheel, and underglow lighting effects | Synapsex sends `0F/82` with the slot and LED. OpenRazer uses corresponding slot-addressed `0F/02` writes. Viper checks the full readback and supports the documented Off, Static RGB, Spectrum, and standard Wave encodings. |
| Smart-Reel and scroll acceleration on/off | Smart-Reel uses `02/97` and `02/17`. Acceleration uses `02/96` and `02/16`. Viper requires the requested slot and a value of 0 or 1. |

Version 4 adds underglow as one firmware lighting zone, `0A`. Version 1 through 3 files still restore. The preview lists the settings present in the file. Backup omits individual settings with missing, stale, or unsupported replies. Before writing, Viper requires a reversible original value for every included setting. It reads brightness alongside each effect so that it can preserve or restore brightness even if changing the effect resets it.

Fixed effects require a reply matching the requested slot and zone, with a supported payload and only zero padding if present. Viper retains the RGB and supported effect parameters exactly. The supported Spectrum encoding uses a zero speed byte and Wave uses direction `01` and speed `28` in hex. The OpenRazer underglow patch routes both USB product IDs through the same zone-specific brightness and effect writers. Its author reported testing on HyperSpeed. Viper's readback and rollback are verified in emulation. Raw underglow effect replies across physical slots still need captures. Custom frames, individual underglow pixels, and other effect variants remain outside the format. [OpenRazer hardware discussion](https://github.com/openrazer/openrazer/pull/2975) · [Underglow patch](https://github.com/ashrynsilkstone-ui/openrazer-basilisk-v4-pro/commit/e3a3f020a3a10379a8f7d9f2a05ecc4684551de9) · [Synapsex effect reports](https://github.com/iMacnucintosh/synapsex/blob/1d9d23352b7a87795c04ae04b5ab5c89d00ee933/crates/razer-protocol/src/lighting.rs)

Wheel mode uses `02/B0` and `02/30`. OpenRazer leaves the first argument zero, and Synapsex places the active slot there. Available captures do not establish slot-specific mode storage, so wheel mode remains outside the backup. Wheel sensitivity levels, inherited Hypershift assignments, undocumented button functions, polling, power, local profile names, and macros are also excluded. [OpenRazer wheel-mode discussion](https://github.com/openrazer/openrazer/pull/2975) · [Synapsex slot routing](https://github.com/iMacnucintosh/synapsex/blob/1d9d23352b7a87795c04ae04b5ab5c89d00ee933/crates/razer-protocol/src/onboard.rs)

If a write fails, Viper attempts to restore the original values and verifies the rollback by reading them back. If it cannot verify recovery, it reports that explicitly. Keep the mouse connected and the destination slot active until the operation finishes.

### CLI

With the Viper service running and one wired or HyperSpeed Basilisk connected, list the slots before making changes. Copy the serial from the result and pass it before each command that changes the mouse. If more than one Basilisk is connected, pass the serial to the read commands too.

```sh
viper-cli onboard list
serial='YOUR-MOUSE-SERIAL'
viper-cli onboard --serial "$serial" create 2
viper-cli onboard --serial "$serial" rename 2 "Work"
viper-cli onboard --serial "$serial" select 2
viper-cli onboard buttons
viper-cli onboard --serial "$serial" key 2 39 normal 68
viper-cli onboard --serial "$serial" read 2 39 normal
viper-cli onboard --serial "$serial" restore 2 39
```

In this example, hexadecimal HID usage `68` assigns F13 to the Hypershift clutch in slot 2. Reassigning the normal-layer clutch takes away its Hypershift function until you restore it. The supported buttons are M1 profile cycle (`0E`), the clutch (`39`), DPI cycle (`60`), scroll mode (`6A`), and the front side button (`80`). The front side button's firmware default is disabled. Restoring it does not launch Razer's AI Prompt Master.

Writes target the active slot and confirm the exact binding and layer. A Hypershift read may report the normal-layer assignment as inherited. The `restore` command restores the documented normal-layer function. To remove an inactive slot, run `viper-cli onboard --serial SERIAL delete SLOT --confirm`. The service protects slot 1 and the active slot.

The public protocol source describes slot listing (`05/81`), creation (`05/02`), deletion (`05/03`), and selection (`05/04`). Button reads and writes use `02/8C` and `02/0C` with the slot, control ID, and layer. Viper confirms these operations through readback. The support claims here describe emulated verification of Viper's implementation. [Synapsex onboard commands and button IDs](https://github.com/iMacnucintosh/synapsex/blob/1d9d23352b7a87795c04ae04b5ab5c89d00ee933/crates/razer-protocol/src/onboard.rs) · [Button report layouts](https://github.com/iMacnucintosh/synapsex/blob/1d9d23352b7a87795c04ae04b5ab5c89d00ee933/crates/razer-protocol/src/commands.rs) · [Razer control list](https://www.razer.com/gaming-mice/razer-basilisk-v4-pro)

## Remaining device and ecosystem work

| Feature | Evidence needed for implementation |
| --- | --- |
| Additional onboard assignments | Physical validation of keyboard, disabled, and normal-layer restore actions. Restoring inherited Hypershift settings and other firmware function types still needs command evidence. |
| Full-slot backup and reset | Hardware captures of fixed-effect readbacks across slots, coverage for remaining effect variants, custom frames, wheel mode and opaque assignments, and persistence verification after power loss. |
| Smart-Reel and acceleration levels | Captures that distinguish all five levels. The two on/off commands are known, and an added level byte was previously observed to have no stored effect. |
| Advanced sensor options and Perfect 180 | Model-specific commands and readbacks for asymmetric cutoff, dynamic sensitivity, sensitivity matching, and rotation. Perfect 180 also needs a reproducible motion capture or an independently tested software action. |
| Bluetooth telemetry and vendor settings | A Basilisk Bluetooth HID/service descriptor and hardware confirmation of BlueZ battery reporting. Vendor configuration still needs a transport and command trace. Razer documents some Synapse Bluetooth configuration, a 125 Hz ceiling, and reduced capabilities. |
| HyperSpeed re-pairing, dock, and firmware | Pro-specific pairing and child-device routing captures that preserve any paired keyboard. Firmware writes also need the signed package format, device matching, and a verified recovery procedure. The normal device information shows the firmware version returned by the device. |
| Cloud profiles, Chroma, and Prompt Master | Viper supports local profile exchange, copying quick lighting effects between its devices, and Launch Program bindings. Vendor cloud sync and Prompt Master require supported service interfaces. Razer publishes a Chroma REST interface, but compatibility with Linux games and partner devices still needs a tested adapter. |

## Sources

- [Razer Basilisk V4 Pro features](https://www.razer.com/gaming-mice/razer-basilisk-v4-pro)
- [Razer Bluetooth support and limitations](https://mysupport.razer.com/app/answers/detail/a_id/14589/kw/Mats)
- [OpenRazer device report, descriptors and USB IDs](https://github.com/openrazer/openrazer/issues/2958)
- [OpenRazer wired and HyperSpeed hardware report and command references](https://github.com/openrazer/openrazer/pull/2975)
- [Synapsex reported Bluetooth product ID](https://github.com/iMacnucintosh/synapsex)
- [BlueZ Device API](https://bluez.readthedocs.io/en/latest/device-api/) and [Battery API](https://bluez.readthedocs.io/en/latest/battery-api/)
- [Razer Mouse Dock V2 Pro](https://www.razer.com/gaming-mice-accessories/razer-mouse-dock-v2-pro)
- [Razer Chroma REST API](https://assets.razerzone.com/dev_portal/REST/html/index.html), [Razer profile synchronization](https://mysupport.razer.com/app/answers/detail/a_id/13859/kw/CLOUD%20PROFILES), and [Razer AI Prompt Master](https://mysupport.razer.com/app/answers/detail/a_id/15017)
