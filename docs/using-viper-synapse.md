# Using Viper Relay

## Find and inspect a device

1. Start on **Devices**. Wait for scanning to finish, then select a device to open its tab. If the list is empty or the service is unavailable, follow the instructions shown on the Dashboard and refresh.
2. Open **Health** to check the model, the connection, and the reason a feature is unavailable. Use **Inspect unit** to select a specific device when more than one unit appears in the same tab.
3. Use the available pages to change settings. **Customize** shows assignable controls. **Lighting** shows available zones and effects. **Performance**, **Power**, **Calibration**, and **Onboard** appear where supported. Use [Device support](device-support.md) to check coverage by model and connection.

## Create a software profile

1. Open the device tab and select **Customize**.
2. Open the **•••** menu next to the profile selector and choose **Add profile**.
3. The app selects the new profile automatically. Choose a control, select an assignment, and click **Save** in the binding editor.

The binding editor shows the profile, keymap, and layer it opened for. If a game changes the active profile, the draft stays with its original target. Use **Tab** and **Shift+Tab** to reach the controls. **Capture key** listens for one key. Escape cancels capture, and Tab moves to the next control. Use **Choose key** to assign Tab or Escape.

After saving, the device header distinguishes the saved software profile from the selected unit's firmware result. If the binding fails to save, the draft stays open and **Retry save** repeats the save. When the editor closes, keyboard focus returns to the control you edited. A separate firmware retry, when available, targets the original unit.

**Linked Games** manages profile switching for supported running applications. If you want to change settings stored on the device, open **Onboard**, confirm the target unit and slot, and use the available slot controls. Backup and restore cover the settings shown for that unit.

## Configure Naga Chroma DPI and tilt

For the wired Naga Chroma (`RZ01-0161`, `1532:0053`), open **Performance** and enable **Viper-managed DPI steps**. Use **Add step** or **Remove last step** to set up to five values and choose a **Startup step**. Enable **X-Y sensitivity** to set each axis separately. Select a numbered step to apply it now.

While Viper is running, DPI Up and Down step through the saved values and stop at the ends. Explicit DPI button bindings and applicable shortcuts take precedence. The status shows the last confirmed setting when a write fails or when the mouse later reports a different DPI. Reopen **Performance** or use **Refresh** to read the live setting.

Open **Customize** to assign the thumb buttons and tilt directions that are available on this connection. Use **Health** to check tilt input support. Tilt that arrives as wheel detents can trigger an assignment. Holding tilt for a binding or shortcut requires driver or native button press and release events.

## Assign BlackWidow Chroma V2 macro keys

For the wired BlackWidow Chroma V2 (`RZ03-0203`, `1532:0221`), use **Customize** to assign M1–M5 to a macro. The native reader keeps ordinary key events independent of macro playback. If the native reader fails to start, use **Refresh** to retry and check **Health**. When Linux exposes F13–F17, Viper can use those events as a fallback. The fallback cannot restore an ordinary key that the driver releases early. Hardware behavior remains unverified for both models. [Device support](device-support.md)

## Find help

Open **?** at the top right for the documentation library, setup instructions, and the contributor guide. Use **Copy documentation link** if your desktop does not open links. **Settings → General** shows the running build, links to its release notes, and lists stable downloads and release history separately.

[Documentation library](README.md)
