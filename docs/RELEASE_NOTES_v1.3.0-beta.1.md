# UltraExplorer v1.3.0-beta.1

**Select a file. Tap Space. Keep working.**

This Windows x64 beta adds compact file previews and quick text editing to
UltraExplorer's zoomable map. It also brings three optional tools: archive
browsing, a drop shelf and a copy-path button. The smooth `F` focus, Tags,
file-operation and Windows-dialog fixes from v1.2.1 remain included.

## Quick Look and movement

- Select a file and tap `Space` to open Quick Look. A tap released before
  200 ms opens the selected file, even when the pointer is over another item.
- Hold `Space` and move the pointer to pan immediately. `Space+drag` also works.
  Releasing Space stops that movement.
- `Ctrl+Space` and **Quick Look** in the file menu remain available.
- Use **Open** for the default application or its arrow for **Open with**.
  Previous/next buttons browse files already loaded in the current folder.
- For ordinary files, **Delete** moves the current file to the Recycle Bin.

Hover previews are enabled by default. They show content without changing
selection; **Layers → Hover previews** or Settings can turn them off.

## Preview, edit and save

- Images have fit and zoom controls, orientation handling and expanded format
  decoding through Magick.NET, including WebP, AVIF, PSD and TGA.
- PDFs show rendered pages with page navigation and zoom.
- F3D provides an interactive 3D viewport with a compact Fit/reset control.
- Media starts paused, with play/pause, Stop, a seek timeline, speed and volume.
- Complete plain-text files up to **2 MiB** open ready to edit. `Ctrl+S` saves
  now; closing, pressing Escape or switching files saves in the original
  encoding, BOM and line endings. An external-change conflict or failed write
  keeps the buffer available instead of silently replacing newer content.
- Supported Office and OpenDocument files show read-only extracted text or
  cells. Other readable files can show information and a hexadecimal header.

## Three optional tools

Open **Settings → View → Optional features**. Each switch is independent,
remembered and **off by default**. File dialogs keep these tools disabled.

- **Browse archives as folders** opens supported archives on the nested
  canvas using bundled 7-Zip 26.04. Copy, drag or extract entries out to get
  ordinary files. Quick Look explicitly extracts an entry to a temporary,
  read-only copy and shows its archive origin. Entries cannot be edited,
  overwritten or recycled inside the archive; hovering does not extract them.
- **Drop shelf** adds a board at the right edge. Drag files onto it, then drag
  cards out when needed. Turning it off hides the board while retaining cards
  for up to seven days. Ordinary cards link to their source paths; temporary
  sources are copied into the shelf's cache.
- **Copy path button** adds a small button on hovered files and folder titles
  in the nested canvas.

Archive review tightened extraction paths, case-sensitive entry handling,
cache publication, password ownership and propagation of downloaded-file
origin metadata. Turning archive browsing off or changing to the tree canvas
leaves virtual archive locations safely. Search timeout, unreadable-settings
and Space-pan recovery fixes are included too.

Thanks to [whostea's contribution](https://github.com/So2K/UltraExplorer/pull/4)
for the optional tools and related fixes.

## Download

Windows 11, x64. Both packages include the .NET/WPF runtime and preview engines.

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.1/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.1/UltraExplorer-win-x64.zip): extract the entire folder, then run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.1/SHA256SUMS.txt)

The installer and portable build use the same local profile for preferences,
favorites, colors and notes. Optional Windows integration keeps its separate
preferences. This prerelease leaves [v1.2.1](https://github.com/So2K/UltraExplorer/releases/tag/v1.2.1)
as the stable release.

## Limits and feedback

This beta is for real-world feedback on the combined preview and optional-tool
workflows. Support varies by file and decoder; it does not promise that every
format or desktop interaction has been tested.

- Extracted Office content does not reproduce rich layout or provide document
  editing. Legacy binary Office files, encrypted PDFs, unsupported proprietary
  models and codecs may fall back to information or a header view.
- Partial and binary content stays read-only. Cloud placeholders are skipped
  by passive hover previews.
- A bounded thumbnail cache does not bound every decoder's internal memory.
  Native parsers and blocking OS reads cannot always be interrupted immediately.
- Archive fixtures cover ZIP, 7z, tar, gzip, encryption and nested archives.
  RAR, multipart archives and ISO have not had equivalent fixture coverage.
  Whole extraction refuses output names that collide on Windows by case.
- The existing long-folder Shell, graph-refresh identity and GPU presentation
  limits remain recorded in the [v1.2.1 review](https://github.com/So2K/UltraExplorer/blob/v1.2.1/docs/REVIEW_v1.2.1.md).

Follow the [beta testing guide](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.1/docs/BETA_TESTING.md)
for short, repeatable checks. [Report an issue](https://github.com/So2K/UltraExplorer/issues/new)
with the app and Windows versions, file type, steps, expected result and actual
result. A screenshot with private names hidden can help; use a generated sample
when sharing a reproduction.
