# Optional archive, shelf and copy-path features

Settings → View → Optional features contains three independent switches. They
start off in new and existing profiles, apply immediately, and are saved in
`workspace.json`. File dialogs keep all three features disabled.

- **Browse archives as folders**: ZIP, 7z, RAR and supported archives become
  folders on the nested canvas. Double-click a file to extract it to a temporary
  location and open it; copy/drag out to obtain real files. Context menus offer
  extraction. Writes into archives are refused. Turning it off returns archive
  files to their usual application and exits any archive folder currently in view.
  Space or Quick Look on an entry opens an extracted copy as a read-only preview
  with its archive origin displayed. Editing, autosave and Delete are disabled
  there; Open/Open with deliberately open the temporary copy. Passive hover
  never extracts archive contents. Switching to Tree view treats archives as
  ordinary files; returning to Nested restores the saved ON preference.
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
Extracted files also inherit the archive's Windows origin metadata
(`Zone.Identifier`), including cached and nested extraction. A known origin
which cannot be preserved fails extraction instead of reporting an unmarked
output as ready to open.

The combined source includes the complete lightweight preview commit74dd485,
with the Space/copy-path conflict resolved using one press-time Space snapshot.
Self-contained Release builds have zero warnings/errors. Final verification:

| Gate | Result |
| --- | --- |
| PR4 and adjacent workspace/camera/selection/read/split/layer/order regressions | 429/429 |
| Preview, text save, image, Space/pan, selected-item focus and hover integration | 174/174 |
| Settings | 308/308 |
| Actual embedded F3D/mpv controls and lifecycle | 25/25 |
| Tags | 35/35 |
| Manifest-safe update/rollback | 47/47 |
| Picker contracts and panes | 212/212 |
| New critical integration gate | 22/22 root checks |

The critical gate includes separate process wrappers: archive backend31/31,
actual read-only archive window9/9, owner-close/system-end recovery17/17,
layout10/10 and optional switch18/18; these child counts are not added again
to the root total. Its origin-metadata checks are17/17 in-process. Source logs
are retained under `artifacts/pr4-review/`, including original failures and
the corrected fixtures. No full-suite all-green claim is made.

Automatic nested archive viewing has a 512 MiB extracted-size limit; explicit
extraction is available for larger files. Archive lists use the ordinary canvas
limits of 50,000 file tiles and 100,000 child folders while retaining complete
counts and extraction indexes. Whole extraction refuses case-colliding output
names; individual copies preserve both using numbered names. Local fixtures
covered ZIP/7z/tar/gzip/encryption/nested archives, including a real NTFS junction
escape refusal. RAR/multipart/ISO support uses upstream codecs and has not been
independently fixture-tested here. A symbolic-link-only fixture lacked OS
privilege; no real cloud provider state was modified. Blocking OS calls can
occupy at most two shelf background readers; file cards may initially reserve
extra room until metadata arrives. Unreadable settings stay intact and report
the save error; reopen once their file is available to resume saving.
