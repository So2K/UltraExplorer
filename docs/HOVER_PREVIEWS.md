# Hover previews

Universal content providers and the compact full-view/editor are described in
[UNIVERSAL_PREVIEWS.md](UNIVERSAL_PREVIEWS.md). PDF, text/document content, F3D
models and mpv media now have providers independent of Windows' thumbnails;
unknown readable files get content/hex information. The original scheduling,
case/literal-path/cache/input rules below still apply.

Hover a file for 220 ms to see a passive content thumbnail. Selection is
independent: hovering one of several selected files previews that particular
file without opening it or changing the selection. This works on the nested
canvas, tree canvas and folder list, including either pane of a split view.

**Layers → Hover previews** and **Settings → Hover previews** control the same
preference. It is enabled by default and remembered in `workspace.json` as
`ShowHoverPreviews`. Existing workspaces retain the enabled default. The
preference saves independently across windows and prepared file dialogs.

The card uses the existing dark palette, preserves image proportions, shows
the name and original image dimensions when available, and stays inside the
window. It does not capture mouse or keyboard input. Presses, scrolling,
camera motion, navigation, menu opening, leaving the item, switching the view
or closing the window dismiss it. Unsupported files keep their existing
information tooltip.

## Loading and resource limits

- Only a settled hovered file is requested. There is no folder-wide thumbnail
  scan or synchronous content/metadata read on the UI thread.
- Two background STA workers, at most 32 queued files, newest hover first.
  Cancelled queued requests are removed; old answers cannot replace a newer
  target. A loading card times out after 3 seconds.
- Frozen, detached 32-bit thumbnails are at most 512 pixels on their longest
  edge. The LRU holds at most 96 entries and 32 MiB of image pixels, including
  negative entries with a 5-second retry interval. Positive entries expire
  after 30 seconds. File length, write time and
  attributes are revalidated in the background, including cache hits.
- WPF/WIC decodes common images, with all eight EXIF rotations/reflections.
  Other formats use Windows' content thumbnail cache with
  `WTS_REQUIRESURROGATE`; support depends on installed thumbnail providers.
  File type icons are never presented as content previews. Borrowed Shell
  bitmaps are detached before their owned handles are released.
- Offline, recall-on-open, recall-on-data-access and leaf reparse files are
  skipped. Merely hovering does not hydrate cloud placeholders or follow a
  leaf symlink. Missing, locked or corrupt files yield no preview.
- Disposal cancels waiters without joining a blocked disk/native worker.

Hover identities are case-exact, including Windows case-sensitive directories.
Leaf and parent names with trailing dots/spaces use their literal Windows
representation; the service does not silently preview a similarly named file.
The bounded cache describes retained thumbnail pixels, not a universal bound
on a codec's internal decoding memory. Some non-JPEG/PNG WIC codecs can decode
the full source before scaling. Shell thumbnails also depend on Windows'
cache/providers; freshness after a replacement with identical metadata is not
guaranteed by the service's own expiry alone.

API references: [Windows thumbnail cache flags](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/ne-thumbcache-wts_flags),
[IThumbnailCache](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nn-thumbcache-ithumbnailcache),
[Windows thumbnail providers](https://learn.microsoft.com/en-us/windows/win32/shell/thumbnail-providers).

## Verification

The focused harness requires isolated state and test-window mode. Publish it
with `-p:IsPublishable=true --self-contained true` when the desktop runtime is
not installed, then run separate processes for these groups:

```powershell
$env:ULTRAEXPLORER_STATE_DIR = 'C:\temp\UltraExplorer-hover-test'
$env:ULTRAEXPLORER_TEST_WINDOW = '1'
ViewAllSmoke.exe --only ThumbnailServiceChecks,HoverPreviewChecks
ViewAllSmoke.exe --only HoverPreviewPreferenceChecks
ViewAllSmoke.exe --only HoverIntegrationChecks
ViewAllSmoke.exe --only SettingsChecks
```

The thumbnail checks read the requested three PNGs in
`J:\Granny2Work\church\statueraw` without modifying them. Window checks use a
cloaked, nonactivated real WPF window and owned fixtures, preserving the exact
two-file selection while previewing its second item. They exercise same-path
handoff between panes, captured-tree subscription cleanup, placement,
immediate disable, and a routed mouse move from a generated list row.

Set `HOVER_PREVIEW_SHOTS` to save rendered cards. `HOVER_REAL_PREVIEW` optionally
names a real file to render read-only. These screenshots use an injected hover
target and are named `*-simulated-hover.png`; they are not evidence of physical
pointer automation.

Release build and focused checks are recorded in the feature's coordination
handoff. Two old selection timing budgets also fail on unchanged base
`6c0f738` on this machine; neither source nor thresholds were changed for this
feature. The current deployment candidate is based on the qualified v1.2.1
product and preserves its smooth F, Tags, selection, picker and file-operation
fixes. Its new combined checks must pass before replacing the installed copy;
historical isolated-feature totals are not a claim about this later candidate.
