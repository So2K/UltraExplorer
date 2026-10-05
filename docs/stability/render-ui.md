# Renderer and window stability review, 2026-10-02

Scope: existing nested canvas, camera, selection/input, GPU/atlas lifetime,
window and split-pane teardown, search result hit routing, context menus and
drag callbacks. No navigation or rendering feature was added.

## Fixed findings

- **P2: late asynchronous camera request replaced the latest destination.**
  `NestedCanvas.FlyToPathAsync` awaited a folder read without checking whether
  another destination, user pan/zoom, or tree had superseded it. An owned fake
  directory reader held one destination while a later destination completed.
  Before the fix the delayed request then moved the camera back. A shared
  camera request generation now rejects stale path and saved-camera requests;
  direct pan/zoom, flights, Fit All and tree swaps invalidate older requests.
- **P2: nonfinite saved camera coordinates poisoned the view.**
  `RestoreCameraAsync` checked width but accepted NaN/infinite X/Y and finite
  values whose scaling overflowed. Coordinates are validated before assigning
  the camera; invalid pan/zoom input is also ignored. The existing view remains
  intact rather than becoming blank or feeding invalid rectangles to a renderer.
- **P2: split pane teardown allowed queued Enter/late flights to resume.**
  `NestedPane.Detach` previously disposed its tree without invalidating its
  flight generation or detaching the canvas from that tree. Teardown is now
  idempotent, invalidates pending flights, clears readiness/canvas tree, unhooks
  pane callbacks and rejects deferred Enter/Open/menu/source-refresh work.
  A real temporary directory fixture closes a pane while its named path is
  still being described and verifies that the late flight does not revive it.
- **P1: glyph atlas disposal could free pages beneath a slow worker.**
  Disposal waited two seconds and then freed native pages even if the worker
  was still rasterizing/copying. The worker now stops queued glyph work and
  owns final page release if the bounded wait expires. Startup publication of
  the worker is synchronized with disposal. A request racing queue closure
  is marked unavailable and removed from pending instead of throwing. The
  deterministic fixture stalls the owned worker, queues another glyph, disposes
  while stalled and verifies deferred page release with no late publication.
- **P2 preventive teardown follow-through:** window startup, nested initialization
  and drive rescan now recheck close state after asynchronous work, avoiding
  late control mutation or split creation after window teardown. These guards
  were established by call-path review; actual drive enumeration latency was
  not injected into user applications.

## Evidence

- Initial camera regression run against unchanged product code: **2/7 passed**.
  Failure was reproduced for delayed destination precedence and invalid
  saved-coordinate handling.
- Fixed focused `NestedCameraChecks`: **15/15 passed**.
- CPU `NestedCameraChecks,SettingsChecks`: **284/284 passed**, including
  overlapping restores, search hit routing, split navigation/selection,
  the newly added pending-flight pane close and existing picker/settings cases.
- Focused build: **0 warnings, 0 errors**.
- Log: `artifacts/stability-render-ui-cpu.log`.
- Focused `GlyphDisposalChecks,NestedCameraChecks`: **19/19 passed**; owned
  glyph worker survives the two-second disposal timeout, publishes no queued
  glyph after disposal and releases its native pages on completion.
- Log: `artifacts/stability-render-ui-lifetime.log`.

## Follow-up: actual shell view-model disposal

`ViewModelLifetimeChecks` supplies fourteen public behavior checks using real
`MainViewModel` instances with explicit temporary workspace/tree/marks paths.
An eight-megabyte valid single note makes asynchronous startup remain pending
at the exact instant the model is disposed; no artificial product hook or
private-field reflection is used. A second case calls initialization after
disposal, and a third fully initializes before closure and then requests a save.

Baseline before the root's shell model fix: **6/14 passed**. In both incomplete
startup cases, the already disposed model added Home=1, QuickAccess=7, Drives=5
and Network=1 (fourteen collection changes) and emitted two late property
notifications. Its graph roots correctly stayed empty under the root's existing
graph lifetime fix. A disposed model still processed former sort notifications;
an initialized disposed model's `SaveNowAsync` changed its owned workspace.
Logs: `artifacts/stability-main-model-full-before.log` and
`artifacts/stability-main-model-detail.log`.

After the root added disposal checks at startup's asynchronous boundaries,
detached the sort notification and gated saving after disposal, the same real
model fixtures pass with **Home/QuickAccess/Drives/Network/Roots=0**, **zero
collection mutations** and **zero late property notifications**. The fully
initialized model no longer handles released sort callbacks or writes its
workspace after disposal.

Fresh build: **0 warnings, 0 errors**. Combined CPU
`ViewModelLifetimeChecks,PickerFavoritePreferencesChecks,SettingsChecks,GlyphDisposalChecks`:
**303/303 passed**. Log: `artifacts/stability-main-model-after.log`.

The root owns the final broader regression run and installation.

## Full-suite follow-up: short metadata and renderer fixture isolation

The initial full suite exposed three strict layer assertions failing. Isolated
current product and the preserved pre-review build both reproduced **67/70**
for `GpuRectChecks,LayerChecks`: icons, glyphs, counts and names appeared, but
all file sizes were missing. This was an existing product bug in the zoom text
change, not a new layer-setting regression. A complete short value such as
`0 B` has a natural width below two filename font units; the draw condition
mistakenly applied the minimum width for trimmed metadata even when the whole
name and whole value fit. The condition now draws complete fitting values and
keeps the existing minimum-width threshold for values requiring trimming.
No filename allocation, icon layout or rendering algorithm was replaced.

The headless rectangle reason assertion passed in isolation and failed after
the real-model lifetime fixture had intentionally loaded a CPU renderer setting.
That fixture now restores the renderer preference in `finally`, following
the existing Settings fixture pattern. The rectangle assertion remains strict.

Fresh build: **0 warnings, 0 errors**. Combined
`ViewModelLifetimeChecks,GpuRectChecks,LayerChecks,LabelZoomChecks`: **89/89 passed**.
The layer calls now include short sizes with Details on and omit them only with
Details off. The zoom checks still cover **27,045 positions**, with zero
filename-room regressions, overlaps or icon-induced shifts.
Logs: `artifacts/stability-parity-isolated-before.log`,
`artifacts/stability-parity-old-before.log`,
`artifacts/stability-parity-current-detail.log`, and
`artifacts/stability-parity-fixed.log`. No throughput claim is made from this run.

The final full-order replay found two more fixture owners of the same process
preference: `PickerFavoritePreferencesChecks` and `MainSaveOrderingChecks` both
load real CPU workspaces and did not restore the applied renderer setting.
Each now captures and restores that setting in `finally`, following the
existing Settings fixture pattern. This changes fixture isolation only;
production code and the strict headless CPU reason assertion are unchanged.
The actual suite ordering
`PickerFavoritePreferencesChecks,ViewModelLifetimeChecks,MainSaveOrderingChecks,GpuRectChecks,LayerChecks`
now passes **108/108**, including `ReasonNotOnScreen` at the rectangle check.
Fresh build has **0 warnings, 0 errors**.
Log: `artifacts/stability-renderer-fixture-order.log`.

## Native prepared-picker sequence invariant

The complete native run's one remaining sequence assertion required exactly
one root, a contract from before physical hierarchy navigation (commit
`5502a5e`). At `7ba3877` the picker already retains all ready drive roots and
focuses the caller's directory inside its physical volume. The saved sequence
diagnostics showed five roots for each of three dialogs and exact native
results under `one`, `two` and `three`; no product root accumulation appeared.

The old count assertion was replaced with four stricter checks. Owned-fixture
timing diagnostics now also snapshot exact root paths: snapshots must be
complete and match their count, every root must be a physical drive/share root,
paths must be unique, the caller's physical volume must occur exactly once,
and repeated dialogs on the same volume must preserve the exact root set with
none of their session folders promoted to a root. Ordinary user diagnostics
continue to record only the existing count.

The native test runner's two intentional guardian-kill cases now parse the
new readiness identity and verify the guardian's exact UTC start ticks before
stopping an owned process; the existing early-guardian process-tree proof is
retained. Guardian protocol implementation belongs to the integration review.

Fresh build: **0 warnings, 0 errors**. Owned secondary-monitor run of
`save-sequence,guard-crash,early-guard-loss`: **67/67 passed**, including all
new root invariants, native result identity, recovery and foreground/state
preservation. Log: `artifacts/stability-native-sequence-fixed.log`.

## Cold GPU bootstrap fixture isolation

The full suite's remaining GPU decision failure was caused by the lifetime
fixture restoring its previous renderer through `SetSettingPreference`.
That production API intentionally starts GPU warm-up for a non-CPU choice;
therefore the later strict cold-bootstrap predicate observed a worker already
started, even though `Decide` for an unhosted visual started nothing itself.
The fixture now uses `UseSavedPreference`, like the Settings and other real-model
fixtures, to restore process state without starting hardware preparation.
The product API and both GPU decision assertions are unchanged.

Fresh build: **0 warnings, 0 errors**. Actual suite-order replay
`PickerFavoritePreferencesChecks,ViewModelLifetimeChecks,MainSaveOrderingChecks,GpuDeviceChecks`:
**98/98 passed**, including CPU rendering of an unhosted visual and no bootstrap
warm-up. The same current GPU-device harness against the preserved pre-review
product assembly also passes **60/60** and both strict cold predicates.
These are offscreen functional checks; no throughput benchmark or live user
window interaction was performed.
Logs: `artifacts/stability-gpu-cold-order.log` and
`artifacts/stability-gpu-cold-old.log`.

## Coverage and limits

Reviewed GPU surface lock/unlock/fallback and per-device teardown paths,
atlas glyph publication and native-page lifetime, icon worker/queue lifecycle,
camera normalization/culling/hit ownership, startup/deferred dispatcher work,
split selection/keyboard ownership and file action callbacks. Existing
`MainWindow.ParentOf` already distinguishes visual/content sources, addressing
the previous search-highlight Run exception; no replacement was needed.

Focused checks use isolated state, temporary files and CPU drawing; they do
not manipulate the user's foreground windows or key state. Physical device
removal, broken third-party Shell handlers and real multi-monitor driver reset
remain dependent on external hardware/software; no claim is made that these
were triggered live. The root performs the sequential GPU and final broad
regression suites to avoid concurrent performance load.
