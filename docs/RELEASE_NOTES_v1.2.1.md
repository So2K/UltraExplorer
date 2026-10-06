# UltraExplorer v1.2.1

**Lost a selected file on the map? Press F.**

`F` smoothly zooms to the selected file or folder, using the same camera
motion as Tags and favorites. A file gets a large centered card, not merely
its parent folder. Selection and history stay unchanged. Typing in an editor
and `Ctrl+F` search keep their existing meanings.

## Review fixes

- Precise focus follows sorting, live directory changes and window resizing;
  later selection, pane changes and manual movement cancel stale flights.
- Batch/script drops no longer pass raw filenames through command-text
  parsing. Supported batch names round-trip literally; unsafe configured
  command templates fail with an explanation.
- Missing or inaccessible transfer sources cannot produce a false successful
  copy/move acknowledgement.
- Folder requests avoid closing or busy windows. Hide and multi-reveal
  operations use current graph objects after asynchronous reads.
- The local source updater preserves unlisted files and nested additions and
  rolls back only package-owned files. Test isolation and local version
  metadata have also been tightened.

## Download

Windows 11, x64. Both packages include the .NET and WPF runtime.

- [Installer — UltraExplorer-Setup-x64.exe](https://github.com/So2K/UltraExplorer/releases/download/v1.2.1/UltraExplorer-Setup-x64.exe)
- [Portable — UltraExplorer-win-x64.zip](https://github.com/So2K/UltraExplorer/releases/download/v1.2.1/UltraExplorer-win-x64.zip): extract the entire folder, then run `UltraExplorer.exe`.
- [SHA-256 checksums](https://github.com/So2K/UltraExplorer/releases/download/v1.2.1/SHA256SUMS.txt)

Preferences, favorites, colors and notes remain in your local profile.
Optional Windows integration and `Win+E` keep their independent preferences.

## Validation and limits

The self-contained Release build has zero warnings and zero errors. New
focus and safety regressions pass **109/109**, with a separate **16/16**
lifecycle child; Tags pass **35/35**, updater fixtures **47/47**, and the full
native-dialog fixture **661/661**. The product-code broad run reported
**4530/4533**: two retained limits and an older test fixture subsequently
corrected and rechecked **45/45**. This is not a fully green broad total.
Exact native and broad qualification is recorded in the
[review report](https://github.com/So2K/UltraExplorer/blob/v1.2.1/docs/REVIEW_v1.2.1.md).

This patch does not replace Windows Shell's recursive long-folder limit,
the graph's refresh identity model or the GPU presentation architecture.
Advanced script shortcuts with preset commands are deliberately rejected.
The stable-channel label does not mean every earlier finding or broad test
is resolved. See the review report for the exact results and limitations.

[Project page](https://github.com/So2K/UltraExplorer) · [Report an issue](https://github.com/So2K/UltraExplorer/issues/new) · [Русский](https://github.com/So2K/UltraExplorer/blob/v1.2.1/docs/README.ru.md)
