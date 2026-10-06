# Windows review and qualification — v1.2.1

Review date: 2026-10-06. This is a follow-up to the
[v1.2.0 integration checkpoint](REVIEW2_INTEGRATION.md), not a claim that every
previous review item or Windows edge case is closed.

## Scope and corrections

The review covered file operations and external drops; navigation, selection,
search, filtering and marks; nested layout and GPU presentation; native folder
and dialog integration; startup, window lifetimes, settings and packaging.
Independent reviews and generated fixtures were used alongside the broad
Windows harness. The incomplete Linux port, separate hover-preview branch and
private local work were not imported into this release.

| Area | Correction |
| --- | --- |
| Selected-item focus | `F` smoothly frames the exact file or folder. File flights follow sort, live-listing and viewport changes, including a read applied on the finishing frame. Selection, pane changes and manual navigation cancel stale focus. |
| Visibility | Focusing an already visible file does not permanently exempt it from a later filter, including when the Files layer initially was off. Explicit hidden/filtered reveals retain their existing behavior. |
| Script drops | Batch arguments use literal data transport rather than EXE quoting through cmd. Shortcuts and script associations are classified before launch; inspected script verbs are also the verbs actually used. |
| Transfer outcomes | Missing or inaccessible requested sources fail before destination creation or copying. A validated move into the same directory remains a no-op. |
| Native folder launch | A closing, busy or picker window cannot silently consume the request; an eligible normal window or the existing launcher receives it. |
| Refreshed graph objects | Hide and multi-reveal operations resolve current nodes after asynchronous materialization, preserving current selection metadata and live focus. |
| Local update | The source installer replaces manifest-owned files individually. Unknown files, nested additions and Setup's uninstaller are preserved; partial failure rolls back only the transaction's files. |
| Test isolation and versions | Test-scope admission uses the app's resolved state directory. Local builds identify as 1.2.1; release builds retain the explicit tag version. |

Ordinary EXE/IPC argument quoting is unchanged. Direct `.cmd`/`.bat` files and
batch shortcuts without preset commands support filenames containing `&`, `%`,
`!`, spaces and trailing directory separators. Command-text hosts, preset
script-host commands and unverified association templates fail clearly rather
than receive guessed escaping. PowerShell associations are accepted only for
the checked literal `-File` template. Third-party scripts remain responsible
for how they subsequently use their arguments.

Metadata reads have two bounded background STA slots and a two-second caller
deadline per phase; a shortcut followed by an association can take about four
seconds. A timed-out metadata reader cannot launch a program later. Native
Shell extensions and program launch themselves are not universally time-bounded.

The cmd transport follows the documented nonrecursive variable substitution
and disables AutoRun and delayed expansion. Script launch pins the same `open`
verb inspected by the association check. [cmd documentation](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd),
[ShellExecuteW operation selection](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutew).

## Verification

The common self-contained Release build for the application and three Windows
test projects completed with **zero warnings and zero errors**.

- New focus/security/lifecycle/refresh-consumer groups: **109/109**, exit 0.
  The lifecycle child independently reports **16/16**; this nested total is
  not added to the parent total as unique scenarios.
- Color Tags: **35/35**, exit 0.
- Pure updater/rollback fixtures: **47/47**, exit 0, on both PowerShell 7 and
  Windows PowerShell 5.1. These tests never invoke the real installer.
- Targeted-stage comparison: **46 production JSON files unchanged**.
- Current NuGet direct/transitive audit reports no vulnerable or deprecated
  packages. A tracked credential-pattern scan found no matches; neither is an
  exhaustive or perpetual security guarantee.
- Workflow lint and the three PowerShell scripts' syntax checks passed.

The final product-code broad run reported **4530/4533**, exit 1. Two failures
retain the graph-refresh identity and recursive Windows Shell path limitations
below. The third was an older off-UI probe fixture that deliberately reported
all sources missing but expected copy/move to succeed. It was updated to assert
the new fail-fast error, while retaining all nine off-UI probes, exact requested
paths, Delete no-op and no destination creation. Its isolated affected group
then passed **45/45**, exit 0. Application code did not change for that repair.
The full-run failure and targeted result are disclosed separately, not combined
into a fabricated fully green broad total.

The initial complete native-dialog fixture reported **646/655**, exit 1.
Nine assertions in three early-handoff cases assumed the original became
alpha-zero before the replacement appeared. The existing handoff deliberately
raises and shows the replacement, waits for composition, then hides the original
to avoid a both-hidden gap. A serial old/current comparison returned **88/88**
and **85/88**; the difference was one pre-hide sample, not a new UI-code change.
The sampler now re-reads original visibility after observing the proxy and
separates protected pre-hide overlap from forbidden post-hide exposure. It
requires exact lease/PID/cookie and z-order, true alpha-zero or DWM hide before
provider completion, and independent hidden-state checks after reordering.
The revised three-case gate passed **91/91**, with zero identity/order violations
or post-hide exposure. These are window-state observations, not atomic physical
pixel measurements or proof of full rectangle coverage. The original failed
run remains recorded; no product timing was changed to make it pass.

The final complete native fixture then passed **661/661**, exit 0, including
the new handoff invariants, actual Open/Save/folder dialog producers, fallback,
recovery and worker-loss cases. No test window took the user's foreground or
keyboard. The three-case 91/91 result is included coverage, not an extra 91
unique scenarios on top of this complete result.

The initial full run on the accepted smooth-F baseline reported **4478/4483**,
exit 1. Three misses were performance budgets: cold icon cache 330/30 ms,
warm glyph rasterization 115.1/60 ms and read queue 351/350 ms. A serial repeat
of these groups on both the previous and current builds passed **212/212**
each: cache 6 ms, rasterization 16.2/14.3 ms and queue 258 ms. Their sources
were unchanged by the F feature. The original broad misses are retained, not
erased by the focused repeats or relaxed thresholds.

## Retained limits

- Windows Shell can reject a recursive folder duplicate when its destination
  tree exceeds its own path limit (`0x8027001E`). Tested long-path file
  operations must not be conflated with arbitrary recursive folder support.
- A refresh can replace graph node identity. The new consumer fixes avoid
  using stale nodes across their awaits, but do not promise that a returned
  object remains the current graph object after a later refresh.
- Deferred saved graph-expansion replay and identity-preserving large refresh
  remain follow-ups. GPU presentation still waits on the shared UI dispatcher;
  a nonblocking shared-texture pipeline is not part of this patch.
- Local filtering covers loaded branches; large listings can be capped. Cold
  folders and offline shares depend on filesystem latency. Virtual archive
  `FileContents` extraction and universal Shell-extension timeouts are not claimed.

The stable release channel is not a claim of a fully green broad suite,
universal compatibility or completion of all 170 earlier findings.
