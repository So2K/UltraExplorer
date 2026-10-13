# UltraExplorer v1.3.0

**Select a file. Tap Space. Keep working.**

v1.3.0 is the stable release of everything tested in the five v1.3.0 betas,
plus the fixes from a full review of the code on 2026-10-13. If you use
v1.2.1, this is the next stable update.

## What is new since v1.2.1

**Quick Look and previews**

- Select a file and tap `Space` to open Quick Look. Hold `Space` and move the
  pointer to pan the canvas.
- Images zoom with the mouse wheel. PDFs and documents scroll as usual.
  Audio, video and 3D models play or render in compact viewers.
- Text files can be edited and saved right in Quick Look.
- **Open** opens the default application. Its arrow lists the other
  registered applications.
- Hovering over a file shows a small preview. You can turn this off in
  **Layers → Hover previews** or in Settings.

**New items are easy to spot**

- Files and folders added by copy, extraction or drop pulse yellow twice
  when they first become visible on the nested canvas.
- Existing contents never flash. The highlight does not change your
  selection or camera.

**Creating items and keyboard shortcuts**

- New folder and text-file dialogs show the full destination. Invalid
  Windows names are explained before anything is created.
- `Ctrl+N` creates a folder and `Ctrl+D` duplicates the selected item.

**File dialogs of other programs**

- Open/Save dialogs replaced by UltraExplorer keep the program's own options,
  for example REAPER's Save Project checkboxes and its **Format** settings.
- If anything goes wrong, the dialog goes back to Windows with your choices
  intact.

**Updates**

- UltraExplorer checks GitHub quietly, at most once a day. It downloads a
  newer version in the background at a limited speed and shows a small
  arrow in the title bar.
- Nothing is installed until you click **Install update**. A new version
  that fails to start is rolled back.
- Stable installations receive only stable releases.

**Optional tools, off by default**

- Archive browsing, the drop shelf and the copy-path button. Each can be
  turned on in Settings.

## Fixes from the 2026-10-13 review

- In folders where a program saves by renaming (Microsoft Word), new files
  stopped being highlighted after many saves. They are highlighted again.
- **Clear** on the shelf no longer removes cards another window added.
- The canvas layout is no longer replaced by an empty one when another
  program briefly holds its file at startup.
- Replacing other programs' dialogs no longer switches itself off when its
  settings file is busy for a moment.
- Files could show wrong icons when graphics memory ran short. They no
  longer do.
- Video and 3D preview engines now close together with UltraExplorer, so
  a USB drive can be ejected.
- Updating from inside the app refuses to run as administrator and points
  to the installer instead.
- Smaller fixes:
  - colours and notes survive a move that fails part of the way;
  - **Back** history stays correct after quick repeated presses;
  - search without Everything finds folders in a drive root;
  - archive temporary files that you edited are not deleted.

The full list is in
[the review](https://github.com/So2K/UltraExplorer/blob/v1.3.0/docs/REVIEW_2026-10-13.md).

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0/UltraExplorer-win-x64.zip): extract the complete folder and run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0/SHA256SUMS.txt)

Both packages include .NET/WPF and the preview engines. Your settings, notes
and colour tags in `%LOCALAPPDATA%\UltraExplorer` are kept when updating.

## Testing and limits

- Release builds of the app and all test projects finish with zero warnings
  and zero errors.
- All CI check sets pass, including the review's new checks.
- The full headless suite passes **5274/5277** checks. The three failures
  also happen on v1.3.0-beta.5 before the review fixes. They come from the
  test machine, not from a regression:
  - recursive duplication of a folder tree beyond the Windows Shell path limit;
  - a real Everything query that got no reply on the test machine;
  - one light-reveal canvas check.
- The native dialog checks against real applications run only on a
  secondary monitor and were last run for beta.5 (796/796). The installer
  scripts have not changed since beta.5.

Windows 11, x64.
