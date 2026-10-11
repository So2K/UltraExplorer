# UltraExplorer v1.3.0-beta.5

**See where newly added files and folders appear.**

- Files and folders added by extraction, copy or drop pulse yellow twice when
  their own readable tiles enter the nested canvas viewport. Each split pane
  keeps its own pending feedback.
- Offscreen, hidden, filtered or too-small entries wait until they can be
  drawn. The highlight follows the item's position and keeps the current
  camera and selection unchanged.
- The feedback uses the existing canvas overlay over both GPU and CPU
  rendering. Once the two pulses finish, the canvas returns to its normal
  idle frame behavior.
- Opening an unread folder establishes its baseline. Existing contents,
  unchanged refreshes, metadata changes and known renames do not become new
  arrivals. A newly added item renamed before it becomes visible keeps its
  pending feedback under the new name.

The [beta.4 Save options, creation shortcuts and update fixes](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.4/docs/RELEASE_NOTES_v1.3.0-beta.4.md)
remain included, along with Space Quick Look, text notes and compact media/3D
previews. Optional archive browsing, the shelf and copy-path tools still start
off.

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.5/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.5/UltraExplorer-win-x64.zip): extract the complete folder and run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.5/SHA256SUMS.txt)

Both packages include .NET/WPF and the preview engines and use the existing
local settings profile. This is a beta-channel update; installation still
requires **Install update** or running the downloaded installer.

## Testing and limits

Arrival checks use disposable directories, isolated settings and manual CPU
canvas frames. They cover real directory-watch notifications reaching the
automatic refresh and actual yellow overlay, two visible pulses, deferred
visibility, independent panes and unchanged selection/camera state. Tests do
not copy or extract files into a user's workspace.

Arrival feedback applies to the nested canvas. The separate tree canvas and
folder list keep their existing behavior. A folder needs a complete known
listing to distinguish new entries; first or incomplete listings establish a
baseline. Pending feedback is bounded, and a rename burst whose pairs exceed
the watcher limit can conservatively omit feedback for that refresh.

The [previous release's qualifications](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.4/docs/RELEASE_NOTES_v1.3.0-beta.4.md#testing-and-limits)
still describe the existing Windows Shell, tree-refresh, GPU and protected-folder
update limits. A new beta label does not claim that every broad desktop or
timing-sensitive check passes.

[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) with repeatable
steps and screenshots with private names hidden.
