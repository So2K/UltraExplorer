# Build, run, test

Requires the .NET 10 SDK on Windows x64.

Before the first build, prepare the pinned portable 3D/media engines:

```powershell
pwsh -File scripts/setup-preview-tools.ps1
```

The build/publish output includes a `preview/` folder with F3D, mpv and license
notices. Keep it with the executable. The local install script and CI prepare
these engines automatically. See [UNIVERSAL_PREVIEWS.md](docs/UNIVERSAL_PREVIEWS.md).

## Build

```powershell
dotnet build UltraExplorer.sln -c Debug
```

Both configurations build with `0 Warning(s), 0 Error(s)`.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1 -Launch
```

Publishes Release into `%LOCALAPPDATA%\Programs\UltraExplorer` and adds an
UltraExplorer shortcut to the Start menu, so Windows Search and launchers find
it. Run it again to update: it closes the installed copy (which saves its
state), replaces it and, with `-Launch`, starts it. `-Desktop` also adds a
desktop shortcut. Settings stay in `%LOCALAPPDATA%\UltraExplorer`.

Every main-branch push and pull request builds, on GitHub Actions
(`.github/workflows/release.yml`), a self-contained application folder
with its .NET/WPF runtime and packs it twice: `UltraExplorer-win-x64.zip` (portable)
and `UltraExplorer-Setup-x64.exe`, an Inno Setup installer made from
[`installer/UltraExplorer.iss`](installer/UltraExplorer.iss). Both are kept as
the run's artifacts; pushing a `v*` tag (`v1.2.3`, `v1.2.3-beta.1`) stamps
that version into the exe and the installer and attaches both plus SHA-256
checksums to a release. Tags with a prerelease suffix become GitHub prereleases.

The installer installs per user without elevation into the same folder as
`install.ps1` (or, on request, for all users into Program Files), adds the
Start menu shortcut with the app's taskbar id, an optional desktop shortcut and
an uninstaller. The uninstaller also removes the file-dialog COM registration
(`--register-picker`) when it points at the copy being removed; settings in
`%LOCALAPPDATA%\UltraExplorer` are kept. To build it by hand, publish as in
[Publish](#publish) below and run Inno Setup 6.3+ on that folder (paths are
relative to the script); the setup lands in `artifacts\installer\`:

```powershell
iscc /DAppVersion=1.2.3 /DAppFileVersion=1.2.3 /DPublishDir=..\artifacts\win-x64 installer\UltraExplorer.iss
```

## Run

```powershell
dotnet run --project src/UltraExplorer/UltraExplorer.csproj
```

Close any running instance before rebuilding — Windows keeps the `.exe` locked
and the copy step fails otherwise.

## Test

```powershell
dotnet build tests/ViewAllSmoke/ViewAllSmoke.csproj -c Debug
tests/ViewAllSmoke/bin/Debug/net10.0-windows/win-x64/ViewAllSmoke.exe
```

`ViewAllSmoke` is a headless harness over the View All engine: lazy expansion,
incremental layout, viewport culling and mip levels, depth scaling, branch
refresh, truncation, drop-target selection, drag rules and persistence, plus the
file-dialog rules - file types, legacy flag translation, typed names, validation
and what the graph is allowed to show - and the nested canvas: its geometry, its
lazily read tree over a fake disk, the camera, hit testing, sorting by name,
date, type and size, and the cost of a frame, all without a window - plus the GPU
renderer offscreen on each graphics card and on WARP: device and surface
lifetime, the rectangle shader against the CPU raster, the icon and glyph
atlases and text shaping against WPF's own - and the split view: two canvases
sharing one change hub, icon service and graphics card, each pane's own
camera, selection and history in a window that is never shown, and copying,
moving and dropping real files between the panes in a folder under `%TEMP%`
(`SplitPaneChecks`). It
builds a throwaway fixture under `%TEMP%`, prints one line per check and exits
non-zero if any check failed. If `%TEMP%` is not writable (a sandboxed shell),
point `TMP` and `TEMP` at a folder that is.

Its `performance` section asserts wall-clock budgets rather than describing them
— a spatial-index build over 300k nodes, viewport queries and render sets over
that graph, reindexing 20k node moves, laying out 20k children of one folder —
and `large real folder` expands `C:\Windows\System32` for real. Run it after any
change to the graph, layout, index or viewport services.

## Publish

Self-contained folder, ReadyToRun:

```powershell
dotnet publish src/UltraExplorer/UltraExplorer.csproj -c Release -o artifacts/win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=false
```

Keep the entire output folder together. It has no runtime prerequisite and
does not extract native DLLs into TEMP at launch. These switches deliberately
live on the command line and not in
the project file: a self-contained project cannot be referenced by the test
harness.

For the installer, add `LICENSE`, `README.md` and `THIRD-PARTY-NOTICES.md`
to the publish folder, then create `UltraExplorer-package-files.txt` containing
every publish-relative filename, including the manifest itself. The workflow
performs this step and verifies every file is present in the ZIP. Setup uses
the old and new manifests to remove obsolete package files after an update.

`tests/FolderTagsSmoke` checks tag navigation, persistence, path classification
and asynchronous refresh. CI publishes the focused harnesses self-contained,
so they also run when the machine has only the SDK installed.

The five public demonstrations can be reproduced with
[`tools/DemoRecorder`](tools/DemoRecorder). Capture details and encoding are in
[docs/media/README.md](docs/media/README.md).

## File dialog mode

UltraExplorer can act as the file dialog for another program.  The whole
contract - switches, JSON request and result, COM registration, which
`FILEOPENDIALOGOPTIONS` do what - is in [PICKER.md](PICKER.md).  The short form:

```powershell
UltraExplorer.exe --pick --folder --title "Pick a folder" --result out.json
```

Exit code 0 accepted, 1 cancelled, 2 error.  `--pick --help` prints every
switch.

To offer the same thing through COM, register the two dialog classes once for
this user (no elevation, nothing outside this account):

```powershell
UltraExplorer.exe --register-picker
```

`--unregister-picker` removes them.  `--com-server` is how COM starts the
server itself; there is no reason to run it by hand.

The COM server has no console and no window of its own, so what happens inside
it goes to `%LOCALAPPDATA%\UltraExplorer\com-server.log`.

`ViewAllSmoke.exe --com-client <folder>` is a caller written the way a real
program would write one: it creates the CLSID, asks for `IFileOpenDialog`,
subscribes to the dialog's events and prints what comes back.  Add `--save` to
drive `IFileSaveDialog` instead.  It needs `--register-picker` first.

## Screenshot switch

`--capture <path>` renders the live window to a PNG once the window settles, and
again every time a `<path>.request` file appears next to it. It exists so a test
harness can snapshot any UI state without fighting DPI scaling or window
z-order. Without the switch the service is never constructed.

```powershell
UltraExplorer.exe --capture C:\temp\shot.png
```

## Search

The search box searches every drive and puts first what is in the folder you
are in: results right in it, then inside its folders, then everywhere else;
within each, exact names before names that start with the word, before the
rest, and caches and package folders (`node_modules`, `AppData`, WinSxS) last.
Results come as you type and stay up while the canvas is used: ↑↓ walk them
(the canvas follows), Enter shows one, Ctrl+Enter opens it, a right-click is
its Shell menu, Esc in the box closes. Moving to another folder yourself
re-orders them for it; going to a result does not.

It asks [Everything](https://www.voidtools.com/) when that is running with its
index loaded, straight over Everything's own window-message IPC
(`Services/Search/EverythingClient.cs`) — no `Everything64.dll` is needed. An
installed Everything that is not running is started on the first search.
Without it the drives are walked on a few low-priority threads, the folder you
are in first, results streaming in. `subst` letters are folded into the
folders they stand for, so nothing is listed twice.

## Renderer

The nested canvas draws with Direct3D 11 when it can and with the CPU raster
otherwise (remote desktop, software rendering, no Direct3D 11). Force one with
`--renderer auto|gpu|cpu`, the variable `ULTRAEXPLORER_RENDERER`, or Canvas options
→ Renderer. `ULTRAEXPLORER_TEXT_TUNING=gamma=1.8;contrast=1;bias=1` overrides the
GPU text tuning. Run a Release build to judge speed: Debug turns the JIT
optimiser off and the canvas is several times slower.

```powershell
dotnet publish src/UltraExplorer/UltraExplorer.csproj -c Release -r win-x64 --self-contained false -o out\release
```

## Nested canvas benchmark and snapshots

Two switches judge a change to the nested canvas by numbers and by pixels
instead of by feel. Both open the window at 1600×1000 on the first monitor
that is not the primary one (or the one `ULTRAEXPLORER_DIAGNOSTICS_MONITOR`
names by index), never activate it, switch to the nested canvas, do their
run on its first pane and exit - with a fresh state folder the view is not
split, and the pictures are those of the one pane a window always had. Compare only runs made on the same monitor: another scale is
another picture.

```powershell
$env:ULTRAEXPLORER_STATE_DIR = "$env:TEMP\ue-bench"
UltraExplorer.exe --nested-bench C:\temp\bench.tsv
UltraExplorer.exe --nested-snapshots C:\temp\shots
```

- `--nested-bench <file.tsv>` flies a fixed route (the whole PC, into a big
  system folder, a pan, back out, a folder of thousands of files, a long
  flight), timing every frame, then times selecting deep folders until the
  address bar and the list follow. One row per phase: mean and worst frame,
  frames over 20 ms. `sort-switch` clicks through Size, Date, Type and Name
  on a folder of thousands of files and reports the worst click. Per phase it
  also reports the UI split (walk, labels, present, GPU wait), GPU time,
  allocations and collections, and `scene_gpu_frames`/`labels_gpu_frames`.
  `--bench-window 1920x1040 --bench-scale 1.5 --bench-layout 0.742` emulates a
  4K 150% screen on a smaller monitor.
- `--nested-snapshots <folder>` renders a fixed set of views to PNG once
  everything in them has been read. The PNGs of two builds compared pixel
  for pixel say whether a change altered the picture. `with-strip\` beside
  them holds the same views with the strip above the canvas. Scenes 01, 02
  and 12 show live disk data (free space, what was read) and differ between
  any two runs. `renderer.tsv` says which renderer drew each scene.

Run them with `ULTRAEXPLORER_STATE_DIR` pointing at an empty folder, so the
saved session does not colour the result and the run does not touch it. A
failure is written next to the output as `*.error.txt`.

`ULTRAEXPLORER_TEST_WINDOW=1` opens an ordinary copy the same way — on the
other monitor, without taking the keyboard — to try a build beside the
everyday one. `ViewAllSmoke --only <Group,...>` runs only the named check groups.

## Where state lives

| File | Contents |
|---|---|
| `%LOCALAPPDATA%\UltraExplorer\view-all.workspace.json` | expanded branches, node positions, viewport, active path, extra roots (and how many starts in a row each has been out of reach - ten, and it is forgotten), nested canvas camera, and the split view's second pane: its path and camera (`secondPane`) |
| `%LOCALAPPDATA%\UltraExplorer\workspace.json` | pinned folders, navigation pane width, minimap toggle, nested or tree canvas, sort order, split view (on or off, side by side or stacked, the divider's ratio, the pane worked with) |
| `%LOCALAPPDATA%\UltraExplorer\folder-marks.json` | colour labels and notes, keyed by path |
| `%LOCALAPPDATA%\UltraExplorer\picker.workspace.json` | canvas layout of file-dialog sessions, kept apart from the user's own |
| `%LOCALAPPDATA%\UltraExplorer\picker-clients.json` | per caller GUID: last folder, file type, recent names |
| `%LOCALAPPDATA%\UltraExplorer\gpu\`, `cache\`, `jit\` | compiled shaders, the icon and glyph atlases, the startup JIT profile - all rebuilt when missing |
| `%LOCALAPPDATA%\UltraExplorer\crash.log` | every exception nothing else caught, and every nested-canvas frame that failed, with its stack; `crash.old.log` holds the previous megabyte |
| `%LOCALAPPDATA%\UltraExplorer\*.json.corrupt-<time>` | a state file that could not be read, set aside rather than overwritten by the next save, to be mended by hand |

Deleting them resets the app to its first-run state. The environment variable
`ULTRAEXPLORER_STATE_DIR` moves the whole folder, so a second copy can run
beside the everyday one without sharing its state; if the folder it names is
unusable, a fresh temporary one is used rather than the real one.
