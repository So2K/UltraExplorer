# Build, run, test

Requires the .NET 10 SDK on Windows x64.

## Build

```powershell
dotnet build UltraExplorer.sln -c Debug
```

Both configurations build with `0 Warning(s), 0 Error(s)`.

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
and what the graph is allowed to show. It
builds a throwaway fixture under `%TEMP%`, prints one line per check and exits
non-zero on the first failure.

Its `performance` section asserts wall-clock budgets rather than describing them
— a spatial-index build over 300k nodes, viewport queries and render sets over
that graph, reindexing 20k node moves, laying out 20k children of one folder —
and `large real folder` expands `C:\Windows\System32` for real. Run it after any
change to the graph, layout, index or viewport services.

## Publish

Self-contained single file, ReadyToRun:

```powershell
dotnet publish src/UltraExplorer/UltraExplorer.csproj -c Release -o artifacts/win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

The output is one ~157 MB `artifacts/win-x64/UltraExplorer.exe` with no runtime
prerequisite. These switches deliberately live on the command line and not in
the project file: a self-contained project cannot be referenced by the test
harness.

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

## Where state lives

| File | Contents |
|---|---|
| `%LOCALAPPDATA%\UltraExplorer\view-all.workspace.json` | expanded branches, node positions, viewport, active path, extra roots |
| `%LOCALAPPDATA%\UltraExplorer\workspace.json` | pinned folders, navigation pane width, minimap toggle |
| `%LOCALAPPDATA%\UltraExplorer\folder-marks.json` | colour labels and notes, keyed by path |
| `%LOCALAPPDATA%\UltraExplorer\picker.workspace.json` | canvas layout of file-dialog sessions, kept apart from the user's own |
| `%LOCALAPPDATA%\UltraExplorer\picker-clients.json` | per caller GUID: last folder, file type, recent names |

Deleting them resets the app to its first-run state.
