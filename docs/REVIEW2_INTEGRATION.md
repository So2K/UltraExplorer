# Saved review2 integration

This records the engineering checkpoint used for the v1.2.0 release.
Qualification facts below are preserved; a release-channel label does not
turn known failures or unimplemented follow-ups into successful checks.

The latest saved Claude review2 work has been combined with the published
Windows product: 90 original commits across 18 work packages, plus nine scoped
snapshots of their uncommitted follow-ups. Original worktrees and indexes were
preserved. Unrelated Linux work and local user documentation were not imported.

Additional integration corrections cover external-drop lifetime, Tags metadata
compatibility, immediate rename propagation, picker rename/freshness, stale list
rows, breadcrumb publication, indexed edge culling, window-specific prompts,
bounded share-menu preflight, early folder forwarding, per-volume read lanes,
cached beacon placement, Explorer handoff identity and cleanup, asynchronous file
commands, visibility/filter provenance and camera continuity through renames.

The current immutable `combined-pass9` self-contained Release build has zero
warnings and zero errors. Focused checks passed for Explorer (18 + 28), file
commands/cross-cluster compatibility (13 + 42), visibility/rename (36 + 109).
Additional affected checks passed: Settings 273, Proxy panes 25 on fresh state
and 25 on a copied broad-run state, LiveUpdate 71, and root integration/Shell
preflight 74. These include the real two-pane explicit-hide command bridge and
an armed volume watcher registered only after its window handle exists.
Nested child totals and substring-selected test groups must not be
added together as unique assertions.

## Deliberate limits

- Not every one of the 170 review findings is closed. The detailed private
  coordination matrix distinguishes saved fixes, current corrections, specific
  tested snapshots, partial fixes, open behavior and design decisions.
- Modern Windows `IFileOperation` replaces both the direct legacy
  `SHFileOperation` API and the Visual Basic duplicate path. Actual long-path
  file copy/move/delete/duplicate checks pass. Windows Shell still rejects a
  recursive duplicate whose destination tree exceeds its own limit, returning
  `COPYENGINE_E_PATH_TOO_DEEP_DEST` (`0x8027001E`). The application returns a
  clear error without the crashing/blocking native error UI for known long
  source/destination/result paths; it does not recursively prescan unknown
  descendant trees to promise universal path support, and does not claim
  that arbitrary long folder trees work or silently switch to a backend with
  different undo/security semantics. The strict success test remains failing.
- GPU presentation still waits for completion on the shared UI dispatcher
  (J072). A correctly synchronized texture pipeline is a separate renderer
  change, not implemented by removing the wait or moving UI-affine code into
  `Task.Run`.
- Saved graph-expansion replay and identity-preserving large paged refresh
  (J019/J046), plus remaining low-priority issues, stay explicit follow-ups.
- Menu preflight bounds reachability work; third-party native Shell extensions
  can still block after preflight. It is not a universal context-menu timeout.
- Native picker checks observe owned window transitions and compositor timing
  timestamps. They are not a recording of every physical display frame.

## Final combined gate

The serial `combined-pass9` broad run completed **4445/4447**, exit code 1.
All 46 production JSON files matched their before/after hashes. The run used an
isolated state folder, short owned TEMP directory, test-window mode and automatic
renderer selection. Child-process and substring-matched group totals overlap;
4447 is the harness's reported total, not a claim of 4447 unique test scenarios.

The two retained failures are:

1. Recursive long-folder duplicate: the documented Windows Shell destination
   limit above; successful file operations must not be conflated with it.
2. A reveal result's node reference is no longer the graph's current reference
   after a concurrent ancestor refresh finishes. Exact path resolution and the
   final live active/selected node pass. The refresh replaces node identity
   (J046); whether the returned object was already stale at the exact method
   return needs an owning-dispatcher diagnostic. This intermittent failure is
   retained, not waived or presented as fixed.

This checkpoint is not a fully green broad suite. The owner subsequently
requested publication as **v1.2.0**, out of beta. The stable release channel is
therefore an explicit release decision, not a claim that all 170 findings or
every Windows edge case are resolved. Release packaging/CI gates are verified
separately, and the retained limits are included in the
[v1.2.0 release notes](RELEASE_NOTES_v1.2.0.md).
