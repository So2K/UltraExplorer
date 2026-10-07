# Third-party components

## Quick Look and universal previews

- AvalonEdit 6.3.1.120 — MIT, https://github.com/icsharpcode/AvalonEdit/tree/v6.3.1
- PDFium build 157.0.8086 through bblanchon.PDFium.Win32 — PDFium BSD license and bundled third-party notices; NuGet metadata declares Apache-2.0 for the package, while the binary release includes the distributor's MIT license, https://github.com/bblanchon/pdfium-binaries
- F3D 3.5.0 — BSD-3-Clause and bundled dependency notices, https://github.com/f3d-app/f3d/releases/tag/v3.5.0
- mpv 20261006 / 6c092d978b — GPL-2.0-or-later with its bundled codec/dependency notices, https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261006 and https://github.com/mpv-player/mpv/tree/6c092d978b

F3D and mpv are separate portable executables invoked only for previews. Their
complete distributions, attribution, source links and license texts accompany
the app under `preview/`; AvalonEdit and PDFium notices are in
`preview/licenses/`. The download script pins the archive SHA256 values.

The Windows dialog-integration build includes the following MIT-licensed components:

- FlaUI.Core and FlaUI.UIA3 5.0.0 — https://github.com/FlaUI/FlaUI
- Interop.UIAutomationClient 10.19041.0 — https://github.com/FlaUI/UIAutomation-Interop

FlaUI copyright (c) 2016-2024.
Interop.UIAutomationClient copyright (c) 2019 Roman.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

7-Zip 26.04 (Copyright (C) Igor Pavlov) is distributed as separate replaceable
`archives/7z.dll` and `archives/7z.exe`, used for optional archive browsing.
7-Zip uses GNU LGPL 2.1 or later, with BSD notices and the unRAR restriction
for parts of 7z.dll. Unmodified binaries and the full upstream license are
in `archives/License.txt`; source is available from
https://github.com/ip7z/7zip/releases/tag/26.04 and https://www.7-zip.org/.
The pinned setup script verifies upstream SHA-256 digests before extraction.

The application's existing graphics/canvas packages retain their own license
metadata in their NuGet packages. This notice accompanies the newly added
dialog-integration dependencies; it is not a relicensing of those components.
