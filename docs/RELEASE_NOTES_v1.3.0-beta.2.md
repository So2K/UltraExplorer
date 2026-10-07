# UltraExplorer v1.3.0-beta.2

**Quiet update checks. Installation when you choose.**

This beta retains the lightweight previews and optional tools from
[v1.3.0-beta.1](https://github.com/So2K/UltraExplorer/releases/tag/v1.3.0-beta.1)
and adds the quiet GitHub update mechanism.

The normal explorer waits 30 seconds after starting before checking. It checks
at most once every 24 hours, downloads a newer compatible package quietly with
a 512 KiB/s limit, and shows a tiny ring followed by a blue arrow in the top-left
title bar. There are no automatic update popups, banners or installation.

Click the arrow to see the version. **Install update** verifies the cached
package, closes the application normally, updates its current writable folder
and restarts it. **Later** keeps the current version. Turning off
**Settings → About → Receive updates** cancels checking and downloading and
hides the indicator. Existing settings, notes and color tags are preserved.

The updater runs only while the normal explorer is open. It adds no service,
scheduled task, sign-in entry or telemetry. Its installation helper runs only
after the user's explicit click. Stable installations stay on stable releases;
betas can receive newer beta or stable releases.

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.2/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.2/UltraExplorer-win-x64.zip): extract the complete folder and run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.2/SHA256SUMS.txt)

Both packages include .NET/WPF and the preview engines. Versions released before
this updater was added need this installer or portable package once to obtain
the feature. This prerelease keeps v1.2.1 as the stable latest release.

## Limits and feedback

The preview/format and archive limits from beta.1 still apply. For protected or
read-only application folders, use the ordinary installer; the quiet updater
refuses file replacement before closing windows. A refused close or note-save
conflict keeps the existing version and buffer. Recovery waits for a stalled
integration operation rather than forcing application processes to exit.

See the [update behavior and verification](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.2/docs/QUIET_UPDATES.md)
and [beta testing guide](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.2/docs/BETA_TESTING.md).
[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) with repeatable
steps and private names removed.
