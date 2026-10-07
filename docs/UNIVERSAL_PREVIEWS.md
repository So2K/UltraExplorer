# Universal previews and Quick Look

Hover over a file for its compact content preview. Press **Ctrl+Space** on the
hovered or focused file, or choose **Quick Look** in its menu, for a resizable
preview window. Escape or Ctrl+Space closes it. The canvas's existing Space
pan gesture stays available.

- Images: large image, Fit, zoom buttons and Ctrl+wheel zoom; EXIF orientation.
- PDF: real rasterized pages, previous/next, Left/Right keys and zoom, without
  depending on an installed Explorer PDF handler.
- 3D: F3D portable reader plugins, native interactive orbit/zoom/pan viewport,
  camera reset and snapshot controls. Formats include OBJ/STL/GLTF/FBX/USD,
  common CAD and scientific formats supported by the pinned distribution.
- Media: mpv native player, starts paused, local playback controls, seeking,
  volume and mute. Closing/switching stops the owned player.
- Text/code: AvalonEdit syntax highlighting, line numbers and Ctrl+F search.
  **Edit text** enters editing; **Save / Ctrl+S** commits. Original encoding
  and line endings are preserved; source changes from another program reject
  the save. Unsaved edits are confirmed before closing the preview or owner.
- DOCX/XLSX/PPTX/OpenDocument/RTF/EPUB: read-only extracted text/cells. ZIP:
  entry listing without extraction. Rich document layout is not reproduced.
- Other regular files: bounded actual text or metadata and hexadecimal header.
  Unavailable, locked/cloud-only and malformed content has an explanatory
  state and **Open in app** remains available.

Plain-text editing is limited to complete files up to 2 MiB. Partial/binary
and extracted Office documents are read only. Legacy binary Office files,
unsupported codecs and encrypted PDFs may use information/hex fallback;
there is no claim that every proprietary format can be decoded. Hover loads
have a 3-second UI deadline; PDF/native processing is bounded/concurrency
limited but native parser internals are not universally preemptible.

## Build and packaging

Run `pwsh -File scripts/setup-preview-tools.ps1` once before building. It downloads
portable F3D 3.5.0 and mpv 20261006 with pinned SHA256 checks; no global installation,
PATH or associations. The app copies `runtime/preview/` to output/publish `preview/`
alongside the original licenses. `install.ps1` and the release workflow prepare
missing engines automatically. PDFium 157.0.8086 and AvalonEdit 6.3.1.120 are pinned
NuGet dependencies. Keep the full publish folder together.

See [PREVIEW_TOOLS.md](PREVIEW_TOOLS.md) and
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for upstream sources/licenses.

## Verification

Build the Release smoke harness with `-p:SelfContained=true`. Set a fresh
`ULTRAEXPLORER_STATE_DIR` and `ULTRAEXPLORER_TEST_WINDOW=1`, then run groups
`DocumentPreviewChecks`, `UniversalThumbnailChecks`, `QuickPreviewChecks`,
`PreviewToolsChecks`, and existing hover/preference/focus checks. New fixtures
include a real two-page PDF, actual encoded text and Office packages, generated
AVI/OBJ and native child viewport IPC/lifetime checks. Screenshots are written
under `artifacts/universal-preview/shots/`; headless layout/injected fixtures
are distinct from physical pointer verification.
