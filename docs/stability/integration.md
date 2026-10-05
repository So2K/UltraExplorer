# Windows integration stability review — 2026-10-02

Scope: folder invocation/parser/navigation; folder IPC and destination receipts;
Explorer observation/transfer/native-session exceptions; Shell view registration
and selection calls; reversible association receipts; independent Win+E lifetime;
native picker/warm-worker/guardian contracts. Review started from `7ba3877`.
No new integration modes or user features were added.

## Confirmed fixes

### P2: a failed folder request left the normal overview sparse

`MainWindow.ApplyFolderInvocationAsync` set `LoadUnfocusedRoots=false` before
navigation and restored it only on success. A deleted destination, superseded
request, or pane detachment could return early and retain the picker-like gate
in an ordinary window. The camera could later show drives without their contents.

The request now owns the gate by its exact pane and ticket. `finally` restores
only that ownership; it cannot lift a newer request's gate on the same pane.
Each continuation verifies its original pane is still attached and active.
The nested camera helper also checks the request before its own late mutation.
Changing the gate schedules a scene, so an already visible drive can load without
another click or camera movement.

Regression evidence: the actual WPF folder-router fixture on a never-switched
private desktop exercises a missing destination, supersession, pane switching,
pane removal, and subsequent valid navigation. Canvas tests use a fake directory
reader to prove visible-drive loading resumes when the gate is lifted.

### P2: the broker's timeout ended the wait but retained navigation intent

The broker used `WaitAsync(15 seconds)` without canceling the underlying window
navigation. A slow directory read could complete after the broker had abandoned
the request, and then change an existing UltraExplorer window while the native
source was still usable.

The broker now supplies that same deadline token to window navigation. The
token participates in every continuation's current-request check, including the
camera helper. Cancellation promptly releases only the matching load gate.
The original one-argument window method remains available for Shell delegates.
The validated-directory hint avoids probing the same network folder synchronously
again on the UI thread while preparing that camera flight.

Regression evidence: the actual window fixture cancels a pending request and an
already canceled request, verifies their folder stays unchanged, and verifies
their overview gate is released. No network outage is induced in the user's
session; cancellation is supplied by the controlled fixture.

### P2: a long-lived broker stopped accepting folders after 4,096 requests

All committed request IDs were retained until every window closed. Once the
dictionary contained 4,096 entries, new folder requests were refused forever
for that broker lifetime, including when all earlier work had finished.

Completed entries now expire two minutes after completion. This is beyond the
18-second client retry budget and 15-second navigation budget. Unfinished work
never expires. Recent successful and failed requests still deduplicate, payload
identity is checked again at commit, and the 4,096-entry bound remains.
Read-only readiness verification does not consume that cache.

Regression evidence: a deterministic clock reproduces the old capacity boundary,
then accepts lifetime request 4,097; 64 concurrent retries execute once; mismatched
payloads are refused; unfinished, recent, and faulted work remains deduplicated.

## Verification

- `FolderInvocationChecks,FolderRouterChecks,FolderRouteCacheChecks,DeferredViewportReadChecks`: **138/138**.
  Actual owned WPF windows/IPC execute on an inactive private desktop; no native
  Explorer is launched there. Seven independent destinations, complete selection,
  camera framing, malformed packets, abandoned offers, OFF during commit, replay,
  and state-file isolation remain covered.
- `ExplorerReplacementSmoke`: **74/74** using owned invisible HWNDs and pipes.
  Readiness/identity/nonce/selection precede source closure; OFF, timeout, late
  approval, modal input, changed tabs/views/focus and native browsing exceptions
  preserve the source. Production observer fixtures use exact owned scope.
- `NativeExplorerNavigationSmoke`: **50/50** policy, cancellation and owned
  message-window checks; no user Explorer/focus/registry changes.
- `ShellWindowSelectionSmoke`: **21/21** real Shell selection/registration
  lifecycle checks; owned HWNDs remain hidden and never activate on Default.
- `WinExplorerShortcutSmoke`: **1,596/1,596** synthetic policy and owned message
  pump checks; no global hook installation or input synthesis.
- `ShellRegistrationSmoke`: **69/69** exact rollback, legacy Win+E release,
  typed-before values, ownership preservation and isolation checks. All writes
  use one disposable `Software\UltraExplorer.Tests\ShellRegistration-*` subtree.
- Isolated Release builds: **0 warnings, 0 errors**. Focused folder log:
  `artifacts/stability-integration-regression.log`.

## Dialog-specific follow-up

Ownership was initially transferred in the root mailbox at 16:25. After Claude's
latest report remained read-only, the root reclaimed these corrections at 17:21.
The following defects describe the original source and are now fixed and verified
in [the dialog/store follow-up](dialog-stores.md):

- `DialogIntegrationStore.Update`: settings mutex is released before registry
  reconciliation; concurrent OFF/ON writers can apply stale preference values.
  Startup/rollback call sites need the same ordering contract.
- `DialogLease.IsProtected`/guardian readiness: guardian PID alone is accepted,
  without a process start identity, so PID reuse can falsely imply protection.
- `FileDialogClientStore`: independent picker processes perform uncoordinated
  read-modify-write, so one caller's last-folder update can overwrite another.
- Raw registry helpers throw `Win32Exception`, but some integration startup/
  recovery catches cover only I/O/access/security failures.

## Limits

No user-owned Explorer frame or live third-party dialog was hidden, clicked,
closed, or used for destructive failure tests. Win+E checks do not claim to
prove a physical keystroke or a hook surviving silent removal by Windows.
Virtual namespaces, multiple Explorer tabs and unsupported caller controls
retain native behavior. Provider/network waits remain externally unbounded;
their late completion is prevented from committing expired folder intent.
Native picker/guardian changes require the root's combined final regression run.

### Root follow-up at 17:21

Claude's next report had not arrived and his last scope remained read-only.
Root announced ownership of these four verified risks in mailbox 1721 and
implemented them in the separate dialog/store package. See
[dialog-stores.md](dialog-stores.md) for the exact fixes, source-linked strict
regressions, ownership and recovery protocol. The earlier transferred status
above records this work package's original handoff, not unresolved final fixes.
