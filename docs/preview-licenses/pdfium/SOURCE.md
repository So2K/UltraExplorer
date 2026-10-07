# PDFium runtime licenses

These files are copied without modification from the pinned Windows x64 runtime
archive for PDFium 157.0.8086.0:

- Release: https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8086
- Archive: https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8086/pdfium-win-x64.tgz
- NuGet runtime: `bblanchon.PDFium.Win32` version `157.0.8086`.
- NuGet packaging repository commit: `5325fa6d0d9379329f10f98fdc4839b4e40f2e79`.
- The archive's `bin/pdfium.dll` and NuGet's `runtimes/win-x64/native/pdfium.dll`
  have the same SHA-256:
  `1F2AEE4286B8CF0413AB4562B7121AA454F9CFD9AFD6CA8609BCEF80927B3F80`.

`LICENSE` is the license notice bundled by the binary distributor. The complete
`licenses/` directory contains PDFium's BSD-style notice and all dependency
notices distributed with this particular binary. The NuGet nuspec separately
declares the packaging license expression `Apache-2.0`; this does not replace
the bundled PDFium or dependency license notices.

The release build configuration disables V8 and XFA. UltraExplorer uses the
PDFium render APIs, without registering script, form-fill or external-resource
callbacks.
