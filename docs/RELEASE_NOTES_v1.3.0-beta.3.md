# UltraExplorer v1.3.0-beta.3

**Quick Look that is easier to use.**

This beta refines the file preview from beta.2 and retains its quiet GitHub update
mechanism. Installation still requires your explicit **Install update** click.

- Images zoom with the ordinary mouse wheel, keeping the point under the cursor
  stable. Drag to pan; `Ctrl+wheel` scrolls vertically and `Ctrl+Shift+wheel`
  scrolls horizontally. PDF scrolling retains its usual wheel behavior and
  explicit `Ctrl+wheel` zoom.
- **Open** is the accented primary action, ahead of the smaller Recycle Bin
  button. Its arrow lists Windows-registered applications with their icons and
  **Choose another app**. Missing default associations return to this menu
  without the previous Windows error dialog. Existing file defaults are kept.
- Closing an active modeless preview returns activation to its visible,
  non-minimized explorer owner before the preview HWND is destroyed. An
  intentionally minimized owner or another foreground application is respected.
  Delayed text loading also avoids taking focus after switching away.
- Audio shows a compact named card with its duration rather than a blank video
  canvas. Video keeps its large viewport. Both use themed thin timelines,
  rounded slider thumbs, accessible playback icons and a compact speed menu.
  Space toggles playback on the media surface; focused buttons and sliders keep
  their own keyboard behavior. Scrubbing still displays actual decoded frames.

Quick text editing, encoding-preserving saves, read-only archive provenance,
F3D previews and the independent optional tools remain included. No full external
editor/player interface is added to the preview.

## Download

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.3/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.3/UltraExplorer-win-x64.zip): extract the complete folder and run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.3.0-beta.3/SHA256SUMS.txt)

Both packages contain .NET/WPF and the preview engines. Your settings, notes and
color tags remain in the existing profile. This prerelease leaves v1.2.1 as the
stable latest release.

## Testing and limits

The new checks use generated PNG/PDF/text/WAV/AVI/OBJ files, real Windows
association enumeration and Shell file data, actual mpv/F3D processes, and real
owned window handles. External application launches are intercepted in these
fixtures, so these tests do not claim every installed application was launched.
Normal/maximized owner state, caption close, cancellation, dirty-note saves,
toolbar keyboard behavior, image pointer geometry and stale menu actions are
covered. Offscreen renders check the actual theme and compact layouts.

Format, archive, GPU and native-call limits from beta.1 still apply. The
conditional activation fix addresses the owner handoff; it does not force a
hidden or intentionally minimized explorer to reappear. The
[quiet update policy](https://github.com/So2K/UltraExplorer/blob/v1.3.0-beta.3/docs/QUIET_UPDATES.md)
and protected-folder/manual-installer limits remain unchanged.

[Report an issue](https://github.com/So2K/UltraExplorer/issues/new) with repeatable
steps, the file type and screenshots with private names hidden.
