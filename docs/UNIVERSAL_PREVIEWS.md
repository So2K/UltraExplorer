# Universal previews and Quick Look

Hover over a file for its compact content preview. **Select a file and tap Space**
to open Quick Look: a release before 200 ms previews the selected file. Holding
Space and moving the pointer pans immediately, even without a mouse button;
Space+drag also works, without waiting 200 ms or a drag threshold.
Any mouse/wheel gesture, changed selection, pane or focus cancels the preview tap.
**Ctrl+Space** and **Quick Look** in the file menu remain available. Escape or
Space closes the preview, while editable fields keep their normal typing.

- Images: large image, Fit, zoom buttons and Ctrl+wheel zoom; EXIF orientation.
  Common formats use Windows WIC. Magick.NET, the image engine used by ImageGlass,
  adds WebP/AVIF/HEIC/JXL/PSD/TGA and other formats without a separate viewer process.
- PDF: real rasterized pages, previous/next, Left/Right keys and zoom, without
  depending on an installed Explorer PDF handler.
- 3D: F3D portable reader plugins, native interactive orbit/zoom/pan viewport,
  a compact Fit/reset control. Formats include OBJ/STL/GLTF/FBX/USD,
  common CAD and scientific formats supported by the pinned distribution.
- Media: mpv decoding/playback engine, starts paused; compact play/pause, Stop,
  timeline with actual frames while scrubbing, speed, volume and mute. Closing/switching stops the owned player.
- Text/code: a lightweight AvalonEdit buffer with syntax highlighting and Ctrl+F.
  Plain text is immediately editable. Escape, closing or switching files saves
  the note in its original encoding, BOM and line endings. Ctrl+S saves now.
  A conflicting external change or failed write keeps the note open, with an
  explicit discard action; it never replaces a newer file silently.
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

**Open** always launches the default application; its arrow offers **Open with**
so you can choose another program. Previous/next file buttons and arrow keys
browse the cached current folder. Text fields keep their editing keys;
Alt+Left/Right also changes files. **Delete** moves the current file to the
Recycle Bin, then shows the next file when available.

The preview contains only the small viewer/editor interface. Whole ImageGlass
and Zed applications are not included. Zed and its experimental GPUI embedding
were investigated as potential cores; the final text preview uses the small
ready WPF editor to avoid a separate IDE startup.

## Build and packaging

Run `pwsh -File scripts/setup-preview-tools.ps1` once before building. It downloads
portable F3D 3.5.0 and mpv 20261006 with pinned SHA256 checks; no global installation,
PATH or associations. The app copies `runtime/preview/` to output/publish `preview/`
alongside the original licenses. `install.ps1` and the release workflow prepare
missing engines automatically. PDFium 157.0.8086 and AvalonEdit 6.3.1.120 are pinned
NuGet dependencies. Keep the full publish folder together.

Magick.NET-Q8-x64 14.17.2 is a lazy native decoder dependency. Implementation,
source inspection and limits are recorded in [IMAGE_PREVIEW_RESEARCH.md](IMAGE_PREVIEW_RESEARCH.md),
[ZED_PREVIEW_RESEARCH.md](ZED_PREVIEW_RESEARCH.md) and [PAN_SMOOTHING_RESEARCH.md](PAN_SMOOTHING_RESEARCH.md).

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
