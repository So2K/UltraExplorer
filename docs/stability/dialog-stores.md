# Dialog integration and caller-store stabilization — 2026-10-02

Root took ownership after Claude's last report remained read-only. The first
isolated source-linked diagnostic reproduced four defects; no installed state,
user HWND, registry associations or benchmark lease was touched.

## Confirmed defects and corrections

- **P2, preference/registration ordering:** `Update` released its settings mutex
  after saving JSON, then applied registry/Run effects from its old snapshot.
  A stalled ON update followed by OFF reproduced durable OFF with both fake
  registration facades ON. The settings mutex now spans durable write and all
  registration effects. Startup/repair calls read the latest preference under
  that same lock; controller/agent/shortcut/router stale direct paths are routed
  through it. Strict UI enable registers before announcing ON, within the lock;
  failed durable writes roll an OFF-to-ON registration back there too.
- **P1 safety prerequisite, guardian identity:** `IsProtected` checked only a
  live PID. An unrelated owned PID reproduced a true protection predicate.
  The guardian's atomic `.ready` JSON now includes `Process` and UTC
  `StartedTicks`. Arm and later protection checks require exact PID/start plus
  UltraExplorer's executable. Missing, malformed, oversized, zero-valued or
  legacy plain-PID markers fail closed. Gone/inaccessible processes return false.
- **P2, caller state lost across processes:** uncoordinated client-store
  read/modify/write retained only **1 of 12** simultaneous caller updates in
  three pre-fix runs, leaving **4/2/6** temporary files. An absolute-path-specific
  named mutex now covers the entire mutation, reads permit atomic replacement,
  and temporary files are cleaned in `finally`. An unreadable existing cache
  does not become an empty mutation baseline: failed I/O/access reads skip the
  write and preserve its bytes. Read-only loading may still report unavailable;
  the existing corrupt-JSON fallback remains unchanged.
- **P2, raw registry failure escaped recovery:** a source-linked fake registration
  throwing `Win32Exception` reproduced an escaped exception after durable ON.
  Expected raw registry failures are now caught in reconciliation/recovery.
  Strict UI enable still reports registration failure before persisting ON.
- **P2, unreadable settings mutation baseline:** read-only settings access uses
  safe OFF defaults when the file cannot be read. Mutation must not persist
  those defaults over existing ON flags and exclusions. Update now refuses an
  actual I/O/access read failure before applying any write or registration effect;
  read-only fallback and the existing corrupt-JSON policy remain unchanged.

## Regression evidence

`tests/DialogStoreStabilityProbe` is now a strict regression executable:
**42/42 assertions passed**, exit zero only when every assertion passes.
Product Release build has **0 warnings/errors**.

The suite links the actual production store, lease parser/predicate and client
store source. Registration is substituted with in-memory facades; all native-window
dependencies throw if invoked. State is a validated GUID child beneath its own
temporary parent, and `DialogStartup.IsAllowed` must be false.

Coverage includes:

- controlled concurrent settings writes and startup repair ordering;
- strict enable rejection and rollback after an owned durable-write failure;
- positive guardian start identity, wrong start with same PID, malformed/missing
  markers, and one owned exited process;
- three runs of 12 independent simultaneous client-store instances;
- nine actual owned child processes: six writers and three concurrent clears,
  preserving every new caller and an unrelated sentinel;
- abandoned settings/client mutexes and a timed-out client mutex, followed by
  successful saving after release;
- an owned existing cache handle that denies reads but permits file replacement:
  Save/Clear preserve all old callers and exact bytes; saving resumes on release.
- the same owned read-failure boundary for integration settings: both toggles,
  exclusions and exact bytes survive, registration effects remain unchanged,
  and writes resume after release.

Log: `artifacts/stability-dialog-store-regression.log`.
The native smoke runner's two guardian-kill sites were updated by its owner to
use the production identity parser and verify the exact start before an owned
kill. Fresh Save-sequence/guardian-crash/early-guardian-loss checks: **67/67**.

## Ordering and limits

Mutex ownership remains synchronous and thread-affine. Settings acquires the
settings mutex before shell registration; shell registration never holds its
mutex while calling back into settings. No dispatcher await or UI callback is
introduced under those locks. Client mutexes are bounded to three seconds and
recover abandonment; inability to remember a folder cannot fail its dialog.

These checks do not simulate physical PID reuse or induce failures in user
applications. They exercise its exact required identity predicate with controlled
stamps. Actual full native/dialog and full unit regressions are the root's final
combined verification, following this package's source freeze.
