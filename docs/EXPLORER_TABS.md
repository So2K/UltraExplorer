# Folder windows and tabs

Every folder opening request creates an independent explorer context. Opening a
second folder does not navigate the first window away from its current folder.
The default is a separate window. In **Settings → Windows Explorer**, enable
**Open incoming folders in tabs** to add requests to the active UltraExplorer
window instead. The choice is saved for the current user and read by the folder
broker on each request. `--new-window` overrides the preference for one launch.

Each tab retains its own folder, selection, navigation history, search, canvas
camera, file list and split panes. Switching tabs keeps those objects and views
alive. Inactive content remains arranged, so a folder request can finish while
another tab is selected; inactive canvases stop their drawing frames.

The **+** button and **Ctrl+T** open a tab. **Ctrl+W** or a middle click closes
one; closing the last tab closes its window. **Ctrl+Tab** and **Ctrl+Shift+Tab**
cycle tabs, **Ctrl+1…8** selects a numbered tab, **Ctrl+9** selects the last tab,
and **Ctrl+Shift+T** reopens the last closed folder. The tab context menu also
duplicates a tab, closes the other tabs, and moves a tab left or right. Dragging
a tab reorders it. The tab strip scrolls when there are more tabs than fit.

To transfer files, drag them onto another tab and hold there for 450 ms. Its
content appears without ending the drag; move onto the desired folder or file
list and release. Files and selections can be dragged from the canvas or the
floating file list. Dropping onto a directory row uses that directory; dropping
onto a file row or empty list space uses the displayed folder. Ctrl dragging a
selected group preserves the group and requests a copy. Tab headers do not
accept file drops. The existing file
transfer pipeline handles the destination, Copy/Move modifiers, invalid and
same-folder targets, Shell progress and archive-source lifetimes. Closing a tab
waits for transfers already accepted by that context.

An observed Windows Explorer frame keeps its own source identity and stable
destination identifier. Replacement closes that frame only after the exact
folder and selection have been confirmed in the destination context. Native
foreground handover requires that the exact source frame still owns foreground;
a background frame does not take focus from another application. Win+E carries
one captured foreground identity through both broker forwarding and process
fallback, so a later foreground choice also wins during a cold start. A shell
blocked by its own modal dialog opens incoming folders in another usable window.
Tests and
diagnostic copies never activate the user's windows.

File picker dialogs keep their existing single-session behavior.

## Validation

The focused suites live in `tests/ViewAllSmoke/ExplorerTabChecks.cs`,
`FolderLaunchContextChecks.cs`, `FolderOpenPreferenceChecks.cs`, and
`WorkspaceRetryChecks.cs`. Coordinator lifetime checks live in
`tests/ExplorerReplacementSmoke/CoordinatorRetirementChecks.cs`.

Run with a dedicated temporary `ULTRAEXPLORER_STATE_DIR` and
`ULTRAEXPLORER_TEST_WINDOW=1`. The routing fixture uses its own inactive Windows
desktop; tab fixtures use a cloaked window and foreground guards. Assertions
exercise real file copies/moves, source lifetime, exact receipts, retained
state, settings persistence, native menu owners and close-time disposal.
