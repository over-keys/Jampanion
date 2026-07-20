# macOS builds from GitHub Actions

This is the source of truth for the Windows Codex (and any other development
machine) when a macOS package is needed. Do not build the macOS app on a local
Windows or Mac workstation. The reproducible build is the GitHub Actions
workflow:

`.github/workflows/build-macos-release.yml`

## Run an artifact-only build

The workflow is manually dispatched. An empty `release_tag` creates an Actions
artifact without changing a GitHub Release. From a machine with GitHub CLI
authentication:

```bash
gh auth status
gh workflow run build-macos-release.yml --ref agent/release-v0.3.1
gh run list --workflow build-macos-release.yml --branch agent/release-v0.3.1
gh run watch <run-id> --exit-status
```

The same workflow can be started from the Actions tab. Download the
`macOS-packages` artifact after the run succeeds. It contains:

- `Jampanion-macOS-arm64.zip` for Apple Silicon Macs
- `Jampanion-macOS-x64.zip` for Intel Macs
- `package.sha256`

To update a Release instead, enter the existing release tag in the
`release_tag` input. The workflow downloads the Windows asset for the checksum
file and replaces only the two macOS assets plus `package.sha256`.

## Required signing for public releases

An artifact-only run (an empty `release_tag`) is an engineering artifact. A
release-tagged run uses Developer ID and notarization when all signing secrets
are configured. Without those secrets it deliberately creates an ad-hoc
release, which can still be distributed when every user explicitly approves it
in macOS Privacy & Security.

When `release_tag` is set, the workflow uses these GitHub Actions secrets when
available:

- `APPLE_DEVELOPER_ID_CERTIFICATE_P12_BASE64`: base64-encoded Developer ID
  Application certificate exported from Keychain Access as a `.p12` file.
- `APPLE_DEVELOPER_ID_CERTIFICATE_PASSWORD`: password for that `.p12` file.
- `APPLE_DEVELOPER_ID_APPLICATION`: the complete signing identity, for example
  `Developer ID Application: Example, Inc. (TEAMID)`.
- `APPLE_NOTARY_KEY_P8_BASE64`: base64-encoded App Store Connect API key
  (`AuthKey_*.p8`) allowed to submit notarization requests.
- `APPLE_NOTARY_KEY_ID`: the API key ID.
- `APPLE_NOTARY_ISSUER`: the App Store Connect issuer UUID.

With the secrets, the release path signs the nested native library and app
executable with Developer ID, applies `scripts/macos/Entitlements.plist` for
.NET JIT and self-extracted native libraries, submits the ZIP to Apple's notary
service, staples the ticket to the app, and then recreates the final ZIP. It
also runs `spctl` against both macOS and generic ZIP extractions.

Without the secrets, the release remains Ad Hoc signed. To authorize that
version on macOS, try opening it once, choose Apple menu > System Settings >
Privacy & Security, click `Open Anyway` in the Security section, then confirm
`Open`. The button is available for about one hour after the failed attempt,
and the exception is saved for that exact app version. Control-clicking the app
and choosing `Open` is an alternative. This is a deliberate per-user security
override; Developer ID signing and notarization remain the recommended public
distribution method.

## Bundle layout that must not regress

The signed application must have this layout after extraction:

```text
Jampanion.app/
  Contents/
    Info.plist
    MacOS/
      Jampanion
      libfluidsynth.3.dylib
    Resources/
      Jampanion.icns
      SoundFonts/
        FluidR3_Jampanion.sf2
```

Windows-only `*.dll` files must not be copied into the macOS bundle. The
SoundFont is a resource, not executable code, and must stay under
`Contents/Resources`. `FluidSynthOutputDevice` resolves that path relative to
the app bundle and keeps a development-build fallback.

The workflow signs the complete bundle, preserves metadata in the release ZIP,
then verifies both a macOS `ditto` extraction and a generic `unzip` extraction.
It also checks the executable architecture, executable bit, nested library,
Info.plist, icon, debug-symbol absence, and developer-machine path absence.

## Startup invariants

Do not move these operations back into `MainWindow` construction or the first
UI event:

- Construct and show the window before creating `MainWindowViewModel`.
- Scan and parse the song library on a background task.
- Keep CoreMIDI enumeration off the Avalonia UI thread and allow the first
  macOS window to open without external MIDI discovery.

The old implementation performed library parsing and native MIDI discovery
before the first paint. On a large library or a slow CoreMIDI service, macOS
showed a continuously bouncing Dock icon and no window. This is a runtime
startup issue, not a .NET publish failure.

Do not add a permanent startup log to diagnose this path. If a failure is
reproduced, use the macOS DiagnosticReports entry for the exact timestamp and
fix the underlying initialization instead.

## Release validation checklist

Before handing a package to a user, confirm:

```bash
codesign --verify --deep --strict Jampanion.app
test -x Jampanion.app/Contents/MacOS/Jampanion
file Jampanion.app/Contents/MacOS/Jampanion
```

Use the architecture-specific ZIP: `arm64` on Apple Silicon and `x64` on
Intel. Do not mix the two packages.
