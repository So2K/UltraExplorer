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
incremental layout, viewport culling, branch refresh, truncation, drop-target
selection, drag rules and persistence. It builds a throwaway fixture under
`%TEMP%`, prints one line per check and exits non-zero on the first failure.

## Publish

Self-contained single file, ReadyToRun:

```powershell
dotnet publish src/UltraExplorer/UltraExplorer.csproj -c Release -o artifacts/win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

The output is one ~157 MB `artifacts/win-x64/UltraExplorer.exe` with no runtime
prerequisite. These switches deliberately live on the command line and not in
the project file: a self-contained project cannot be referenced by the test
harness.

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

Deleting them resets the app to its first-run state.
