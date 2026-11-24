# Viper Relay repository guidance

- Use Viper Relay in user-facing copy. Keep the existing executable, package, settings directory, and protocol identifiers where they form a compatibility contract.
- Use the .NET 10 SDK and follow the workflows in [Development](docs/development.md). Prefer emulated devices for verification. Real hardware and kernel input tests are opt-in, and the desktop smoke test must use its private X11 display, session bus, and temporary settings.
- Distinguish saved model profiles, live readings from a selected unit, and onboard settings stored on a device. State the model and connection when documenting device-specific behavior. Label emulation and physical verification separately.
- Keep [README.md](README.md), [the documentation index](docs/README.md), and the applicable user or device guide aligned with the workflow exposed by the app.
- Set build and archive versions in `Directory.Build.props`. Use `X.Y.Z-rc.N` for release candidates and tag them as GitHub prereleases. The stable download link and AUR package follow the latest stable release. Verify CI, CodeQL, and both candidate archives before publishing.
- Keep local notes, assistant state, test artifacts, and generated release files outside commits.
