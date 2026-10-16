# UltraExplorer v1.3.1-beta.2

**Show in folder should show the file.**

When a browser or another Windows application asks to reveal a file, UltraExplorer
now selects that exact file and centres a readable card with its neighbours still
visible. Previously the camera fitted the entire containing folder, making the
target almost impossible to find in a large Downloads folder. The fix covers
fresh windows, existing windows and retained tabs. Archive browsing uses the
archive's existing visual without adding a duplicate file tile.

This beta also publishes the folder-tab and search features already tested in
local builds:

- Optional incoming-folder tabs with independent cameras, selections, navigation
  history and split panes. Separate windows remain the default.
- Tab keyboard shortcuts, drag reordering and file transfers between tabs or
  their file lists. See [folder windows and tabs](EXPLORER_TABS.md).
- A search-query builder and **Filter map** mode. The map keeps its spatial
  layout while hiding nonmatching items; clearing the filter restores the view.

The optional Windows integration and quiet update preferences remain under user
control. Nothing installs an update automatically. This prerelease keeps
**v1.3.0** as the stable latest release. Its version is higher than v1.3.0 so the
older stable build is not offered as a newer update to this beta.

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.1-beta.2/UltraExplorer-Setup-x64.exe)
- [Portable ZIP](https://github.com/So2K/UltraExplorer/releases/download/v1.3.1-beta.2/UltraExplorer-win-x64.zip): extract the whole folder before running `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.1-beta.2/SHA256SUMS.txt)

Windows 11 x64 is the tested release target. Both packages include the .NET/WPF
runtime and preview engines. The installer and portable build share the existing
local profile; they are not isolated side-by-side installations.

## Verification and limits

The reveal checks use generated folders containing 1,500 files, actual selected
card geometry and rendered selection outlines, real Windows
`SHOpenFolderAndSelectItems` calls, current-window and tab contexts, multiple
selection, narrow windows, stale requests and valid ZIP archives. Existing focus,
routing and tab checks also run. The CI workflow includes dedicated gates for
these features and the search builder/map filter. Native 3D capture checks wait for a complete decoded screenshot before accepting the rendered frame.

Development qualification checked persistent preferences, marks and global
cameras after installation. Transient folder-window contexts follow their
existing close lifetime; this is not a promise to restore every open window after
updating. The automated shell checks invoke the real Windows reveal API; they
do not exercise every browser's physical mouse gesture.

Existing format, GPU, archive and native-parser limits remain documented in the
repository. This is a beta; use the stable release if you do not want to test
these changes.
