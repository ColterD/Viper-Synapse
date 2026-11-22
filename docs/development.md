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

## Run tests and build packages

```sh
VIPER_HARDWARE_TESTS=0 dotnet test ViperSynapse.slnx --configuration Release
```

The explicit hardware suite is opt-in because it writes settings to a plugged-in device. Some Linux integration tests also skip when their required kernel interfaces are unavailable. Report hardware checks separately from headless test results.

`packaging/build-release.sh` builds the self-contained Linux x64 tarball and Debian package under `packaging/out`.

## Review changes

Open focused pull requests against the fork’s `main` branch. For device changes, describe the specific model, connection mode, and behavior verified. Include the test command and any hardware coverage gaps.

- [Build and test](../.github/workflows/ci.yml) runs the release-configuration test suite.
- [CodeQL](../.github/workflows/codeql.yml) scans C# for security and quality findings.
- [Workflow lint](../.github/workflows/workflow-lint.yml) checks changes to GitHub Actions workflows.

Treat reviewer and scanner suggestions as evidence. Explain decisions when an alert conflicts with intended behavior, and record the tests or source evidence behind the decision.

[Documentation index](README.md)
