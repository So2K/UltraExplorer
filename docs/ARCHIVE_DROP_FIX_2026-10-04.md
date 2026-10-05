# Archive drag/drop lifetime fix — 2026-10-04

Base: `a874477`, including Claude's completed review fixes and self-contained
installation. User report: dragging an inner ZIP from a RAR open in NanaZip
into UltraExplorer displays `Windows could not finish the operation (error 0x7C)`.

## Cause

Both the nested canvas and tree editor used async-void Drop handlers. Their
first asynchronous source validation returned control to OLE before file copying
finished. An archive manager can then remove the extracted temporary sources.
The copy sees missing files or invalid source paths; an existence check cannot
protect a file between checking it and using it.

NanaZip's archive drag creates a stack-owned `CTempDir`, extracts on mouse
release and destroys that directory when `DoDragDrop` returns. The code is visible
in its [PanelDrag.cpp](https://github.com/M2Team/NanaZip/blob/30a41ebb18cfeed248088ffd51969d31fd8db3f1/NanaZip.UI.Modern/SevenZip/CPP/7zip/UI/FileManager/PanelDrag.cpp)
and [FileDir.h](https://github.com/M2Team/NanaZip/blob/30a41ebb18cfeed248088ffd51969d31fd8db3f1/NanaZip.UI.Modern/SevenZip/CPP/Windows/FileDir.h).
Keeping an IDataObject or its path array does not extend that stack lifetime.
Upstream [7-Zip](https://github.com/ip7z/7zip/blob/main/CPP/7zip/UI/FileManager/PanelDrag.cpp)
can also replace preview paths with final paths after extraction.

Microsoft identifies 0x7C as `DE_INVALIDFILES`, an invalid source/destination
path. The code alone is a diagnostic hint, not proof of a particular failing
path. [SHFileOperation documentation](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shfileoperationw)

## Fix

- The external Drop callback remains active until the existing transfer ends.
  A shared DispatcherFrame pumps UI continuations, painting and native progress
  or cancellation while disk work stays on background/STA operation threads.
- Both canvases reread the final CF_HDROP list rather than reuse preview paths.
- Copy is reported only after success. Rejected or failed transfers report None;
  the existing optimized-move reporting remains unchanged.
- Final data-provider failures are caught, cached data/highlights are cleared,
  and the error is shown through the existing toast.
- Normal window close waits for the pending transfer before disposing the model.
  A transfer fault does not prevent saving the window. Reentrant drops in the
  same window are refused while its first transfer is active.
- Existing public DropIntoPathAsync remains Task-returning. Copy/paste and
  split-pane commands continue to use the same transfer implementation.

No archive decoder or new user-facing mode is added. The source application
still extracts its own format; UltraExplorer protects the transfer lifetime.

## Regression evidence

The same owned WPF fixture was run with separately built `a874477` product code
and with the correction. It uses real nested/tree Drop handlers, delayed data
rendering, tiny valid ZIP payloads, actual Windows file copying and source
cleanup immediately after Drop returns. The child runs on a never-switched
private desktop with isolated state and validated temporary paths.

- Initial before proof: **17/26**; both canvases returned before destination
  bytes existed, reused preview data and then reported an invalid target.
  Log: `artifacts/archive-drop-a874-before.log`.
- Expanded corrected fixture: **114/114** across nested/tree × fresh paths,
  stable temporary paths, final COM read error and close during copying.
  It checks exact destination bytes at return and after cleanup, unchanged
  source/archive canary, responsive dispatcher, reentrant-drop rejection,
  final effects, close ordering and no window being shown.
  Log: `artifacts/archive-drop-expanded-after.log`.

The final frozen fixture uses the same code for both products: **65/114 before**,
**114/114 after**, with all intended destinations verified. Stable paths isolate
the lifetime race independently of changing preview paths. Logs:
`artifacts/archive-drop-final-fixture-before.log` and
`artifacts/archive-drop-final-fixture-after.log`.

Menu/file-operation and split-pane regressions independently passed **89/89**
with both the correction and the pre-fix product in the same harness. Logs:
`artifacts/archive-peer-final-current.log`, `archive-peer-final-a874.log`.

The first full self-contained run passed **3479/3481**. Both failed assertions
were reproduced against the old product: hidden-focus depends on the fixture
being beneath hidden AppData ancestors, and Rename's composite assertion can
fail its captured selection-A precondition even though the prompt names B and
B is renamed correctly. Assertions and production code for those cases were
not altered. A final full run uses an owned visible TEMP/TMP for the intended
ordinary-folder fixture precondition; the default-TEMP failure log is retained.

Additional native Open/Save/multiselect/Cancel/folder regression: **43/43**.
Log: `artifacts/archive-drop-native.log`. Self-contained publishing completed
without reported warnings/errors and includes Desktop Runtime 10.0.11.

The full rerun with visible TEMP stopped at ExplorerObserverChecks' unchanged
five-second COM timeout (**525/526**). The isolated observer check reproduces
that timeout on both current and a874477 product (**13/14** each). No Explorer
restart, user-window mutation or longer timeout was used to mask this result.

All remaining 103 groups were then run explicitly (only ExplorerObserverChecks
excluded): **3460/3464**. Hidden-focus and Rename assertions passed with the
ordinary visible-folder fixture; Settings produced three dependent F6/split
failures and its separate COM/tree-picker child failed two assertions. These
remaining qualifications are recorded rather than claiming a fully green suite.
Logs: `artifacts/archive-drop-qualified-full.log`,
`archive-drop-remaining.log`, `archive-drop-remaining-scope.json`, and
`archive-peer-observer-{current,a874}.log`.

Final paired Settings qualification: **272/272 current**, **272/272 a874477**
with the same harness, including split controls and the child COM/tree-picker
checks. Logs: `artifacts/archive-peer-settings-current.log` and
`archive-peer-settings-a874.log`. The broad-run focus failures were not reproduced
in this isolated qualification; no assertion or product behavior was changed to
make them pass. The separate observer COM timeout remains unqualified.

## Installed copy

Functional fix commit: `26de931`. The installer replaced application files and
restored both integration preferences/roles, then exited with an error while
removing its old backup: `clrjit.dll` was still in use. The late cleanup failure
does not mean application-file replacement rolled back. Installed PE metadata
was inspected without loading it and confirms the `ExternalFileDrop` type.

The updated installed window was opened, PID **19912**, Responding=true.
Enabled=true, WinEEnabled=true, Renderer=Gpu, favorite-link preference and
pane state match the pre-install projection. Shortcut Ready=true, Error=null;
its PID/start FILETIME was verified with limited-information process queries.
Both Run entries are present and the installed listener/guardian/worker run.

An older window **24160** remains mapped to the exact installer-owned backup
path; PID/start were verified. Graceful Close was requested, then the existing
Restart Manager helper returned **350** and the process remained alive. It was
not forcibly terminated. A separate attempt to remove the backup was rejected
by automatic approval review (no specific reason beyond policy blocking), so
the directory is retained. Future cleanup must preserve any still-needed old
process resources and use the exact verified backup path.

Installed exe SHA-256:
`06504F9E2F95B31ECB7C6486B13E36972F33ED748F8A0ED7CF2AD74D71D891FB`.
Installed DLL SHA-256:
`B9BB65255B63986B702C4B0B8DD6C7F788603D821C311189AA9ADB1BB41BFE6D`.
Logs/projections: `artifacts/archive-drop-install.log`,
`archive-drop-install-before.json`, `archive-drop-install-after.json`.

## Investigated alternative

A ready-made folder Shell IDropTarget was prototyped on an owned inactive
desktop. COM bridging, binding and Copy negotiation worked, but Drop stalled
there, so this approach was not qualified or shipped. The fix retains the
already exercised file-operation pipeline. Diagnostic log:
`artifacts/archive-drop-prototype.log`.

No user's archive or destination files were used by failure tests. Live NanaZip
pointer input has not been automated; the regression reproduces its source
lifetime contract and validates both actual UltraExplorer handlers.
