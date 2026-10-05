# Explorer replacement lifecycle smoke checks

Run from the repository root:

```powershell
dotnet run --project tests/ExplorerReplacementSmoke/ExplorerReplacementSmoke.csproj
```

The test exercises the production coordinator through its injected actions and
the production router's SendAsync/ValidateReadyAsync client, packet framing and
offer/commit protocol. Real user-only named pipes carry request nonces,
destination IDs, process lifetimes, filesystem folders and complete selections.
Source and destination HWNDs are
invisible message-only windows created by this test process. Production Explorer
filters are never relaxed. The only native close endpoint checks the fixture's
handle inventory, owning PID, state and cookie before posting Close.

Application logs use a random `ULTRAEXPLORER_STATE_DIR`. Fixtures and logs are
removed only after verifying their resolved paths are exact children of the
designated temporary fixture directories. User Explorer windows, registry
settings, taskbar and desktop are not modified.

Coverage includes preparation before closure; accepted/ready receipts; request
nonce and process lifetime; exact folder, selection and destination; distinct
windows per source frame; stable retry identity; active tab/view and source
generation changes; source cookie replacement and cleanup; checkbox OFF and
disposal during awaits; timeout and late readiness; duplicate callbacks; empty
selection; pre-commit timeout without late UI effects; disabled modal frames;
native menu/move and editable-input idle rules.

Native-session checks cover existing Explorer frames, virtual This PC to disk
navigation, changes of folder/tab, Back navigation, independent new windows in
the same Explorer process, and process/thread lifetime reuse. A protected native
session creates no destination. Protection established during readiness or final
validation prevents the coordinator from closing the source.

These fixtures verify the coordinator and endpoint contract. Actual Explorer COM
reads are covered separately by the exact-HWND read-only observer probe. The
inline-edit classifier tests are deterministic input tests, rather than changing
focus or beginning Rename in a user's native window.

Private-desktop Explorer launch is deliberately excluded: `STARTUPINFO.lpDesktop`
controls the new process's initial desktop, while Explorer can delegate requests
to a previously running instance. These two documented behaviors do not provide
an isolation guarantee for a launch fixture:

- [STARTUPINFOW and lpDesktop](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow)
- [Explorer is a single-instance application](https://devblogs.microsoft.com/oldnewthing/20170328-00/?p=95845)

Out-of-context WinEvents are queued asynchronously. They cannot guarantee that
the native window has not painted its first frame before adoption:
[SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook).
