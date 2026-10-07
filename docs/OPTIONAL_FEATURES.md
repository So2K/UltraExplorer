# Optional archive, shelf and copy-path features

Settings → View → Optional features contains three independent switches. They
start off in new and existing profiles, apply immediately, and are saved in
`workspace.json`. File dialogs keep all three features disabled.

- **Browse archives as folders**: ZIP, 7z, RAR and supported archives become
  folders on the nested canvas. Double-click a file to extract it to a temporary
  location and open it; copy/drag out to obtain real files. Context menus offer
  extraction. Writes into archives are refused. Turning it off returns archive
  files to their usual application and exits any archive folder currently in view.
- **Drop shelf**: enable the board at the right edge, then drag files onto it.
  Drag a card to take its files out, Shift+drag to arrange cards, Ctrl+wheel to
  zoom, and pull the left edge to resize. The shelf normally keeps path links;
  temporary source files are copied into its own cache. Turning it off hides
  the board and cancels previews without deleting its cards. On reactivation,
  cards older than seven days and links to missing files are pruned.
- **Copy path button**: enable a small button on hovered nested-canvas files
  and folder titles. Turning it off immediately removes its hit target.

The four PR fixes are always active: fly-in wheel grace, recovery from stale
Space-pan state, protection against replacing unreadable settings, and retention
of successful indexed search results when a secondary query times out.

Archive browsing uses unmodified, separately replaceable 7-Zip 26.04 binaries
in `archives/`. `scripts/setup-archive-tools.ps1` downloads from the upstream
release and checks the upstream SHA-256 digests before extraction. Installed
7-Zip remains a fallback. Build preparation and installation include the pinned
runtime and its upstream license; no global 7-Zip installation is required.
Source: https://github.com/ip7z/7zip/releases/tag/26.04.

The original contribution is PR #4 by whostea:
https://github.com/So2K/UltraExplorer/pull/4. Review corrections preserve settings
when the real UpdateAsync save path cannot read them, keep OLE sources alive
through shelf copies, restrict cache deletion to owned copy directories, and
protect archive output/caches and encrypted nested archive resolution.

Review qualification on the preview49de903 base: self-contained Release build
with zero warnings/errors; PR4 combined gate147/147 (including separate process
wrappers; Masked8/8, Optional18/18, ShelfDrop9/9 child totals are not added again),
adjacent workspace/selection/camera/read/split/layers/order280/280. The final
per-operation password owner correction passed the focused73/73 gate. Logs
are `artifacts/pr4-review/`. Final combined preview qualification follows after
the other agent's exact frozen commit is incorporated.

Automatic nested archive viewing has a512MiB extracted-size limit; explicit
extraction is available for larger files. Archive lists use the ordinary canvas
limits of50,000 file tiles and100,000 child folders while retaining complete
counts and extraction indexes. Whole extraction refuses case-colliding output
names; individual copies preserve both using numbered names. Local fixtures
covered ZIP/7z/tar/gzip/encryption/nested archives, including a real NTFS junction
escape refusal. RAR/multipart/ISO support uses upstream codecs and has not been
independently fixture-tested here. A symbolic-link-only fixture lacked OS
privilege; no real cloud provider state was modified. Blocking OS calls can
occupy at most two shelf background readers; file cards may initially reserve
extra room until metadata arrives. Unreadable settings stay intact and report
the save error; reopen once their file is available to resume saving.
