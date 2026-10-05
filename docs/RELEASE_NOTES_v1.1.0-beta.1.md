# UltraExplorer v1.1.0-beta.1

**Your files. One zoomable map.**

This Windows x64 prerelease brings color-tag navigation to UltraExplorer's
zoomable folder map. Mark a folder with a color and reach it from **Tags** in
the navigation pane, or use its mark as a visual landmark on the canvas.

## Color Tags

- Folders with a non-default color appear below **Network** in the sidebar.
- Click an entry to reveal that exact folder.
- Right-click for **Change color** or **Unpin**. Unpin clears the color while
  retaining the folder's note.
- `Shift+F10` and the Menu key open the same menu.
- Same-name folders include their parent location so entries can be distinguished.
- Colored files are excluded from the folder Tags list.
- Colors and notes remain shared between app windows and persist across restarts.

## Explore the map

Each drive is a root, with folders and files nested inside it. Double-click
to enter a folder, zoom out for context, paste a path to jump there, or reveal
a global search result on the canvas. The Windows shell keeps familiar address
navigation, file operations and context menus around the map.

The native C#/.NET 10 app uses WPF and a Direct3D 11 nested-canvas renderer,
with a CPU fallback. Split view lets two independent cameras work side by
side or top to bottom.

See the [project page](https://github.com/So2K/UltraExplorer#see-it-in-action)
for navigation, search, Tags and split-view demos.

## Download

- **UltraExplorer-Setup-x64.exe**: installer with Start menu entry and uninstaller.
- **UltraExplorer-win-x64.zip**: portable build; extract and run `UltraExplorer.exe`.

Both include the .NET runtime. Requires Windows 10 1903+ / Windows 11, x64.
Preferences, favorites, colors and notes live in `%LOCALAPPDATA%\UltraExplorer`.

## Before trying it

- [Everything](https://www.voidtools.com/) is recommended for indexed global
  search. Without it, UltraExplorer walks directories in the background.
- A camera jump does not remove filesystem latency: a cold folder or offline
  network share still takes time to read.
- Local filtering covers loaded branches; global search is the way to find an
  unvisited path. Very large canvas listings can be capped.
- **View all** is available; **View select** remains disabled.
- Windows folder/dialog integration and the separate `Win+E` preference are
  optional and off on fresh installations. Unsupported locations and dialogs
  retain their original Windows provider. See the
  [integration guide](https://github.com/So2K/UltraExplorer/blob/main/docs/EXPLORER_REPLACEMENT.md).

## Feedback

This is a beta release. For a useful bug report, include the app version,
Windows version, exact steps, expected result and actual result. Mention
whether the path is local, removable or networked, and whether Everything or
Windows integration is enabled. A small reproducible case helps most.

[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) ·
[Build and test](https://github.com/So2K/UltraExplorer/blob/main/BUILD.md) ·
[Русский](https://github.com/So2K/UltraExplorer/blob/main/docs/README.ru.md)
