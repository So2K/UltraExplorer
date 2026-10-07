# Try the v1.3.0-beta.3 beta

The useful feedback is a short workflow that behaves unexpectedly: which file
type, what you did, and what happened. Use copies in a small disposable folder
so editing, extraction and Recycle Bin checks are easy to repeat.

## Get started

1. Download the installer or portable ZIP from the
   [v1.3.0-beta.3 release](https://github.com/So2K/UltraExplorer/releases/tag/v1.3.0-beta.3).
   For the ZIP, extract the entire folder before running `UltraExplorer.exe`.
2. Keep a few sample files together: two text files, an image, a PDF and,
   if relevant to your work, a media file, a model or a small ZIP containing
   text and an image. Use content you can freely change and share.
3. Start with the optional archive, shelf and copy-path switches off. Existing
   Windows folder/dialog integration and `Win+E` have their own preferences.

Windows 11, x64 is the release target. No separate .NET installation is needed.

## Quiet updates

After starting, the normal explorer waits 30 seconds before checking; a saved
timestamp limits checks to once per day. New downloads show a tiny ring near the
top-left title buttons, then a blue arrow. Nothing opens automatically.

1. Leave the arrow alone: the current version must keep working.
2. Click it and choose **Later**: only the compact panel closes.
3. In **Settings → About**, turn **Receive updates** off: the indicator and offer
   disappear and downloads stop. Reopening must keep the choice.
4. To apply a prepared update, open the arrow and explicitly press **Install
   update**. Check that your settings, notes and color tags remain after restart.
   A note conflict or refused close must leave its buffer available.

Use a future release for an actual update check; do not alter version numbers or
timestamps in your ordinary profile. The current version does not offer itself
or an older version. For protected/read-only installation folders, update with
the ordinary installer. The [update behavior](QUIET_UPDATES.md) describes limits
and generated-fixture coverage.
The portable build uses the same local settings profile as the installer;
extracting another copy does not create an isolated profile.

## Quick checks

| Try | Expected result |
| --- | --- |
| Select one image, move the pointer over another file, then tap `Space` briefly. | Quick Look shows the selected image. The file menu's **Quick Look** also opens its target. `Ctrl+Space` previews the hovered file, or the selection when nothing is hovered. |
| Close the preview. Hold `Space` and move the pointer; repeat with `Space+drag`. | The map follows the pointer immediately. Releasing Space stops the movement and does not open a preview. |
| Rest the pointer over an image, then scroll or move away. | A passive preview appears without changing selection and disappears with the gesture. Turn **Hover previews** off and repeat: no content card appears. |
| Open a copied text file with `Space`, edit it, then press Escape. Reopen it. | Editing is available immediately; the saved text returns. Repeat using **Next file** and Quick Look's close button to save. `Ctrl+S` saves while keeping the preview open. |
| Preview an image and a multi-page PDF. | Image wheel zoom needs no modifier and preserves the point under the pointer; drag pans. Ctrl+wheel pans vertically, Ctrl+Shift+wheel horizontally. PDF wheel scrolls and Ctrl+wheel zooms. |
| Preview media or a supported model, if you have samples. | Media starts paused; audio uses a named card, video a viewport. Compact controls and Space on the media surface play/pause; sliders/buttons keep their keys. Timeline seeking shows actual video frames. A model can orbit/zoom/pan and reset with Fit. Closing stops the previous preview and leaves the owner visible in its prior normal/maximized state. |
| Use **Open** and the **Open with** arrow on a copied file. | The accented Open uses the default app. Its arrow shows Windows programs with icons and Choose another app. A missing default opens that choice menu without the previous error dialog. |
| Zoom away from a selected file, then press `F`; repeat in split view. | The active pane flies smoothly to that file. Selection and navigation history stay intact. |

When a text field has focus, Space and arrow keys keep their editing meanings.
Use the preview's previous/next buttons or `Alt+Left` / `Alt+Right` to change
files while editing.

For a save-conflict check, open a copied text file in Quick Look, make an
unsaved change, then change that same copy in another editor. Closing Quick
Look should report the conflict and retain your buffer; it should not silently
replace the external change. Record any error message exactly.

If checking **Delete**, use one disposable ordinary file. It should move to
the Recycle Bin, with the next cached file shown when available. Restore the
sample from the Recycle Bin to repeat the check.

## Optional tools

Use **Settings → View → Optional features** to enable one tool at a time.
Reopen Settings after restarting to check that each preference is retained.

| Tool | Repeatable check |
| --- | --- |
| **Browse archives as folders** | On the nested canvas, enter your small ZIP and select a text entry. Tap Space: Quick Look should show a read-only copy with its archive origin. Editing and Recycle Bin deletion should be unavailable. Copy or extract the entry to a disposable ordinary folder and compare its content. Hovering an entry should not start extraction. |
| Archive layout and disable | While inside the ZIP, change from nested to tree view, then return to nested view. The tree should treat the physical archive as a file. Turn archive browsing off while inside an archive: the view should leave the virtual location and clear the archive-entry selection. Re-enable to browse again. |
| **Drop shelf** | Drag two copied files onto the board at the right edge. Drag a card into another disposable folder. Shift+drag rearranges cards; Ctrl+wheel zooms the board; its left edge resizes it. Turn the switch off and on: unexpired cards should return. Ordinary cards are links, so retain their sources while testing. |
| **Copy path button** | Hover a nested-canvas file or folder title and use the button. Paste into a text editor to check the path. Turn the switch off: the button and its clickable area should disappear immediately. |

For encrypted archive checks, use an archive you created with a known test
password. Cancelling or giving the wrong password should produce an explanation
without opening another file. Archive browsing is read-only: copy entries out
before editing. These optional tools stay disabled in file-picker dialogs.

## What to include in an issue

[Open an issue](https://github.com/So2K/UltraExplorer/issues/new) with:

- App version (`v1.3.0-beta.1`) and Windows version.
- Exact steps, expected result and actual result; include the error text.
- File extension and approximate size, and whether the path is local,
  removable, networked or cloud-backed.
- Nested or tree layout, split view, CPU or GPU rendering, and relevant
  preview/optional-tool/integration settings.
- Whether the same steps reproduce after restarting. A screenshot with private
  names hidden or a tiny generated reproduction is useful.

Do not attach private documents or personal profile files. Unsupported codecs,
encrypted PDFs and legacy Office files can use an information/header fallback;
partial, binary and extracted document content is read-only. RAR, multipart and
ISO coverage is still limited. See the
[release notes](RELEASE_NOTES_v1.3.0-beta.1.md) for the remaining limits.
