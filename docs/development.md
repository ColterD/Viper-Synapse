# Development

Use the .NET 10 SDK and run commands from the repository root. See [Getting started](getting-started.md) for device permissions and launching the desktop app.

## Repository layout

| Project | Responsibility |
| --- | --- |
| [App](../src/ViperSynapse.App/) | Avalonia desktop UI and client for the background service. |
| [Service](../src/ViperSynapse.Service/) | Device sessions, persisted settings, profiles, and D-Bus. |
| [Core](../src/ViperSynapse.Core/) | Device catalogue, Razer report definitions, keymaps, macros, and lighting effects. |
| [Hardware](../src/ViperSynapse.Hardware/) | Linux device discovery, hidraw transport, evdev input, and uinput output. |
| [CLI](../src/ViperSynapse.Cli/) | Terminal tools for listing and probing devices. |
| [Tests](../tests/ViperSynapse.Tests/) | Automated tests for core, service, app, and hardware behavior. |

## Add a device page

1. Define the page's availability in `DeviceViewModel.BuildPages`. Use model capabilities and connection information to decide when the tab belongs in the list. Use the Health view to explain compatibility limits.
2. Create an Avalonia `UserControl` under [Views](../src/ViperSynapse.App/Views/) with a typed data context and wire it into `DeviceView.axaml` using the selected page. Use the existing Health and Lighting views for section headings, spacing, and status styles.
3. Add an emulated test for page availability and the behavior the page exposes. Cover an unsupported connection or missing interface when it changes what a user sees or can do.

## Add a documentation page

Put the Markdown file in `docs/` and add it to the [documentation library](README.md). Use the labels readers see in the app, and state the model and connection for device-specific instructions. Link prerequisites and related guides with relative paths so they work in GitHub and a local checkout.

## Run tests and build packages

```sh
dotnet test ViperSynapse.slnx --configuration Release
```

Tests that write settings to a plugged-in device require `VIPER_HARDWARE_TESTS=1`. Tests that create virtual devices through the Linux kernel require `VIPER_UINPUT_TESTS=1` and access to `/dev/uinput`. Run the kernel tests in an isolated Linux environment with udev access to the virtual event nodes. Report hardware and kernel input checks separately from headless test results.

### Private desktop smoke test

Install .NET 10, Xvfb, xauth, xdotool, D-Bus, AT-SPI (`at-spi2-core`), Fontconfig (`fc-cache`), and DejaVu fonts, then run:

```sh
tests/desktop-smoke/run.sh
```

The script starts a new X server and session bus with temporary app state. It builds a private Fontconfig cache from the installed fonts before opening the window. It uses a native Avalonia window and a daemon with emulated devices. It checks navigation at the minimum width and 150% scaling, direct access to an affected unit's Health page, save failure and retry, keyboard activation of the remap and per-key lighting controls, and unplugging during an uncommitted edit. The remap walkthrough sends native X11 keyboard input to check Tab traversal, visible focus, retry, and focus return. The headless suite covers additional behavior and protocol failures.

The smoke test reports page-switch time and managed allocation samples for synchronous device drawing. Compare samples on the same machine and configuration before using them to evaluate a rendering change. The runner also accepts build flags such as an offline restore configuration and a `DOTNET` SDK path override.

To capture the tested states, install ImageMagick and set `VIPER_DESKTOP_SMOKE_ARTIFACTS` to an absolute output directory.
For a crash during startup, set `VIPER_DESKTOP_SMOKE_BACKTRACE=1` to launch the same fixture with GDB inside its private display and D-Bus session. Use the regular invocation to check for startup failures that only reproduce outside GDB.

The private X server has no X session manager. Avalonia 12.1.3 may log `SESSION_MANAGER environment variable not defined` during startup. The fixture exercises keyboard focus directly on the private display. It does not require a desktop session or access to a user's accessibility bus.

### Release packages

`packaging/build-release.sh` builds the self-contained Linux x64 tarball and Debian package under `packaging/out`. It uses the version in `Directory.Build.props`, including its prerelease suffix. Set `VIPER_VERSION` to override the version and `VIPER_OUT` to choose another output directory. The script builds in a fresh temporary directory and copies the two packages to the destination, where an existing package of the same version is replaced.

Run `bash tests/packaging-release-safety.sh` to check that producing packages preserves existing files in the checkout and output directory. This check uses a temporary fixture and stub packaging tools.

## Review changes

Open focused pull requests against the fork’s `main` branch. For device changes, describe the specific model, connection mode, and behavior verified. Include the test command and any hardware coverage gaps.

- [Build and test](../.github/workflows/ci.yml) runs the release-configuration test suite and the private desktop smoke test.
- [CodeQL](../.github/workflows/codeql.yml) scans C# for security and quality findings.
- [Workflow lint](../.github/workflows/workflow-lint.yml) checks changes to GitHub Actions workflows.

Treat reviewer and scanner suggestions as evidence. Explain decisions when an alert conflicts with intended behavior, and record the tests or source evidence behind the decision.

[Documentation index](README.md)
