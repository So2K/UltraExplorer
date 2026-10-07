# Research outcome

After the user clarified the core-only preview concept, the prepared full Zed
runtime, launcher, setup script and button were removed from the product.
Nothing was installed or registered globally. The unused runtime remains only
in ignored research artifacts. The final inline text preview is immediately
editable and saves on close, with no external editor startup. The investigation
below records why the tested Zed distribution/experimental embedding did not
provide a small ready WPF editor core.

# Zed integration research — 2026-10-07

The actual Zed editor is prepared as a separate portable editing window. The
existing compact text preview stays immediate; hover never starts an editor
process. This is not a transplanted Zed editing engine or an embedded Zed pane.

## Exact upstream examined

Stable [Zed 1.22.0](https://github.com/zed-industries/zed/releases/tag/v1.22.0),
source commit `76659a55a8c10ed355a070f8764a0b1733e3c115`.
The official Windows x64 installer is 84,005,568 bytes; SHA256
`8565827fd93034875cadc1e72b7242d41759f019f5b9e97efbbe6f1f84c429d1`.
It is extracted, never executed. Its [Inno installer](https://github.com/zed-industries/zed/blob/v1.22.0/crates/zed/resources/windows/zed.iss)
would otherwise force-close Zed and alter PATH, file associations and AppX.
The unpacked GUI executable is approximately 360 MB; CLI, ConPTY and AMD
runtime dependencies accompany it under `runtime/preview/zed`.

`scripts/setup-zed-preview.ps1` downloads verified pinned payloads and performs
offline extraction/integrity checking. It uses [innoextract Windows build 670](https://github.com/UserUnknownFactor/innoextract_win/releases/tag/670),
SHA256 `79b69b9b1fcd98f42ccd4b245efdf6a03bcfb674ba6af482f5a46891c9ed4d14`;
the extractor stays in the ignored cache. That maintained ZLIB-licensed fork
supports this actual Inno 6.4.3 package and does not execute installation scripts.
The verified release's build workflow was inspected. No global installation,
registry, association, updater, PATH or existing Zed process is changed.

## Process ownership and startup

The supported [CLI](https://zed.dev/docs/reference/cli) offers `--new`,
`--foreground` and `--user-data-dir`, but the [Windows singleton source](https://github.com/zed-industries/zed/blob/v1.22.0/crates/zed/src/zed/windows_only_instance.rs)
uses a release-channel mutex/pipe, ignoring the custom directory. The normal
`bin/zed.exe` CLI can forward to a user's existing Zed. It must not be used to
acquire an owned editor instance.

`ZedPreviewTools` instead starts the main `Zed.exe` directly with
`--foreground --user-data-dir <UltraExplorer state>/ZedEditor -- <exact file>`
and child environment `ZED_STATELESS=1`. The [main executable](https://github.com/zed-industries/zed/blob/v1.22.0/crates/zed/src/main.rs)
explicitly bypasses singleton handling in stateless mode and uses in-memory
databases. [Custom paths](https://github.com/zed-industries/zed/blob/v1.22.0/crates/paths/src/paths.rs)
put configuration under `<custom>/config` and data/extensions/logs under the
custom root. Windows `temp_dir` still uses Zed's LocalAppData cache; it is not
an isolated preference/history database. Launcher discovery never scans PATH
or attaches to existing editor windows.

The owned default settings disable auto-updating, auto-save, formatting on save,
AI and language-server startup, with no session restore. Existing owned settings
are preserved. Opening a file remains an explicit editing action. Quick Look
closing never kills Zed; Zed governs dirty buffers and its own save prompts.

## Why the Zed guts are not a drop-in WPF component

The [editor crate](https://github.com/zed-industries/zed/blob/v1.22.0/crates/editor/Cargo.toml)
is GPL-3.0-or-later and depends on Zed's Rust workspace, language/project models,
GPUI event/render lifecycle and many other crates. [GPUI](https://github.com/zed-industries/zed/blob/v1.22.0/crates/gpui/Cargo.toml)
is Apache-2.0, but is a Rust UI framework rather than an exported .NET editor
DLL. Neither offers a supported C ABI/Win32 child editor SDK in this release.
Using the internal engine in WPF would require a Rust bridge/build, exported
buffer/save/dirty APIs and modifications to GPUI's Windows hosting lifecycle.

The closest official reuse candidate was also inspected:
[embedded_gpui README](https://github.com/zed-industries/embedded_gpui/blob/28bc6d3d559b62040626403040b680ad96ef696b/README.md)
and [design](https://github.com/zed-industries/embedded_gpui/blob/28bc6d3d559b62040626403040b680ad96ef696b/DESIGN.md),
commit `28bc6d3d559b62040626403040b680ad96ef696b`. It runs WASM GPUI
plugins inside a native Rust GPUI host; its demonstrations include counters,
text input and drawn surfaces. The authors identify it as an experimental
extension prototype, without a supported API. Its Apache-2.0 crate uses
Wasmtime 36 and the special Zed `gpui-embedded-in-gpui` branch for
`Application::run_embedded`. The inspected manifests and host example provide
neither a Zed editor widget nor a C/.NET/WPF host. The design also records
missing guest IME composition support. This is a useful starting point for a
future Rust bridge, rather than a ready editor DLL: WPF rendering/input
integration and the actual Zed editing/save lifecycle would still need work.
It does not change the current choice of immediate compact preview plus the
actual isolated Zed window for explicit editing. No speculative port or GUI
performance claim is based on this prototype.

The [GPUI Windows window implementation](https://github.com/zed-industries/zed/blob/v1.22.0/crates/gpui_windows/src/window.rs)
creates a `Zed::Window` with DirectX 11. Its parent HWND is used for native dialogs,
not an external host option. `SetParent` alone would leave desktop chrome,
activation, dialogs, IME, keyboard routing and unsaved-close behavior unresolved.
No successful embedded-editor proof exists; the shipped launcher does not claim
one. A full Zed process also cannot deliver the same startup cost as an already
loaded small WPF preview; real GUI cold/warm latency has not been measured.

The source's `WindowsWindow.activate()` explicitly calls global `SendInput`
for Alt down/up before bringing its window forward. This is ordinary upstream
activation on a user-requested launch, but a hidden/cloaked automated GUI
fixture would still trigger it. Consequently the automated actual-binary probe
uses `--system-specs`, which returns before GPUI/window creation. It does not
claim actual GUI/embedding/save interaction proof or no-input behavior for a
production Zed activation.

## Encodings and exact files

This release's [file-content source](https://github.com/zed-industries/zed/blob/v1.22.0/crates/language/src/file_content.rs)
handles UTF-8, UTF-16 LE/BE, Unicode BOM retention and guessed legacy encodings.
It explicitly implements UTF-16 encoding on save, and includes upstream
Windows-1251 roundtrip and Unicode BOM tests. Old reports that Zed supports only
UTF-8 do not describe this pinned release.

The launcher nevertheless admits only UTF-8 and BOM-tagged UTF-16. Legacy
encoding guessing has no CLI override and can disagree with the compact
editor's detection, so those files retain its encoding-preserving editor.
Literal trailing-dot/space names also stay in the compact editor: no normalized
neighbour may be opened for editing. Source files are passed as separate exact
`ArgumentList` entries, never a shell command. The launcher does not rewrite or
convert them.

## Verification and redistribution

`ZedPreviewChecks` verifies the main executable, stateless/isolated launch plan,
encoding guards, controlled settings, actual version/system-specs startup,
no persistent database, unchanged source bytes and unchanged user Zed settings.
It exercises no GUI and injects no input. Actual editing-window behavior remains
an explicit manual verification item.

GPL/Apache license texts and exact distribution metadata accompany the runtime.
Zed's own dependency/license notices are compiled into its assets; retain their
source/build information when redistributing. [Windows requirements](https://zed.dev/docs/installation)
include a supported Windows 10/11 version and working DirectX 11 GPU driver.
