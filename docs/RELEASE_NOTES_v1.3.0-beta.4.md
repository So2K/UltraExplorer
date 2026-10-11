# UltraExplorer v1.3.0-beta.4

**Save options, clearer creation dialogs and keyboard shortcuts.**

- New folder and text-file dialogs show the full destination before creating
  anything. Invalid Windows names are explained beside the name field.
  `Ctrl+N` creates a folder in the selected destination; `Ctrl+D` duplicates the
  selected item. Existing Shift shortcuts remain available. Typing in a text
  field keeps its normal keyboard behavior.
- REAPER's Save Project options now appear in the replacement dialog, including
  the embedded checkboxes and fields. **Format** returns to the application's
  native Save dialog and opens its own settings, preserving the chosen name,
  destination and options. That Save dialog stays native for the operation.
- A file dialog whose name cannot be read immediately waits for the complete
  dialog before replacement. The chosen name and destination remain protected.
- The October 9 review fixes preserve edited archive copies, shelf items and
  marks shared between windows, and newly copied clipboard contents during a
  long move. They also improve pane focus, Quick Look text state, watcher and
  icon cleanup, and recovery after a cancelled Windows shutdown.
- Installer upgrades preserve the original user's integration settings when
  administrator credentials belong to another account. From beta.4 onward,
  quiet updates retain the old version until the new application reports a
  successful start, with rollback on an early failure. The first update from
  beta.3 still uses beta.3's updater. Installation requires **Install update**.

Space Quick Look, quick text notes, media/3D previews and optional archive,
shelf and copy-path tools from beta.3 remain included. The optional tools still
start off.

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.4/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.4/UltraExplorer-win-x64.zip): extract the complete folder and run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.4/SHA256SUMS.txt)

Both packages include .NET/WPF and the preview engines. They use the existing
local settings profile. This prerelease leaves v1.2.1 as the stable latest
release.

## Testing and limits

Creation and review checks use disposable files and isolated state. The native
Save checks use owned Windows dialogs on a secondary monitor. The real REAPER
dialog was previously inspected read-only; no user project was saved or changed by testing.
These checks do not imply every application-specific dialog is supported.

Focused creation/dialog/review checks pass **205/205**, with the isolated
creation child reporting **38/38**. The final focused native cases pass
**204/204**. Setup identity fixtures pass **30/30** in both PowerShell 5 and 7;
they use disposable profiles and do not exercise an actual UAC account switch.

The broad desktop run reported **5197/5201**: the retained graph reference,
recursive long-folder duplication and hover-preference check failures, plus
a live Everything timeout that passed separately. This is not a fully green
broad total. Windows Shell's recursive long-folder duplication limit, existing
graph-refresh and preview/GPU limits and protected-folder update restrictions
still apply. Archive extraction refuses a destination that
is itself a junction, and legacy Open dialogs accept multiple files only from
one folder. Updates use GitHub's checksums and do not have an independent
signing key. An all-users installation keeps per-user folder/dialog integration
disabled.

[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) with repeatable
steps and screenshots with private names hidden. See the
[October 9 review](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.4/docs/REVIEW_2026-10-09.md)
and [quiet update behavior](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.4/docs/QUIET_UPDATES.md)
for the detailed qualifications.
