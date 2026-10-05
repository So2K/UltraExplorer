# Per-user folder registration

`Services/ShellReplacementRegistration.cs` exposes `Reconcile(bool enabled)` and
`IsAllowed`. The lifecycle owner calls it together with dialog integration. A
failed operation throws; a partial registration is rolled back using its receipt.
Development and tests have not changed the user's live folder associations.

Registration writes only these targets in the native 64-bit HKCU view:

| Relative to `HKCU\Software\Classes` | Values | Command |
| --- | --- | --- |
| `Folder\shell`, `Directory\shell`, `Drive\shell` | default value | `open` |
| Each class's `shell\open\command` and `shell\explore\command` | default and `DelegateExecute` | quoted executable, `--shell-request --open-folder "%1"`; delegate empty |
| `CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command` | default and `DelegateExecute` | quoted executable, `--shell-request --home`; delegate empty |

Folder association lookup is distinct from executing an `.exe`: Microsoft
documents the object verb lookup and separate executable launch behavior in
[Launching Applications](https://learn.microsoft.com/en-us/windows/win32/shell/launch).
`Directory` covers filesystem folders; `Folder` also covers Shell containers, as
described in [Association Arrays](https://learn.microsoft.com/en-us/windows/win32/shell/fa-associationarray).
The request router must therefore preserve native handling for unsupported
virtual Shell namespaces without recursively invoking the replaced default verb.

`Folder`, `Directory`, and `Drive` class keys are shared between architectures on
current Windows. `Classes\CLSID` is redirected; the native 64-bit view is used for
the Windows Shell launcher. See [Registry Keys Affected by WOW64](https://learn.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys).
After reconciliation the facade sends `SHCNE_ASSOCCHANGED` with `SHCNF_IDLIST`,
following [SHChangeNotify](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify).

## Receipt and recovery

`%LOCALAPPDATA%\UltraExplorer\shell-replacement.json` is an atomically replaced,
flushed receipt, saved before the first registry write. Each entry records its
original raw registry type and bytes, or absence, plus the values owned by this
registration. A separate list records every ancestor key that was absent.
Expanded environment strings are backed up literally. Existing class metadata
and unrelated sibling keys are not rewritten.

The backup is read from HKCU itself, never the merged HKCR view. This preserves
absence: removing a temporary user override exposes the original machine-level
registration. Microsoft's [Merged View of HKEY_CLASSES_ROOT](https://learn.microsoft.com/en-us/windows/win32/sysinfo/merged-view-of-hkey-classes-root)
explains why backing up the merged value would lose that distinction.

Off restores a value only when its current type and bytes still match an owned
value. A later user or installer edit survives both repeated on reconciliation
and off. Only keys created by this registration and still empty are removed;
the complete CLSID is never deleted wholesale. Moving the installed executable
retains the initial backup and journals the new owned command before rewriting it.
An invalid or wrong-root receipt causes an error before any registry mutation.
The per-user mutex prevents two UltraExplorer windows racing with each other;
other applications can still edit registry values concurrently.

Test window, isolated state, fixture-only process, benchmark, and snapshot copies
cannot invoke the production registry facade. The engine accepts an injected
`RegistryKey`, allowing its real native registry implementation to be tested
under a disposable random subtree.

Run:

```powershell
dotnet run --project tests/ShellRegistrationSmoke/ShellRegistrationSmoke.csproj -c Release
```

Verified: 33 assertions for exact topology/type/byte restoration, idempotency,
prepared receipt recovery, rollback after four successful writes, executable
migration, user edits and siblings, wrong-root rejection, failed backup, and
independent production guards. The fixture removes its own unique HKCU subtree.

## Existing implementations inspected on 2026-10-01

| Candidate | Actual mechanism | Material limits |
| --- | --- | --- |
| Files, commit `0e3c17ca44d143fb656be27eacf2b25c22a62043` | HKCU Folder open/explore commands; blank delegates; launcher CLSID command | Static undo hardcodes Explorer defaults and removes the launcher CLSID; no exact prior-state backup; registry method is described as unsupported. Repository contains MIT and MPL licenses. |
| Explorer++, commit `adf72c76b883271427ba16c39f9d6f5011488a42` | Custom Directory/Folder default verb plus registered Shell windows for selection requests | GPL-3.0-only source; static default undo cannot restore another prior manager. Best reference for the documented COM route; source is not copied here. |
| Tablacus Explorer Addons, commit `3b627eb1979c7d9541569a3c329f863ada0b42bb`, Import Explorer 1.12 | Enumerates visible non-busy `Shell.Application.Windows`, opens matching views and transfers focused item before quitting Explorer | MIT; manual import rather than interception. Hides a source before destination success and catches errors silently; this failure behavior should not be reused. |
| OldExplorer, commit `eb30d4e5133492c0c01f0674500df3e8dfa95af5` | Same launcher CLSID/blank delegate to change no-argument Explorer launch | MIT; arguments bypass it; author reports default taskbar icon bypass after Windows build 26100.4202. Undo deletes the complete per-user CLSID. |
| One Commander | Separate experimental folder-default and Win+E mechanisms | Vendor documentation distinguishes the routes; no reusable implementation source identified. |
| Directory Opus | Explorer replacement plus separate Win+E handling | Vendor explicitly excludes Open/Save dialogs, Control Panel, and some explicit Explorer launches; no reusable implementation source identified. |

Sources: [Files set script](https://github.com/files-community/Files/blob/0e3c17ca44d143fb656be27eacf2b25c22a62043/src/Files.App/Assets/FilesOpenDialog/SetFilesAsDefault.reg),
[Files undo script](https://github.com/files-community/Files/blob/0e3c17ca44d143fb656be27eacf2b25c22a62043/src/Files.App/Assets/FilesOpenDialog/UnsetFilesAsDefault.reg),
[Files unsupported guide](https://github.com/files-community/files-community.github.io/blob/main/docs/articles/replace-file-explorer.md),
[Explorer++ registration](https://github.com/derceg/explorerplusplus/blob/adf72c76b883271427ba16c39f9d6f5011488a42/Explorer%2B%2B/Helper/SetDefaultFileManager.cpp),
[Tablacus import source](https://github.com/tablacus/TablacusExplorerAddons/blob/3b627eb1979c7d9541569a3c329f863ada0b42bb/importexplorer/script.js),
[OldExplorer documentation](https://github.com/LesFerch/OldExplorer),
[One Commander Advanced](https://www.onecommander.com/help/3._Full_reference_guide/Settings/Advanced.html),
[Directory Opus limitations](https://docs.dopus.com/doku.php?id=basic_concepts%3Aexplorer_replacement).

## Reveal/select registration

Folder registry commands carry the parent path, but do not carry selections from
`SHOpenFolderAndSelectItems`. The API can reuse an existing Shell window, so a
registry-only implementation cannot promise highlighted files. Microsoft's
[SHOpenFolderAndSelectItems](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems)
also defines multiple selected children, zero-child single-item handling, edit
mode, and desktop selection.

`Services/FolderShellWindowRegistration.cs` now implements this independent
adapter. The lifecycle owner calls `Attach(MainWindow)`; it observes the current
filesystem folder and integration setting. `Refresh(false)`, off setting changes,
unsupported namespaces and window close revoke its own registration. Filesystem
navigation replaces the pending/open registration pair for the new folder and
reports it with `OnNavigate`. Only UltraExplorer's own HWND is registered.

The native `IShellView.SelectItem` method copies a relative PIDL before sending
work from a COM RPC thread to the WPF STA. A bounded dispatcher wait avoids an
indefinite native caller stall, and the managed copy remains safe if the caller
times out. Ordinary selection calls arriving together are batched for 35 ms,
preserving multiple selected files and a directory selected in its parent, then
passed to `ApplyFolderInvocationAsync` as an exact reveal invocation. Unsupported
rename, positioning, check state, deselection and no-focus flags retain failure
HRESULTs. Selection readiness remains the folder navigation API's responsibility.

Run the genuine cross-process native API checks with:

```powershell
dotnet run --project tests/ShellWindowSelectionSmoke/ShellWindowSelectionSmoke.csproj -c Release
```

Verified: 21 assertions using `SHOpenFolderAndSelectItems` in a separate test
process for single and multiple items, selected directories, and `cidl=0`.
Navigation, virtual namespace refusal and revoke are tested too. The test owns
a disabled message-only HWND and unique temporary files. Before invoking the opening
API, the client requires `FindWindowSW` to return exactly that HWND; a failed
registration therefore cannot accidentally launch native Explorer. The first
before/after foreground equality measurement passed locally, then failed during
a root rerun. That measurement conflated unrelated user activity with test
activation. Replacing it with a foreground-event watch scoped to the exact own
HWND and launched client PID/creation time observed an event naming the fixture;
the fixture was not foreground at assertion time. Moving to a message-only HWND
still observed such an event. Those failures are preserved in
`artifacts/shell-window-selection-foreground-observation.json` and
`artifacts/shell-window-selection-foreground-observation-message-only.json`.

The corrected fixture adds a callback inside its own process, bound only to the
fixture UI thread (`WH_CBT`, module handle zero, exact current thread ID). It
refuses `HCBT_ACTIVATE` and `HCBT_SETFOCUS` only for the exact fixture HWND or its
children with the same verified source PID, and passes every other callback
along. This changes the test fixture; production Shell view activation remains
intended behavior. There is no native DLL injection or external process/thread
hook. The guarded run passed `DefaultDesktopNoOwnActivation` while real native
selection still reached the adapter. Unrelated user foreground changes are
allowed, and failure details identify only owned HWND/PID/process creation time.

Measurement limits: foreground event callbacks are asynchronous notifications;
a notification naming an inactive HWND alone does not establish physical focus
at the earlier instant. Polling every 10 ms does not inspect every instant. The
test combines that owned-only observation with prevention at the fixture's own
activation/focus callback. It never switches the input desktop or activates or
controls another application's windows. No registry association was written by
this test. A cold
launch through production registry routes still needs the combined route fixture.

Explorer++ provides a tested reference for receiving these calls through
public COM APIs in [BrowsingHandler.cpp](https://github.com/derceg/explorerplusplus/blob/adf72c76b883271427ba16c39f9d6f5011488a42/Explorer%2B%2B/Explorer%2B%2B/ShellBrowser/BrowsingHandler.cpp#L112):

1. Register a pending window with its absolute folder PIDL in a byte-array VARIANT,
   current STA thread ID, empty root VARIANT and `SWC_BROWSER`.
2. Register an `IWebBrowserApp` object and top-level HWND. Its `Document` exposes
   `IDispatch` and `IServiceProvider`; the folder-view service exposes `IShellView`.
3. Shell obtains that service and invokes `IShellView.SelectItem` with a relative
   child PIDL. Combine it with the folder PIDL and forward selection to the UI.
4. Notify navigation with `OnNavigate`; revoke registrations when closed or off.

The code's cold-start finding is significant: `RegisterPending` is needed before
`Register`; `OnNavigate` alone does not receive a new launch's selection request.
It uses `SWC_BROWSER=1`, despite the SDK naming that type for Internet Explorer.
This operational behavior needs Windows build testing. Implement independently
from [IShellWindows](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nn-exdisp-ishellwindows),
[RegisterPending](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-registerpending),
[Register](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-register),
and [IShellView.SelectItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellview-selectitem).

Suggested independent adapter boundary:

```csharp
// The owner creates this on the WPF STA and retains it for the view lifetime.
IDisposable RegisterShellView(nint topLevelWindow, string folder,
    Action<IReadOnlyList<string>, ShellSelectionOptions> select);
// Returned registration also supports Navigate(newFolder) and Revoke().
```

Direct `CreateProcess` of `C:\Windows\explorer.exe` with an explicit path,
existing Explorer tab navigation, special namespaces and changing Windows
taskbar launch paths remain separate routes. The inspected references do not
provide a universal pre-launch interceptor that also satisfies the no-injection,
no-system-file-patching, no-core-shell-replacement constraint. External Shell
window handoff can cover some bypasses after opening; it needs acknowledged
destination readiness and safe source-window/tab ownership, and cannot honestly
guarantee zero flicker for every application.
