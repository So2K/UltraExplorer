# Tree, filesystem, watcher and persistence stability review

Reviewed on 2026-10-02 against shared starting revision `7ba3877`.
This pass fixes existing behavior only. Tests use generated directories, isolated
state and fake volumes; no installed integration setting or user file is changed.

## Confirmed findings and fixes

### P2: a failed directory stayed failed after disk changes

`NestedTree.OnFolderChanged` marked failed folders stale, but only asked loaded
folders to refresh. `Request` ignored that stale flag for failed folders: a
non-retryable failure never recovered automatically, and a transient failure
waited twenty seconds despite a new structural event proving the disk changed.

Reproduction: load a directory, fail its next explicit refresh, restore the
reader and send a structural watch change. Its old error and listing remain
until further manual action. This failed for both retryable and non-retryable
errors in the new fixture.

Fix: a failed visible folder is requested after the real change; a failed
folder whose listing is stale may retry. Without new evidence the original
retry delay remains. Off-screen folders stay lazy and recover when drawn again.
The queue and worker counts, sparse hierarchy and reparse guard are unchanged.

### P2: the older canvas save could overwrite the latest selection and camera

`ViewAllWorkspaceStore` used independent temporary files but did not serialize
saves. A delayed older canvas snapshot could replace a smaller newer close
snapshot last. `WorkspaceStore` and `FolderMarkService` already serialized
equivalent overlaps.

Reproduction: issue a save of 30,000 nodes, immediately issue a newer save with
a different active path and zoom, and await both. Before the fix the saved file
held the old snapshot. No artificial I/O hook is involved.

Fix: serialize saves within the store with the same semaphore pattern used by
the other stores. Atomic replacement and independent temporary files remain.
Cancellation preserves the previous state and leaves the gate usable.

### P2: extra-root descriptions disagreed about system folders

`DescribeDirectoryAsync` recognized Hidden alone, while `DescribeEntryAsync`
and enumeration recognized Hidden or System. A system-only folder therefore
had different visibility depending on how it reached the tree.

Reproduction: create a temporary system-only directory, describe it through
both routes and compare `IsHidden`. Fix: use the same Hidden/System rule.

## Validation

`TreeWatchStabilityChecks` covers the three reproductions, recovery on-screen
and after returning from off-screen, unchanged retry behavior without new
events, cancellation, latest snapshot contents and temporary-file cleanup.
The unpatched reproductions are recorded in
`artifacts/stability-tree-watch-before.log` (5/10 assertions passed; the canceled
save preservation assertion also depended on the already incorrect prior save).

Existing coverage was exercised for nested listing reconciliation, cancellation
and disposal, read lanes and expiry, sparse path deletion/recreation, real NTFS
watch records, rename/move, content patches, overflow, suspend/rearm/drop,
polling and retries, network aliases, marked paths and corrupt state recovery.

The first broader run found two races in the existing gated read-queue fixture:
it cleared the reader-entry log while reserved workers had not entered yet.
The fixture now waits for all reserved reader entries before clearing the log;
its exact ordering and expiry assertions remain. An unrelated 200 ns watch
microbenchmark also exceeded its threshold during parallel builds; its budget
and production hot path were not changed.

The focused rerun after the fixture correction passed **111/111**, covering
all fourteen new assertions, the complete read queue and hub timing checks.
The unchanged watch-noise median measured **150 ns** (200 ns budget), with
zero allocations on misses. The preceding broader run passed **367/370**;
its three failures were the two corrected fixture races and that unchanged
timing check. Build completed with zero warnings and errors. Logs are
`artifacts/stability-tree-watch-after.log` and
`artifacts/stability-tree-watch-focused.log`.

## Examined lifecycle invariants and remaining limits

- A read is accepted only with its current ticket; detached folders are not
  attached again. Cancellation completes explicit waiters and disposal stops
  queued work. Already-running native filesystem calls may finish afterward.
- Refresh keeps surviving child identities, updates their metadata on the owner
  thread and unregisters removed descendants in bounded sweeps. Sparse named
  ancestors are not enumerated merely to reach a leaf.
- Structural watch events use the ordinary queue; content-only patches preserve
  the listing and mark mismatches for reread. Epoch changes cover overflow and
  rearming. Network read lanes stay separate from local lanes.
- Stores retain atomic replacement and corruption quarantine. The new save
  serialization is per store instance; it does not coordinate different
  processes writing the same custom path.
- `NestedTree.Invalidate` has no production callers. Its behavior during an
  already-running read and the cumulative diagnostic `LoadedCount` accounting
  were noted as API/diagnostic follow-ups rather than expanding this product
  stabilization change.
- Real SMB disconnect, physical device removal and cloud placeholder behavior
  were not induced. Existing tests simulate these boundaries or exercise owned
  local handles; this is not proof against all filesystem/provider failures.

## Follow-up: MainViewModel save ordering

Root identified a second save-ordering boundary above `WorkspaceStore`:
`MainViewModel.SaveNowAsync` captured settings, then awaited a workspace read
before entering the store's write gate. Serializing only writes cannot preserve
request order if those reads finish in the opposite order.

`MainSaveOrderingChecks` confirmed this with the real VM and stores. Its valid
workspace includes an ignored 8 MB JSON field to cross actual asynchronous read
boundaries. Independent held synchronization contexts finish the older read,
hold the VM continuation, complete the newer save (active pane 1), then resume
the older captured save. The final file returned to active pane 0. The read
stream is verified closed before resuming either write, so this was not a
sharing-violation result. No reflection, production hook or arbitrary delay
is used. The unpatched proof is
`artifacts/stability-main-save-ordering-before.log` (**5/6**).

Root's correction gates the entire main save operation before its capture,
read, merge and write. The test now recognizes a queued newer save through an
owned exclusive read boundary: an unguarded call starts a temporary write,
whereas the corrected call waits before reading or writing. It releases the
gate holder first when needed and verifies both saves complete, active pane 1
remains, and two subsequent manual flushes persist changed values without a
deadlock. A round-robin cleanup pumps both held contexts even after a failed
assertion.

`MainSaveOrderingChecks`, `TreeWatchStabilityChecks` and `Persistence` passed
**33/33** after the root correction; build had zero warnings/errors. Log:
`artifacts/stability-main-save-ordering-after.log`. This follow-up edited only
the owned regression test and this report; VM production changes belong to root.

## Full-suite follow-up: sort shared-budget fixture

The retained full run (`artifacts/stability-full.log`, line 1476) failed the
assertion that a background sort slice must skip after more than a slice of
on-demand work. The fixture used 12 ms of elapsed time for its entire loop,
but production charges only calls which actually relayout a folder. Stamp-only
and empty folders, pauses between calls and other loop costs could use those
12 ms without exhausting the production 4 ms allowance. The production sort
budget implementation is unchanged from `7ba3877`.

The isolated CPU canvas/sort run passed **291/291** before any change, confirming
the failure depends on accumulated suite state/load rather than reproducing
consistently in a fresh process. The fixture now retains its 12 ms workload
target but accumulates time only around stale folders which must reorder at
least two files. It leaves the drive untouched and explicitly checks that the
actual workload exceeds a 4 ms slice before keeping the original exact
skip-one-slice and next-slice-resumes assertions.

After this test-only correction the CPU canvas/sort group passed **292/292**,
including 12.0 ms over 13,435 real file reorders. No production budget, rendering
implementation or test performance threshold was changed. Logs:
`artifacts/stability-sort-before.log`, `artifacts/stability-sort-after.log`.

The later full qualification exposed another dependency in the same fixture:
the notification check subscribed after the demand-work scenario had placed
33,956 folders and consumed two background slices, then required the *remaining*
work to span multiple slices. In the warm full run that remainder correctly fit
in one slice (`artifacts/stability-qualification.log`, line 1479).

That notification fixture now finishes the preceding scenario and subscribes
before a fresh complete type-sort pass. It keeps the same requirements:
multiple slices, exactly one `Changed` event, no event before sorting finishes.
Only test sequencing changed; production notifications and budgets are intact.
The CPU-only final canvas/sort run passed **292/292**, including a complete pass
of 18 slices with one final notification, with zero build warnings/errors.
Log: `artifacts/stability-sort-final.log`.
