# Portable native preview engines

The hover service renders actual 3D models with F3D and video frames with mpv.
Quick Look embeds an owned native viewport: F3D supports orbit, pan, zoom,
camera reset, edges and model animation; mpv supports paused initial playback,
its on-screen controller, play/pause, seeking, audio, subtitles and mute.
These viewers do not modify source model/media files. Model geometry editing
is outside the capability of F3D; the Quick Look text editor handles text edits.

## Prepare a checkout

Run `pwsh -File scripts/setup-preview-tools.ps1` before building or publishing.
The default destination is `runtime/preview` and the download cache is under
ignored `artifacts/preview-tools`. `-Destination` and `-CacheDirectory` accept
custom absolute or relative directories. The csproj copies the entire runtime
tree beside the application as `preview/f3d` and `preview/mpv`, including
reader plugins, dependent DLLs and license notices. Keep that tree intact when
copying the portable application; a lone executable cannot contain these
external processes.

The script downloads pinned portable archives, verifies SHA256 before
extraction, and records the source/version/hash in `distribution.json`. It
does not run installers, register file types, change PATH or alter user
configuration. The portable 7-Zip extractor remains in the download cache.

| Engine | Pinned distribution | SHA256 |
| --- | --- | --- |
| F3D | 3.5.0, generic Windows x64 ZIP | `db57f9fb7e1bbe2c022ec19dab3fd1eb38545f8c7b3d29d3906a951936a2e897` |
| mpv | shinchiro Windows x64 20261006, git `6c092d978b` | `be14ecd58dbc0a8fda31d6243c07937d6cd19a1a9dbecc04bba324bbc7ce22c2` |
| 7zr | official 7-Zip 26.04 reduced extractor | `256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282` |

F3D is the official [F3D portable release](https://github.com/f3d-app/f3d/releases/tag/v3.5.0).
Its packaged [reader plugins](https://github.com/f3d-app/f3d/blob/v3.5.0/doc/user/02-SUPPORTED_FORMATS.md)
include native meshes, Assimp, OCCT CAD, USD, IFC, Alembic, Draco, PDAL and VDB.
Run `preview/f3d/bin/f3d.exe --list-readers` to inspect this exact build.
The mpv archive is an [upstream-recommended Windows build](https://mpv.io/installation/)
from [shinchiro](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261006),
using generic x64 rather than requiring x86-64-v3 CPU instructions.

## Implementation and limits

`Services/PreviewTools.cs` launches shell-free `ProcessStartInfo.ArgumentList`
processes with `--no-config`, at most two simultaneous thumbnail renders,
a 20-second absolute deadline, and caller cancellation. Each request uses
its own temporary output directory and returns a frozen WPF bitmap. F3D uses
`--output`, `--resolution` and a 256 MiB input limit for thumbnails; mpv decodes
one silent frame through `--vo=image` with an aspect-preserving scale filter.
Files which cannot produce a frame (including audio without cover art) retain
their content/details fallback. Large/unsupported/corrupt models also use that
fallback. TypeScript `.ts` stays a text document; `.mts` and `.m2ts` are media.

`Controls/NativePreviewHost.cs` owns a WPF `HwndHost` child and one engine
process. mpv uses its documented [native `--wid` embedding and JSON IPC](https://mpv.io/manual/stable/)
through a fresh named pipe. Playback starts paused. Built-in mpv OSC remains
enabled, while external user configuration and scripts are disabled. F3D's
CLI has no parent-HWND option: the host finds only the new process's window,
removes its desktop border, reparents it into the viewport and resizes it with
the host. Native interactions use the engine's own mouse/keyboard handlers;
toolbar model keys are posted exclusively to this owned HWND. The initial
F3D window is placed off-screen and shown without activation when attached.
Loading is bounded; closing/switching cancels and kills only the owned process
tree. This does not attach to or stop an existing user's F3D/mpv session.

Extension recognition trims trailing dots/spaces only for choosing a format.
The source file arguments retain the literal Windows extended path. F3D itself
normalizes trailing names, so those main inputs use an owned temporary hardlink
to the exact requested file, or a bounded copy up to 256 MiB if hardlinks are
unavailable. It never reads a similarly named normalized neighbour. For these
rare literal names, relative external textures/companions may not resolve from
the temporary alias; the complete parent folder is never scanned/copied or
modified. Unknown and unavailable formats remain
eligible for the generic document/hex/metadata preview, without promising that
every proprietary codec or model version can be decoded.

Discovery checks packaged `preview`, a checkout's `runtime/preview`, and
optional explicit `ULTRAEXPLORER_F3D`, `ULTRAEXPLORER_MPV` executable overrides
or `ULTRAEXPLORER_PREVIEW_TOOLS` runtime-root override. It does not search
the registry or system PATH. The override executable must exist.

F3D may populate its upstream shader cache under the Windows LocalAppData
`f3d` folder. This is an engine cache, not an application preference change.
Native viewports require a working graphics driver and keep the explicit
fallback if embedding or decoding fails. WPF's `RenderTargetBitmap` cannot
capture child HWND pixels; the native smoke group requests engine screenshots
to verify actual viewport content.

## Licenses and checks

F3D ships under BSD-3-Clause with its entire dependency notices under
`preview/f3d/share/licenses`. mpv is shipped as an independent executable;
its upstream GPL/LGPL/copyright texts are included in `preview/mpv/licenses`.
The pinned source links identify the exact versions used. Keep these notices
and the corresponding source/build information with any redistributed bundle.

`ViewAllSmoke --only PreviewToolsChecks` verifies pinned runtime availability,
actual OBJ and landscape/portrait AVI thumbnails, exact trailing-dot/space mesh content,
cancellation, F3D HWND parent/resizing/native screenshot, mpv JSON IPC and
native screenshot, actual paused WAV audio, byte-preserved source files, and
owned process teardown (within a bounded 3-second observation window).
The fixture uses its own cloaked WPF window, generated media and no physical
input. Screenshots are saved under `artifacts/universal-preview/shots`.
