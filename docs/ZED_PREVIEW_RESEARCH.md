# Zed editor research — 2026-10-07

Zed was investigated as a possible editing core. The final inline preview uses
AvalonEdit: complete plain text is editable immediately and saves on close in
its original encoding. No Zed application, launcher, setup script or runtime is
shipped with UltraExplorer. See [UNIVERSAL_PREVIEWS.md](UNIVERSAL_PREVIEWS.md)
for the implemented editing behavior and its limits.

The candidate examined was [Zed 1.22.0](https://github.com/zed-industries/zed/releases/tag/v1.22.0),
source commit `76659a55a8c10ed355a070f8764a0b1733e3c115`. This note records that
investigation, rather than instructions for a product integration.

## Component reuse limits

The pinned [editor crate](https://github.com/zed-industries/zed/blob/v1.22.0/crates/editor/Cargo.toml)
is GPL-3.0-or-later and depends on Zed's Rust workspace, language/project models
and GPUI rendering/event lifecycle. [GPUI](https://github.com/zed-industries/zed/blob/v1.22.0/crates/gpui/Cargo.toml)
is Apache-2.0. The inspected crates provide no supported C ABI or Win32 child
editor SDK for a .NET/WPF host. Reusing them would require a Rust bridge,
buffer/save APIs and changes to Windows hosting, input and lifetime handling.

The official [embedded_gpui prototype](https://github.com/zed-industries/embedded_gpui/blob/28bc6d3d559b62040626403040b680ad96ef696b/README.md)
and its [design](https://github.com/zed-industries/embedded_gpui/blob/28bc6d3d559b62040626403040b680ad96ef696b/DESIGN.md)
were inspected at commit `28bc6d3d559b62040626403040b680ad96ef696b`. It runs WASM
GPUI plugins in a native Rust GPUI host. The project describes an experimental
extension prototype without a supported API. Its examples and manifests
provide neither a Zed editor widget nor a C/.NET/WPF host; the design also
records missing guest IME composition support.

The pinned [GPUI Windows window implementation](https://github.com/zed-industries/zed/blob/v1.22.0/crates/gpui_windows/src/window.rs)
uses a parent HWND for native dialogs. Reparenting its desktop window alone
would not resolve editor activation, dialogs, keyboard/IME routing or dirty
buffer handling. No successful embedded editor or actual GUI startup/edit/save
performance result was established by this investigation.
