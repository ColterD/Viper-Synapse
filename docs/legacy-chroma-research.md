# BlackWidow Chroma V2 and Naga Chroma support research

Viper identifies both models from its OpenRazer-derived catalogue. The Lighting page supports Static, Spectrum Cycling, Off, brightness, and custom frames on both devices. The BlackWidow uses one keyboard-wide zone for quick effects. On the Naga, Backlight controls the thumb grid, and the logo and scroll wheel have their own zones. Viper reads brightness back after writing it. Physical-device behavior and other quick effects still need verification.

| Model | Catalogue and vendor capabilities | OpenRazer implementation |
| --- | --- | --- |
| BlackWidow Chroma V2, `1532:0221`, `RZ03-0203x` | Wired keyboard, 6×22 matrix, custom frames, gaming mode, macro indicators, and five M keys. | Standard keyboard effects use `03/0A`, transaction `3F`. Static carries `06` followed by RGB. Main-zone brightness uses `03/03` and `03/83`, transaction `FF`, with storage `01` and zone `05`. |
| Naga Chroma, `1532:0053`, `RZ01-01610` | Wired mouse, 1×3 matrix for logo, wheel and thumb grid. Razer documents twelve thumb buttons, tilt, and X/Y sensitivity. Viper lists 16,000 DPI and polling. | Zoned effects use `03/0D`, transaction `3F`. Static is effect `06` and Spectrum is `04`. Brightness uses `03/03` and `03/83`, transaction `FF`. Transaction selection must follow the command. |

[Razer BlackWidow manual](https://dl.razerzone.com/master-guides/RazerSynapse/BlackWidowChromaV2-00000545-en.pdf) · [Razer Naga manual](https://dl.razerzone.com/master-guides/NagaChroma/NagaChromaOMG-ENG.pdf) · [OpenRazer BlackWidow model and capabilities](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/daemon/openrazer_daemon/hardware/keyboards.py#L484-L501) · [OpenRazer Naga model and capabilities](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/daemon/openrazer_daemon/hardware/mouse.py#L457-L496)

[OpenRazer keyboard effect builders](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerchromacommon.c#L208-L281) · [BlackWidow effect routing](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerkbd_driver.c#L2398-L2401) · [OpenRazer mouse effect builders](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerchromacommon.c#L780-L830) · [Naga brightness routing](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L4444-L4451)

## Custom lighting frames

Open **Lighting → Advanced Effects**, choose a paint colour, and click a key or LED. The BlackWidow editor shows the 6×22 matrix with labels based on the US ANSI layout. Matrix cells remain accessible for other layouts. The Naga editor shows three LEDs in frame order: scroll wheel, logo, and thumb grid. A custom frame applies to the whole device. Viper saves the colours with the local profile and sends them to the device for display. [BlackWidow V2 layout overlay](https://github.com/CalcProgrammer1/OpenRGB/blob/6b681e3c3769755e20d0a89759abf38719d1d76f/Controllers/RazerController/RazerDevices.cpp#L121-L143) · [Naga LED order](https://github.com/CalcProgrammer1/OpenRGB/blob/6b681e3c3769755e20d0a89759abf38719d1d76f/Controllers/RazerController/RazerDevices.cpp#L6691-L6746)

| Device | Frame upload | Activate after all rows succeed |
| --- | --- | --- |
| BlackWidow Chroma V2 | Transaction `3F`, `03/0B`, 70 bytes per row. Header `FF row 00 15`, then 22 RGB triplets. | Transaction `3F`, `03/0A`, arguments `05 00`. |
| Naga Chroma | Transaction `3F`, `0F/03`, 71 bytes. Header `00 00 00 00 02`, then three RGB triplets and zero padding. | Transaction `3F`, `0F/02`, 12 bytes starting with `00 00 08`. |

Viper follows the OpenRazer client's upload-then-activate order. If a row fails, Viper stops the upload and retries the whole frame on the next apply. Idle dimming sends a black frame and wake restores the saved colours. The firmware path is verified by acknowledgements and emulated report checks. Visible output still needs hardware confirmation. [OpenRazer client draw order](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/pylib/openrazer/client/fx.py#L84-L93) · [BlackWidow row builder](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerchromacommon.c#L450-L470) · [Naga driver upload](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L3945-L3973)

## Button remapping

Open **Customize** to bind the controls Viper discovers from Linux input nodes and native HID report descriptors.

| Device | Physical controls | Linux sources |
| --- | --- | --- |
| BlackWidow Chroma V2 | **Macro Keys**, M1–M5 | Viper's native reader maps vendor report `04` to F13–F17 source codes. The existing evdev path also accepts F13–F17 from OpenRazer. |
| Naga Chroma | **Thumb Grid**, Thumb 1–12 | The factory number-row keys `1` through `0`, then `-` and `=`, emitted by the mouse's keyboard interface. |
| Naga Chroma | **Tilt Wheel**, Tilt Left and Tilt Right | First-byte bits 5 and 6 on the mouse interface, or usages `22` and `23` in keyboard report `04`. |
| Naga Chroma | **DPI Buttons**, DPI Up and DPI Down | Usages `20` and `21` in keyboard report `04`. |

Bindings use the same Linux source codes with either BlackWidow input path, so existing profiles still work. The Naga labels assume the factory thumb-grid outputs. [OpenRazer BlackWidow translation](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerkbd_driver.c#L5164-L5220) · [Razer Naga Chroma guide](https://dl.razerzone.com/master-guides/NagaChroma/NagaChromaOMG-ENG.pdf)

### Native BlackWidow M keys

For USB `1532:0221`, Viper looks for interface 1 bound to Linux's `hid-generic` driver with a descriptor declaring a 16-byte input report `04`. It uses the separately identified feature-report endpoint for mode control. The native reader runs while an active binding or shortcut uses an M key. After opening the reader, Viper reads the current mode using transaction `FF`, command `00/84`, then enables and verifies driver mode `03 00` using `00/04`. When the session ends, it restores regular mode `00 00` if Viper made the change. Systems exposing the M keys through OpenRazer continue to use evdev. [BlackWidow USB descriptor report](https://github.com/openrazer/openrazer/issues/1562) · [OpenRazer driver-mode startup](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/daemon/openrazer_daemon/hardware/device_base.py#L326-L334) · [Device-mode reports](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerchromacommon.c#L20-L54)

The native parser tracks only values `20`–`24` in the vendor snapshot and emits separate press and release events for M1–M5. Ordinary keys still arrive through evdev and keep their own held state. This avoids Viper replaying an M-key-only packet as a whole keyboard report, the translation pattern associated with OpenRazer's reported Q release. Emulation covers the report and mode paths. The exact descriptor and simultaneous key behavior still need physical verification. [OpenRazer held-key report](https://github.com/openrazer/openrazer/issues/2528)

### Native Naga tilt and DPI buttons

Native HID remains the primary DPI input route. The evdev fallback is available only when Linux exposes the exact DPI source codes (`0x118` and `0x119`) for this wired model. Real firmware and driver exposure of those codes remains unverified.

For USB `1532:0053`, Viper checks interfaces 0 and 1 under `hid-generic`. The mouse interface must describe a single unnumbered input report of up to eight bytes. The keyboard interface must describe a 16-byte report with ID `04`. The USB enumeration in OpenRazer's support issue identifies those interfaces and their packet limits. OpenRazer treats the remaining 15 bytes of report `04` as a snapshot of active button usages. [Naga USB enumeration](https://github.com/openrazer/openrazer/issues/187) · [OpenRazer button codes](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L6195-L6239) · [OpenRazer report dispatch](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L6409-L6479)

Viper starts read-only listeners when a binding or shortcut uses the native controls. It combines overlapping tilt holds from both reports, filters their evdev button aliases while native tilt is active, and leaves ordinary input on evdev. Emulation covers the two report formats and held-key behavior. Physical testing still needs to confirm which routes the factory firmware emits and whether a DPI press can change the firmware's sensitivity stage alongside its Viper binding.

### DPI stage changes during remapping

OpenRazer exposes live X/Y DPI for `1532:0053`. It reads `04/85` with transaction `FF`, selector `00`, and X/Y in bytes 1–4 of the reply. It sends `04/05` with transaction `FF` and the shared command builder puts `01` in the write packet, even when its caller passes `NOSTORE`. The read reports two current sensitivity values. The device-specific driver omits the stage-table interface, so a read cannot identify the active stage or tell whether firmware switched stages. [OpenRazer live DPI routing](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L2585-L2645) · [Read routing](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L2906-L2933) · [Packet builders](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razerchromacommon.c#L1211-L1240) · [Naga device attributes](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/driver/razermouse_driver.c#L6961-L6970)

Razer documents reassigning the factory sensitivity buttons in Synapse. The older OpenRazer driver-mode notes describe some mice losing DPI and tilt behavior in mode `03 00`, but the reports do not establish that the Naga Chroma both stops switching stages and keeps sending the remappable button events. The Naga Chroma's OpenRazer device class leaves driver mode off. Verifying suppression would require a device-specific assignment capture or a physical comparison of DPI values and button reports before and after changing mode. A successful mode readback alone would establish only that the mode changed. [Razer Naga Chroma guide](https://dl.razerzone.com/master-guides/NagaChroma/NagaChromaOMG-ENG.pdf) · [OpenRazer driver-mode discussion](https://github.com/openrazer/openrazer/issues/201) · [Naga device class](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/daemon/openrazer_daemon/hardware/mouse.py#L457-L497)

Performance offers **Viper-managed DPI steps** for the wired Naga Chroma. With this per-profile option enabled, DPI Up and Down move through the configured X/Y steps while Viper Relay is running. An explicit binding or shortcut takes precedence. Choose a numbered step to apply it to the inspected mouse, or set **Startup step** to choose which one the profile applies on reconnect or profile switch. The service writes live sensitivity, checks the readback after a short settling interval, and retries once after a valid mismatched reply. The Performance page reports the last confirmed step or a failure. This sequence is tested in emulation. Physical firmware timing and persistence remain unverified.

One owner of an `RZ01-0161` running firmware `1.0.3.0` reported the reassigned buttons acting as Back and Forward after Synapse 4 was closed. That suggests the device can retain some button assignments under some configurations. The report contains no USB trace, so it leaves the assignment command and the conditions for persistence unknown. [Owner's Synapse report](https://insider.razer.com/razer-support-45/razer-naga-chroma-b4-b5-remapping-broken-after-synapse-4-update-sensitivity-stage-cannot-be-overridden-91334)

MacRazer records a hardware report for live DPI and polling on `0053`. Its device entry records a failed stage-table read. OpenSnek publishes onboard button mapping evidence for later Naga models. Razer Control lists `0053` in its device database and identifies Viper Ultimate as the hardware used to validate its mapping protocol. Open Synapse limits capture-backed mapping to Viper V3 Pro. The original OpenRazer Naga Chroma support thread discusses a Synapse lighting capture, with no button assignment trace in the reviewed posts. [MacRazer device entry](https://github.com/SorcRR/MacRazer/blob/6383f6ee9dd16c8837e512a77800952eee515dfd/Sources/MacRazer/RazerDevices.swift#L371) · [OpenSnek support matrix](https://github.com/gh123man/OpenSnek/blob/19ca70d0ccaa1478d428a4de1f5f2295dd14d6ea/README.md#supported-devices) · [Razer Control mapping source](https://github.com/voidrlm/razer-control/blob/0596bb3b211912ff31dd6af0c8df8d4ba4585f78/razer_control/buttons.py#L1) · [Open Synapse mapping notes](https://github.com/1apok/open-synapse/blob/7ae69226b96c9ea750527ab45fd08b14685ecafd/README.md#L120) · [OpenRazer support thread](https://github.com/openrazer/openrazer/issues/59)

#### Capture needed to verify suppression

Capture control transfers and input reports from USB `1532:0053` only. Record firmware version, hardware revision if available, active Synapse profile, and the interface numbers. A useful trace has four parts.

1. With distinct sensitivity stages and the middle stage selected, record the factory DPI Up and Down press/release reports on all input interfaces and live X/Y DPI before and after each press. Return to the middle stage between trials. This rules out an unchanged value caused by reaching the end of the stage list.
2. In a Synapse version where reassignment works, capture changing just one DPI button from its factory action to an ordinary mouse button. Capture the restore to the factory action separately. Repeat for the other direction to distinguish the two physical button addresses. Include the entire control request and reply with the USB interface index.
3. Repeat the button and live DPI observations after quitting Synapse, then after reconnecting the mouse. Check tilt and one thumb button in each state. For a separate driver-mode experiment, record `00 00` and `03 00` mode readbacks alongside those same button and DPI observations and restore the original mode afterward.
4. Treat a repeatable host press/release with unchanged live X/Y DPI as evidence of suppression only if the same physical button changed DPI in the factory trial and the trace shows no intervening DPI rewrite. Emulated fixtures can then check the observed assignment, readback, and restore packets and replay the observed input reports.

#### Read-only DPI diagnostic

With the DPI buttons released, select the Naga Chroma's interface 0 control node from `viper-cli list` and run `viper-cli naga-dpi-watch /dev/hidrawN`. The command listens for report `04` on the same mouse's interface 1 under `hid-generic`. It sends only the live X/Y DPI GET, changes no settings, and stops with Ctrl+C.

Each edge includes the complete raw input report and a host timestamp. `before` is the most recent DPI sample available when Viper observes the press, refreshed every 250 ms when idle. `after` is a fresh GET after the press reaches the diagnostic. Sample timestamps are measured from diagnostic startup, and failed reads are marked `unavailable`. Use separate presses with a pause between them. Closely spaced presses can share a baseline or land before the preceding press's read completes. For firmware attribution, keep other DPI writers inactive and use the USB capture comparison above.

To save a capture and replay it offline, run:

```sh
viper-cli naga-dpi-watch /dev/hidrawN --capture naga-dpi.jsonl
viper-cli naga-dpi-replay naga-dpi.jsonl
```

The capture creates a new file and flushes each record as it is written. The first line identifies the device, schema version, and initial DPI sample. Each subsequent line contains one raw report that changed a DPI button, the baseline sample, and one post-press sample per decoded press. Timestamps are elapsed 100 ns ticks. Unavailable X/Y readings are stored as `null`. Replay checks the records and decodes the reports again, then prints the recorded edges and DPI samples without contacting a device or waiting between records. A session can end while a button is held.

Compare captures from the factory and remapped setups offline:

```sh
viper-cli naga-dpi-watch /dev/hidrawN --capture factory.jsonl
viper-cli naga-dpi-watch /dev/hidrawN --back-forward --capture remapped.jsonl
viper-cli naga-dpi-compare factory.jsonl remapped.jsonl
```

For the second command, assign the physical DPI Down button to Mouse Button 4 (Back) and DPI Up to Mouse Button 5 (Forward) before starting. The diagnostic opens a separate input reader on interface 0 and requires an unnumbered report whose descriptor places HID Button usages 4 and 5 in bits 3 and 4 of the first byte. Mouse captures use version 2 with `source` set to `mouse-back-forward` and the descriptor-derived `reportLength`. Replay and comparison also accept the existing version 1 factory format. Any physical button that emits those same mouse usages looks identical in the capture, so press only the two DPI controls for this test. [Razer button names](https://dl.razerzone.com/master-guides/RazerSynapse3/NagaChroma-0000083-en.pdf)

Comparison pairs the first observed DPI Up or Forward press in each capture, then the second, and so on. DPI Down or Back is paired separately. Use the same sequence of trials in both setups. The output includes the raw press and release reports, whether they match, and each press's X/Y DPI change. A missing reading leaves the DPI result unknown. A missing press means there was no corresponding entry for the selected report source. Changes in the raw mouse report can also reflect movement, scrolling, or other held buttons in that snapshot.

Use `viper-cli naga-dpi-watch /dev/hidrawN --both --capture combined.jsonl` to observe both interfaces in one read-only session. To select a different delayed-mirror window, add `--mirror-window-ms N` with a whole number from 0 to 1000. The default is 30 ms. A value of 0 disables delayed matching after release. Overlapping reports still merge, and a re-press on the same interface still counts separately even if the previous mirror remains held.

With a nonzero window, the diagnostic matches one opposite-interface press to the oldest eligible single-interface release for the same button. Each logical press triggers one live DPI read. The reports carry no shared press ID, so a separate opposite-interface press inside the chosen window can look like a delayed mirror.

Version 5 combined captures store the window as `mirrorWindowMs` and tag each raw DPI-related report by interface. Raw reports without a logical edge appear as `edges=none`. Replay and comparison use the stored window and timestamps. Version 4 retains its fixed 30 ms window, and version 3 retains its overlap-only rule. The configurable window has been verified in emulation.

To compare press counts under several windows using one combined capture, run:

```sh
viper-cli naga-dpi-windows combined.jsonl 0 15 30 60
```

The command validates the capture using its recorded rule, then counts DPI Up and Down presses from the same raw reports under each requested window. It reports the recorded counts first. Alternate rows use the current merge rules, including separate counts for same-interface re-presses while the previous mirror is still held. They contain press counts only because DPI samples were taken for the original logical presses. Replay and comparison still use the recorded rule.

Add `--details` to see exactly which reports are counted differently, even when the press totals agree. Each line gives the frame number (starting at 1 after the header), timestamp, input source, raw bytes, button, and how the recorded and candidate rules count it. The command shows the first 1000 changed classifications and reports the full count if there are more.

Export the counts and changed report details as a single JSON document with `--json`:

```sh
viper-cli naga-dpi-windows combined.jsonl 10 30 --json > window-comparison.json
```

The version 1 export contains `recorded` and `windows` press counts and a `changedReports` array. Each detail identifies a frame, button, and candidate window, with raw bytes, timestamp ticks, and booleans for the recorded and candidate classifications. A legacy overlap-only capture has `recorded.windowMs` set to `null`. `changedReportCount` counts all changed classifications, and `detailsTruncated` indicates whether the 1000-entry limit omitted any details. Invalid captures produce an error and no JSON document.

In Viper Synapse, open **Settings → General → Naga Chroma DPI comparison**. Choose **Open combined capture…** to select a local `.jsonl` made with `--both`, or try the [emulated sample capture](samples/naga-chroma-combined.jsonl). Enter candidate mirror windows in milliseconds, separated by commas or spaces, then select **Compare**. You can change the windows and compare the same file again. The viewer accepts 1–16 distinct whole numbers from 0 to 1000 and up to 100,000 capture reports. On the sample, 10 ms counts two DPI Up presses, while 30 ms counts one.

You can also choose **Open comparison JSON…** to load an existing export. Both paths show the recorded and candidate press counts above the changed reports and indicate when details were omitted. Comparison runs offline without a connected mouse or a running device service.

The changed reports can be filtered together by **Candidate window**, **DPI button**, and **Input source**. The report status shows how many exported rows match and indicates if the export was truncated. Opening a new valid file resets the filters to **All**.

Use **Search changed reports** to find an exact decimal frame number or a case-insensitive fragment of the raw report hex. Prefix a query with `0x` to search only the raw report. Search updates as you type and works together with all three filters. Clear the field to see every row that matches the filters. Opening a new valid file clears the search.

Select any changed-report column header to sort ascending, then select it again to sort descending. The arrow shows the active sort direction. Frame, time, and window sort numerically. Rows with the same sort value keep their order from the JSON export. The sort stays active as filters change or another file is opened.

Select a changed report to see its full raw hex and the recorded and candidate press classifications below the table. Choose **Copy Hex** to place the hex on the clipboard. The report stays selected when it is still visible after sorting, searching, or filtering. Opening a new comparison clears the selection.

Choose **Export changed reports CSV…** to save the rows matching the current search and filters, in their displayed order. The CSV includes the changed-report columns, including raw report hex and the recorded and candidate classifications. The export button is disabled when no rows match. A truncated JSON export provides only the rows it already contains.

## Remaining implementation and emulation targets

1. Reproduce each USB control endpoint and input interface. The Naga support report provides USB descriptors and Linux input-node listings. Keep the Naga Epic Chroma and Naga Hex V2 product IDs separate. [Naga descriptor report](https://github.com/openrazer/openrazer/issues/187)
2. Verify visible custom frame output and the remaining quick effects on physical devices. A community project reports testing custom effects on the Naga Chroma. [User-tested custom effects](https://github.com/kevchu3/openrazer-custom-effects)
3. Capture the BlackWidow native report descriptor, mode reply, and simultaneous M-key and ordinary-key behavior on physical firmware. [M-key discussion](https://github.com/openrazer/openrazer/issues/1562)
4. Confirm the Naga thumb-grid, tilt, and DPI button events on physical hardware, then verify independent X/Y DPI bounds and the available polling values. Vendor profile documentation and the OpenRazer source leave firmware remapping and slot backup unverified.

Synapse 2 cloud features retired on October 28, 2025. Razer says existing local profiles and manual exports are still available. Those vendor profiles and Viper's local remapping profiles are separate from any firmware slot support. [Razer Synapse 2 support notice](https://mysupport.razer.com/app/answers/detail/a_id/3782)
