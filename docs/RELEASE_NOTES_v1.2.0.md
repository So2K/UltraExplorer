# UltraExplorer v1.2.0

**Your files. One zoomable map.**

v1.2.0 brings the saved review work into the native Windows file manager:
18 work packages, plus cross-file fixes for navigation, selection, file
commands, shared marks and optional Windows integration. It is published
without a beta label. The stable-channel designation is a release decision,
not a claim that every test passes or every review finding is closed.

## Highlights

- **Safer file actions.** An externally deleted folder no longer makes the
  list select its parent for the next Delete. External drops negotiate an
  asynchronous handoff when supported; other sources remain held until the
  actual copy finishes, not through the later folder refreshes. Modern Windows
  `IFileOperation` handles copy, move, delete and duplicate.
- **More consistent navigation.** Folder forwarding can happen before WPF
  startup, windows restore before the folder read finishes, and arrow-key
  navigation keeps its target framed. Explorer handoffs retain exact source
  identity and selection, retire abandoned destinations and preserve windows
  the user has already used.
- **Marks and visibility stay together.** Tags metadata and shared notes are
  preserved through the integration. Rename tracking carries the camera to
  the renamed branch; hidden-folder changes update local filters. Explicit
  Hide from canvas and passive synchronization keep distinct intent.
- **Less unnecessary work.** Per-volume read lanes, cached beacon placement,
  icon-arrival scoping and reused search rows reduce redundant work. Clipboard
  preparation and supported file-command disk operations run off the UI thread.
- **Independent windows and panes.** Split transfers reject duplicate key
  repeats, stale list rows cannot act under a new folder title, and prompts
  disable only their owner. Picker freshness and handoff ordering preserve
  native input and the Windows fallback.

Color **Tags**, notes, favorites, split view, global search and the native
Windows context menu remain available. **View all** is implemented;
**View select** remains reserved and disabled.

## Download

Windows 11, x64. Both assets include the .NET runtime:

- [UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.2.0/UltraExplorer-Setup-x64.exe): installer, Start menu entry and uninstaller.
- [UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.2.0/UltraExplorer-win-x64.zip): extract and run `UltraExplorer.exe`.

Preferences, favorites, colors and notes live in `%LOCALAPPDATA%\UltraExplorer`.
Folder/dialog integration and the separate `Win+E` preference are optional
and off on fresh installations. Unsupported dialogs and locations can stay
with Windows; the dialog footer offers **Use the Windows dialog this time**.

## Validation and known limits

The combined self-contained Release build completed with **zero warnings and
zero errors**. Its serial broad harness reported **4445/4447**, exit code 1,
with all 46 production JSON files byte-identical before and after. Child and
substring-selected groups overlap, so that total is not 4447 unique scenarios.
The two retained failures are disclosed below, not waived.

- **Recursive long-folder duplicate.** Windows Shell rejects a destination
  tree beyond its limit with `COPYENGINE_E_PATH_TOO_DEEP_DEST` (`0x8027001E`).
  Covered long-path file copy/move/delete/duplicate and folder deletion checks
  pass, but recursive duplication is not universally supported. Known long
  source/destination/result paths receive a clear error without the blocking
  native error dialog. Shorten names or move the tree closer to a drive root.
- **Returned node identity after refresh.** A concurrent ancestor refresh
  can replace the node object returned by a reveal. Exact path resolution and
  the final live active/selected node pass the reproduction, but whether the
  returned reference was already stale at the exact method return still needs
  an owning-dispatcher diagnostic. The intermittent assertion remains failing.

Focused checks also cover Explorer handoffs, file commands, Tags compatibility,
visibility/rename behavior, split panes, native dialog fallback and live-update
startup. Their results do not replace the broad-suite result above. Details
are in the [integration checkpoint](REVIEW2_INTEGRATION.md).

GPU presentation still synchronizes on the shared UI thread. A nonblocking
texture pipeline, deferred saved-expansion replay and identity-preserving large
paged refresh are separate follow-ups, not changes promised by this release.
Third-party Shell extensions can still block after reachability preflight.
The local filter covers loaded branches; very large canvas listings can be
capped. Cold folders and offline shares still depend on filesystem latency.

## Demos and feedback

The [project page](../README.md#see-it-in-action) retains the six existing
30 fps APNG demonstrations and GIF alternatives. They use Windows Graphics
Capture, the CPU fallback and a preloaded, generated workspace, with elapsed
frame timestamps; the search demo uses a scoped real directory walk.

[Same folder, two views](../README.md#same-folder-two-views) shows native
Explorer's static Details view on the left and UltraExplorer's animated canvas
on the right, on the same 12-level generated destination. It is not a timed
folder-walk race or a disk/GPU benchmark.

For a useful report, include v1.2.0, the Windows version, exact steps, expected
and actual results, and whether the path is local, removable or networked.
Mention Everything and Windows integration if enabled.

[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) ·
[Build and test](../BUILD.md) · [Русский](README.ru.md)
