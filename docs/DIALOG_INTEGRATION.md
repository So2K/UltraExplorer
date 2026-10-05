# Open / Save / folder dialogs through UltraExplorer

This optional Windows mode shows UltraExplorer's picker when another program
opens a standard Windows file dialog (`IFileOpenDialog` / `IFileSaveDialog`,
including Select Folder). The program's own dialog is never closed or replaced:
it stays alive, transparent, behind UltraExplorer, and receives the result from
it. UltraExplorer hands back the chosen path(s), the file type and the
program's own extra options; the program still validates the choice, asks
before replacing a file, and does the actual open or export.

It is **off by default** and only the user turns it on. Nothing is patched: no
system DLL, no COM class of Windows, no file association, no hook DLL loaded
into other programs, no elevation.

## Switching it on and off

**Settings → File dialogs → Use UltraExplorer throughout Windows.**

The adjacent **Open UltraExplorer with Win+E** checkbox is independent: it can
stay enabled when general replacement is off. Updating preserves both choices.

- On: a small resident listener starts (`UltraExplorer.exe --dialog-agent`,
  with an icon in the notification area) and a value named
  `UltraExplorer dialogs` is added under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, so the listener starts
  again when you sign in. Closing UltraExplorer's own window leaves the mode on.
- Off: the value is removed, any dialog being replaced right now goes back to
  Windows, and the listener stops within a second.
- The sign-in value follows the setting whoever changes it: switching off,
  **Restore and pause**, the tray menu, the emergency shortcut and an automatic
  pause after a failure all remove it. Each start of UltraExplorer's window
  puts it right (adds it when the mode is on and it is missing or names an
  executable that no longer exists; removes it when the mode is off). The
  uninstaller runs `--dialog-recover` and removes it as well.
- A copy started with its own state folder (`ULTRAEXPLORER_STATE_DIR`) or as a
  test copy (`ULTRAEXPLORER_TEST_WINDOW=1`) never touches that registry value.

Updating the installed copy with `scripts/install.ps1` builds into a temporary
folder first. It restores any outstanding Windows dialogs, stops the installed
background roles and closes ordinary installed windows through their normal
save path. Application files have a rollback copy until replacement succeeds.
If an owned modal window prevents the normal close, the installer uses
[Restart Manager](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmshutdown)
for only the exact installed process and creation time, without force flags.
The application's existing session-ending handler saves its state; original
Windows Explorer and other applications are not registered for this close.
The user's on/off choice and current application exceptions survive the update;
an enabled listener starts again from the installed executable. Another copy's
active integration makes the update stop before recovery, because those copies
may share the same per-user state.

## Using it

The picker opens centred over the program's dialog, on the same monitor, at
least as large as the dialog and large enough for the canvas (1280 × 860 at
100 %, never beyond the monitor's work area). It takes the keyboard from the
dialog once it is on screen. The dialog's folder, file name, file types and
Open/Save wording are carried over.

With the listener running, the picker is usually on screen about a tenth of a
second after the dialog appears (measured: median 0.10 s, worst 0.14 s),
already showing the dialog's folder. Its OK button (and Enter, and a
double-click on a file) waits a little longer, until the dialog has been read
in full - its file types, the program's own options and whether it takes
several files - which is typically within a third of a second of the dialog
appearing: a dialog that has just appeared answers slowly while it is still
setting itself up. Cancel, **Use the Windows dialog** and switching the mode
off work during that read. A full read that exceeds eight seconds returns to Windows;
that worker is retired before another request can use it. An input attempt
waiting for the contract is discarded when its dialog is cancelled or replaced.

The prepared picker now acquires its independent recovery lease and makes the
original transparent before showing its own first frame. The native dialog
therefore cannot cover the picker while its full contract is still being read.
An unsupported contract, mode-off or recovery returns the exact original to
Windows. This uses out-of-process window events and the existing watchdog;
an initial Windows frame before event delivery is still possible.

The replacement uses the tile canvas by default. It starts directly in the
caller's folder; file-type filters reuse the cached listing, and selected
tiles retain their exact full paths even while navigation catches up. The
standalone command-line/COM picker keeps its tree layout.

The initial folder is framed after the final dialog placement and resize, so
its tiles are readable immediately. **This PC / Fit all** includes every drive
root; unfocused drive contents load only when explicitly opened, keeping startup
fast. The sidebar always keeps the other drives available.

For Open dialogs, the last file choice remains pending after clicking empty
space or navigating elsewhere. The footer shows its filename and full location,
with **Clear** to discard it. Choosing another file replaces it; editing the
filename switches to that typed choice. A retained file always resolves to its
original full path, even if the current folder contains an identically named
file. A new dialog starts with a fresh choice.

The footer shows:

- **Options from <program>**: the program's own check boxes, radio choices,
  lists, text fields and labels, in the window's dark style. A change is made
  in the hidden original at once, with the notifications the program expects,
  so its `OnFileOk` sees exactly those values.
- **Use the Windows dialog this time**: closes the picker and shows the
  program's dialog exactly as it was. Nothing is chosen or cancelled.
- **Always use Windows for <program>**: the same, and the program is not
  replaced again. **Settings → File dialogs → Reset exceptions** undoes it.

Before a confirmed OK or Cancel is passed on, the original dialog is made visible again, so
a "file already exists" question, a sharing error or the program's own
validation message appears normally, from the program. UltraExplorer never
answers such a question for you. A Select Folder dialog needs two confirmations
from Windows (the first navigates into the typed folder); the second is given
only after checking that the dialog really is in the folder you chose.
Cancel during the provisional read is sent to the protected original while it
is still transparent; if the caller ignores it, cleanup restores its window.

## Which dialogs it serves

The Open, Save and Select Folder dialogs Windows shows through its common item
dialog, whichever way a program asks for one. Tested (see Checks) with:

- the COM dialogs themselves (`IFileOpenDialog` / `IFileSaveDialog`, with or
  without `FOS_PICKFOLDERS`), including the program's own check boxes, lists,
  radio choices and text fields (`IFileDialogCustomize`);
- WinForms `OpenFileDialog`, `SaveFileDialog` and `FolderBrowserDialog`, WPF's
  `OpenFileDialog`, `SaveFileDialog` and `OpenFolderDialog`, and
  `GetOpenFileNameW` / `GetSaveFileNameW` in Explorer style without a hook
  (Windows shows the same dialog for all of them);
- dialogs with no file-type list at all (a WinForms dialog without `Filter`,
  ShareX's upload), many types (24, the chosen one far down the list), long
  translated labels, several files at once, a folder whose path has spaces and
  Cyrillic letters, a drive's root, the user's own folder shown by name.

The program gets back what Windows' own dialog would have given it: the chosen
path or paths, the file type it reads back (`FilterIndex`, `nFilterIndex`,
`GetFileTypeIndex`) and its own options, and it still validates the choice and
asks before replacing a file. A proposed Save name is shown as the dialog
shows it (with extensions shown, "export" for a log is "export.log").

## When it stays with Windows

A dialog is left exactly as Windows showed it, never half replaced, when:

- it is not a common item dialog: the legacy folder tree (`SHBrowseForFolder`)
  has no file view and is never taken for a file dialog (tested: nothing is
  shown or hidden, and its own Cancel reaches the program). The older
  Explorer-style Open/Save dialog, which Windows shows for `GetOpenFileNameW` /
  `GetSaveFileNameW` given a hook procedure or a template of the program's (a
  preview, options of its own that the picker could not mirror), is looked at
  and left alone: its folder is not shown as a path the picker can read, and
  the log says so (tested with a hook: nothing shown or hidden, no lease, its
  own Cancel reaches the program);
- it has an extra control the picker cannot mirror faithfully (a push button,
  a menu, a custom interactive control, a password or multi-line field, file
  metadata fields in a Save dialog);
- a file type's pattern cannot be read from its label (a label such as
  "Photoshop image" with no `(*.psd)` is never guessed; given one without its
  pattern, Windows' own dialog showed "Photoshop image (*.psd)" in the tests,
  which is read - on a machine that shows file extensions; with them hidden
  that is untested);
- its folder is a virtual location that is not a file system path (This PC,
  Libraries, a phone). Folders the dialog shows by name rather than by path
  are resolved: the known folders (Downloads, Documents, Desktop, Pictures,
  Music, Videos and the rest, the user's own folder under the user's name)
  in the user's and the system's languages and English, from the same names
  the dialog reads them from; a per-dialog process without the prepared
  worker knows the six common ones in English and Russian;
- the program runs elevated (as administrator) and UltraExplorer does not;
- the dialog is no longer in front by the time it has been read, or the picker
  cannot be put over it (the dialog or the picker gone meanwhile);
- the program is in the exception list, or eight replacements are already open.

A program with its own, non-Windows file browser is not affected at all. The
standard dialog is recognised by its window structure, not by its title, so it
works in any Windows language. It has been tested against the real Windows
dialog classes with custom controls (see Checks below), not against every
version of every program: there is no claim that a particular Photoshop,
Blender or browser version behaves.

The original dialog is noticed through out-of-context WinEvent hooks with a
200 ms foreground poll as a backstop. The hooks live on a thread of their own
that only pumps their events: a top-level dialog window is handed to the
listener the moment its event arrives, and that moment is its recognition.
A prepared worker starts with the listener and accepts requests over a
current-user-only named pipe; busy or unavailable workers use the per-dialog
process fallback. UI Automation reads only the dialog's controls, pruning the
file list and navigation tree.

The worker keeps one picker window ready: already shown once, but cloaked by
the desktop compositor (never on screen, never activated - a thread-local hook
refuses its activation until it is handed the keyboard - and out of the
taskbar), on the monitor the last dialog was on. When a dialog is recognised,
its folder, kind, title, labels and position are read with plain Win32 calls
(the window manager's captions, no message to the dialog, about half a
millisecond), then its file name and file types from the dialog's own thread
within a small time budget - all asked at once, each from a thread of its
own, because a dialog that has just appeared answers only between bursts of
its own work and answers everything waiting when it does. Meanwhile the ready
picker is taken, put over the dialog (same monitor and scale, owned like it)
and its footer names the application. Then it is bound
(`MainWindow.RebindAsync`: the previous state goes, the folder becomes the one
tile root), drawn while still cloaked, put directly over the dialog (without
its owner, whose thread is busy with the dialog it has just shown) and
uncloaked once the frame showing the folder exists: its first frame on screen
is the finished picture, over the dialog. It is placed after the window just
above the dialog rather than asked to the top: Windows does not always honour
"top" from a process that is not in the foreground, and in about one early
test in four it had left the picker under the dialog it was to cover. Until
the dialog is hidden, a dialog that comes up over the picker again (its
program brings it forward, or a click on the program's window does) is covered
again at the picker's next check, five times a second (54-251 ms in the
tests), and in real use the keyboard comes back to the picker with it. UI
Automation reads the full contract meanwhile; only when it
confirms the dialog is mirrorable is the watchdog's lease armed, the original
hidden and OK enabled. The quick read and the full read resolve a folder the
same way, so a picker shown early is never taken back for its folder. Anything
the Win32 read cannot vouch for - a virtual folder, a network or slow path,
unreadable file types, an extra control of a kind the picker never mirrors -
is not shown early: the full read decides, and a dialog that stays with
Windows has never been hidden or written to. After a dialog the used window
is closed and the next one prepared once the worker is idle; it is ready as
soon as it has been shown cloaked. The log has one `timing` line per
replacement: from recognition to the picker's frame on screen and to "ready to
accept", with the stages between and the recognition's own timestamp.

What the waiting worker costs is kept to the window itself. Its picker shows
the application's own folder (a handful of files that never change) and
watches nothing on disk while it waits - no volume watch, no folder
registered - so changes on disk cost it nothing; bound to a dialog, it
watches the dialog's folder at once and the folders on the way to it once it
is on screen. The tree canvas behind the tiles runs no timer at rest (Nodify's
auto-pan check runs only while something is dragged in it, in every window),
and the Shell's menu handlers, which an ordinary window keeps loaded for quick
right-clicks, are not loaded into the worker. Measured with a test copy at
rest: about 30 ms of CPU a minute, and under 50 ms a minute while 120,000
files were created and deleted around it (before: 0.75 s, and 1.6 s under
that churn); about 180 MB private memory, 1,200 handles, 32 USER and 62 GDI
objects and 58 MB of dedicated GPU memory (before: 230 MB, 1,970, 81, 166 and
about 110 MB). Every picker window now gives its icon thread back when it
closes (each closed window used to leave one, with its hidden window and
handles); a worker is still replaced after 400 dialogs, or at once should its
USER objects or handles run high.

Out-of-context hooks are asynchronous: this mechanism cannot guarantee that
UltraExplorer appears before the original's very first frame. The original
remains available until the replacement is presented and the watchdog is
armed. A prepared worker is recycled after a provider timeout, so abandoned
COM calls and queued option writes do not contaminate later requests.

## Recovery

A hidden original is protected by a lease and an independent watchdog process
(`--dialog-guardian`). The lease, in `dialog-integration/sessions/`, records the
exact window, its process, the replacement process and its start time, a random
cookie also stored as a property on the window, and the window's original
transparency, so a reused window handle can never be touched.

- The window is hidden only after the watchdog has taken the lease. Windows
  may refuse to cloak another program's window; the fallback makes it a
  zero-alpha layered window, which keeps its UI Automation tree and lets the
  mouse through. The original is never moved, reparented or disabled, and its
  program keeps its own modal loop throughout.
- If the replacement closes unexpectedly, stops responding for five seconds,
  or the watchdog itself stops, the original is made visible with its exact
  previous transparency and the mode is paused (switched off, with the reason
  shown in Settings). The watchdog only ever ends the replacement process it
  was given, never the program that owns the dialog.
- If the program raises a window of its own over the hidden dialog, the dialog
  is shown again at once and the picker steps aside.
- Every start of UltraExplorer, of the listener and of the watchdog heals what
  a crash, a logoff or a killed process tree left behind: a dialog whose
  replacement process is gone is made visible again, and lease files of windows
  that no longer exist, or left without their lease, are deleted. The listener
  repeats this every two seconds, so a dialog is put back even if the
  replacement and the watchdog both vanished.

By hand: **Ctrl+Alt+Shift+Esc**; the tray icon's menu (**Restore Windows
dialogs and pause**); **Settings → File dialogs → Restore and pause**; or from a
shell:

```powershell
UltraExplorer.exe --dialog-recover
```

Each restores every dialog in progress and pauses the mode. Turn it on again in
Settings.

## Files

In UltraExplorer's state folder (`%LOCALAPPDATA%\UltraExplorer`):

- `dialog-integration.json`: on/off, application exceptions, last recovery reason;
- `dialog-integration/integration.log`: one line per replacement and per
  recovery, with the process id; past 256 KB it becomes `integration.log.old`,
  so the two stay under 512 KB;
- `dialog-integration/sessions/`: short-lived leases, removed when a
  replacement ends or by the healing described above.

A replacement reads the normal favourites and settings but never writes the
canvas workspace.

## Checks

Requires the Windows .NET 10 SDK. `FlaUI.UIA3` 5.0.0 is pinned; its UI
Automation calls run on a dedicated MTA thread with connection and transaction
timeouts.

```powershell
dotnet build UltraExplorer.sln -c Release
# headless (contract, placement, over the dialog in the z-order, sign-in guard, lease healing, log cap)
$env:ULTRAEXPLORER_STATE_DIR = "$env:TEMP\ue-dialog-checks"
tests/ViewAllSmoke/bin/Release/net10.0-windows/win-x64/ViewAllSmoke.exe --only DialogIntegrationChecks,PickerFilters,PickerCommandLine,PickerNames,PickerSessionRules,PickerValidation,PickerGraphRules,PickerTileChecks
# the prepared picker as it waits (nothing watched on disk, no timer) needs a real window: it is in
tests/ViewAllSmoke/bin/Release/net10.0-windows/win-x64/ViewAllSmoke.exe --only SettingsChecks
# interactive: real Windows dialogs, needs a desktop with a second monitor
tests/NativeDialogProxySmoke/bin/Release/net10.0-windows/win-x64/NativeDialogProxySmoke.exe src/UltraExplorer/bin/Release/net10.0-windows/win-x64/UltraExplorer.exe "$env:TEMP\ue-dialog-proxy" [case,case...]
```

The state checks of `DialogIntegrationChecks` run only with
`ULTRAEXPLORER_STATE_DIR` set, so they can never touch the real settings.

The interactive suite starts a fixture process that shows the real
`CLSID_FileOpenDialog` / `CLSID_FileSaveDialog` with `IFileDialogCustomize`
controls, an isolated listener limited to that fixture's process id, and the
replacement. Its cases: `open`, `open-fresh`, `open-large` (4,000 files),
`open-profile` (the user's own folder, which the dialog's address names
without a path: served, and never shown early only to be taken back),
`multi`, `save`, `save-fresh`,
`save-downloads` (a Chrome-like HTML save in Downloads), `save-custom`
(check box, list, radio choice and text reach the program's `OnFileOk`),
`save-extension` (the program's default extension is its own), `save-repeat`
and `save-sequence` (several dialogs in a row, each in a folder of its own,
all served by prepared pickers of one worker: one tile root each, the right
folder each time), `save-recycle` (a worker at its limit is replaced after
its dialog, and the next dialog is served by the new one), `folder`,
`cancel`, `fallback` (Use the Windows dialog this time), `exclude`,
`unsupported` (an extra push button keeps the Windows dialog), `crash` (the
replacement is killed), `guard-crash` (the watchdog is killed), `stall` (the
replacement's UI hangs) and `overwrite` (the program's own warning appears and
is answered), `early-cancel`, `early-fallback`, `early-off` and `read-timeout`
(a fixture-only delayed provider checks cancellation, mode-off and the deadline
while the original is already protected and hidden, and that the provisional picker is above the
original it covers; in `early-off` and `read-timeout` the runner also puts the
picker just under the original, nothing activated, and the picker must be back
over it within a second), and `idle-worker` (the prepared worker with no dialog:
its window only cloaked, under 100 ms of CPU in 15 s).

Programs show the same dialogs in other ways, and the fixture shows them each
way (`tests/NativeDialogProxySmoke/DialogProducers.cs` and the fixture's edge
modes). WinForms: `winforms-open` (no Filter), `winforms-open-filter` (three
types, FilterIndex 2 must come back), `winforms-open-multi`,
`winforms-open-initialdir` (a bare name resolves in the InitialDirectory),
`winforms-save` (DefaultExt and AddExtension, no types),
`winforms-save-overwrite` (OverwritePrompt, answered) and `winforms-folder`
(the Vista FolderBrowserDialog, UseDescriptionForTitle). WPF: `wpf-open`,
`wpf-save` (FilterIndex 2, DefaultExt) and `wpf-folder` (OpenFolderDialog).
Win32: `win32-open`, `win32-open-multi` and `win32-save` (GetOpenFileNameW /
GetSaveFileNameW with OFN_EXPLORER and no hook, ten types with long Russian
labels), and `win32-hook` (a hook procedure, so Windows' older Explorer-style
dialog: looked at and left with Windows, the log saying why, nothing shown or
hidden, its own Cancel reaching the program). The common item dialog's edge
cases: `open-types24` (type 17 of 24),
`open-psdlabel` (a type named "Photoshop image" without its pattern, which
Windows itself shows as "Photoshop image (*.psd)"), `save-defaultname` (a
proposed name without an extension, accepted as the dialog shows it),
`open-cyrillic` (a folder with spaces and Cyrillic letters in its path),
`folder-driveroot` (a folder picker in `C:\`) and `save-overwrite-prompt`
(FOS_OVERWRITEPROMPT on the proposed, existing file). And `shbrowse`, the
legacy SHBrowseForFolder tree, which stays with Windows: never taken for a file
dialog, nothing shown or written, and its own Cancel reaches the program.

Assertions read what the program itself received. Each case prints the
replacement's `timing` lines, and the run ends with their median and worst, separately for dialogs
already open when the listener started and dialogs that appeared while it was
waiting for them: the `-fresh` cases (the fixture shows its dialog only once
the prepared worker is ready and settled, as in real use) and the later
dialogs of `save-repeat` and `save-sequence`. For those the runner's own
WinEvent hook times the dialog's appearance, so their lines also give
dialog shown -> recognised, and shown -> our frame and -> ready.
`NATIVE_DIALOG_SMOKE_PREWARM=1` makes every case wait for the prepared
worker; without it most cases exercise the per-dialog process.
`NATIVE_DIALOG_SMOKE_NO_CAPTURE=1` leaves out the test copy's picture of the
picker (`--capture`, some 50 ms of its interface thread), for timings as the
user gets them.

Every window it opens is on a secondary monitor and none may take the
foreground: the fixture's dialog is owned by an invisible window there and
refuses activation, UltraExplorer's copies run with `ULTRAEXPLORER_TEST_WINDOW=1`
(which makes them place themselves there and never activate anything), and a
WinEvent hook fails the case if any test window becomes the foreground window
(the line says how long before it the user last touched the mouse or keyboard
and whether the pointer was over that window, as a click on a test window
activates it too).
Not every producer centres its dialog on the owner (GetOpenFileNameW and WPF
put it at the owner's corner), so for the WinForms, WPF, Win32 and
SHBrowseForFolder producers the fixture also keeps every top-level dialog it
creates inside that monitor's work area, and logs any move it had to shorten;
the WinForms and WPF producers make the process system-DPI-aware before
anything is measured, as those programs are from their start.
It refuses to run without a second monitor, and refuses a state folder inside
the real one. It holds `Local\UltraExplorer.NativeBench.Secondary` for its
whole run, windows and cleanup included, so it never shares that monitor with
another test run that measures pixels there; it waits for that run at most
`NATIVE_DIALOG_SMOKE_BENCH_WAIT_MINUTES` (30) and then refuses to run.

Not covered by the automated cases, because they would take the user's
keyboard: the hand-over of the keyboard from the dialog to the picker, and
bringing the program's own warning to the front, in a normal (non-test) copy.

## Background

QuickSwitch was looked at: it switches the folder of an open dialog rather than
replacing it and does not carry custom controls; none of its GPL code is used
in this MIT application. See [FlaUI](https://github.com/FlaUI/FlaUI),
[WinEvent hooks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook),
[UI Automation threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)
and [layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
