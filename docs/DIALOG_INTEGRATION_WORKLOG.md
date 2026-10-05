# Native dialog integration — current handoff, 2026-09-30

User requires optional Open/Save/folder replacement, original custom export controls and safe recovery; latest priorities are tile mode, reliable selection and near-instant display. Original attachment is in C:/Users/User/.codex/attachments/dbeecc09-63e4-4d50-890d-dce331741947/Pasted text.txt.

## Shared branch and ownership

- Workspace E:/AiControl/UltraExplorer, branch codex/native-dialog-proxy.
- d5787ac saved the original implementation; be5150b merged the approved black/white icon; 2c6be51 is Claude's tested safety/startup/placement implementation; 23ad7d2 is Codex tile selection/navigation.
- 68da7f4 is bounded native capture + resident worker, 2f12571 records full163 native results, 590b123 is the reviewed installed-copy update with recovery/rollback.
- BUILD.md, PROJECT_HANDOFF.md and untracked Linux port belong to the user's other work. Preserve them.
- Shared coordination is coordination/messages/. Claude explicitly freed Integration for Codex speed merge, then offered prepared-window/first-frame work after this speed commit. See his 20260930-1215-claude.md and Codex next-axis reply.

## Current implementation

- Real native COM caller remains responsible for validation/overwrite/export. Unsupported contracts remain native. Default off; user explicitly enabled it on September 29. Installed copy was updated by Claude on September 30 at 11:29 client time, including current tile changes.
- Independent guardian protects exact HWND/PID/cookie/start identity and restores original transparency. Settings/tray/emergency recovery, per-app exclusions, healing and opt-in sign-in startup are implemented.
- Native proxy defaults to Nested tile canvas. Requested folder is a direct root; navigation preserves cached roots. Selection-generated labels retain exact selected paths; metadata avoids disk probes on click; clamped single-selection is synchronized back to tiles. File/folder and hidden rules survive ordinary workspace layer settings.
- Corrected filename commit after type changes: Save writes filename ComboBox and CBN_EDITCHANGE; Open/multi/folder write the actual visible Edit. Direct Edit readback alone can leave native Save's cached previous name.
- Bounded/cached NativeDialogChrome traversal prunes file/navigation rows. App control visibility follows HWND WS_VISIBLE independently of opacity. Compact contract refresh watches dynamic options/metadata and validates before Submit.
- Resident warm worker uses current-user-only named pipe, ACCEPTED/DONE/RECYCLE protocol, cold fallback, exact child retirement, active-session counting and recycle after provider timeout. Queued mutations skip after fallback. a9f4f6c supersedes the constructor-only prewarm with a shown, DWM-cloaked window and thread-local activation guard; the consumed window is disposed and the next prepared at idle.
- Successful native handoff keeps mode enabled even if a provider disappears during late follow-up.

## Verification

- Shared Release solution and Debug solution: zero warnings/errors.
- Selected headless picker/integration/tile contracts: 182/182, artifacts/final-picker-contracts.log.
- Separate no-visible-window tile STA probe: 43/43, artifacts/picker-tile-probe. It was initially built against the last successful isolated DLL during Claude edits; final selected headless and native runs use current source.
- Final full warm native set: 163/163 after restricting the filename ComboBox notification to Save; Open/multi/folder retain the direct Edit writer. See artifacts/final-native-warm-verified.log. Includes repeat, dynamic visibility, crash, guardian crash, stall, overwrite, fallback and app exceptions. All use secondary monitor + foreground trace and verify no workspace/marks writes.
- Dynamic hidden-to-visible checkbox + format change + native callback/path: 12/12.
- Repeated real Save in one caller/process: 17/17, same worker PID/start ticks and distinct leases, artifacts from managed speed worktree.
- 22 shared warm presentation samples: median 465 ms, range 390–717 ms, recorded at protected ContentRendered. Older pipeline logs were 3–5 seconds. This does not guarantee suppression before the native first frame.
- Claude full smoke on pre-speed combined copy: 2194/2196, remaining baseline/environment GPU timing + LightReveal issue also on master. Do not claim full smoke green.
- Installer mocked lifecycle/rollback: 111/111; Windows PowerShell5.1 UTF8/atomic preference helper: 4/4. No real installed state/process actions by these probes. See artifacts/install-script-probe.
- Rapid tile clicks: 100 alternating selections update session, ComboBox and inner text synchronously (median0.062ms, max0.298ms), late graph callbacks retain the last name. Filter hide/reveal highlight bug was reproduced (57/58) and fixed in Controls/NestedCanvas.Frame.cs by resolving pending selection from cached listings; final no-visible-window probe58/58, six new regressions pass. Wider NestedSelectionChecks+PickerTileChecks120/122; two 10k timing budgets also fail with old68 DLL on the same machine (10.9ms Ctrl+A versus8ms, 3.46ms loading versus3ms), while old DLL additionally fails the newly covered visibility regressions. Do not describe this wider group as wholly green.

## Next work

1. Claude confirmed prepared-window ownership in coordination/messages/20260930-1250-claude.md, beginning over 2f12571. Preserve ACK/RECYCLE, poisoning and native contract gates; early UI must not accept until full contract is validated. Review notes were sent for lease-owned hidden Capture, all-path accept gate, foreground recheck, prepared focus and preservation of early user edits.
2. Run current full native suite after final merge; inspect timings and screenshots. Never allow test windows to take user's focus or primary monitor.
3. Installer590b123 now handles installed background roles. Claude will perform one coherent real install after his prepared-window tests; Codex is not concurrently running install. A foreign active integration copy makes the installer abort before shared-state recovery.
4. Claudе first-frame measurements in scratch: Win32 initial read ~0.5 ms; own shown+cloaked window uncloak ~1–2 ms; navigation ~30 ms. Current out-of-context hook cannot guarantee pre-paint replacement. No kernel driver or global COM override has been installed.

Managed speed checkout archive is confirmed; needed ignored logs were preserved under artifacts/dialog-speed-evidence before archive. A second managed checkout C:/Users/User/.codex/worktrees/dialog-guardian-notify/UltraExplorer prototypes event-driven guardian lease registration in isolation only; shared DialogLease.cs remains Claude-owned until merge coordination. Main source has the complete first speed implementation. No push/PR requested or created.

## Staged presentation through a prepared picker (Claude, 2026-09-30)

Implemented over b0a5177 in the shared tree, Integration owned by Claude (see coordination 20260930-1250-claude.md):

- `Picker/Integration/FastDialogRead.cs`: Win32-only glance at recognition (captions via InternalGetWindowText, no message to the dialog): folder from the address caption (drive path found by letter, never the translated prefix; `\\server` left to UI Automation), mode from the name box id (1148/1001/1152), title, OK and name labels, rect, and whether every extra control is a mirrorable kind (check/radio buttons, lists, plain edits, labels; a push button or anything unknown keeps the dialog off the early path). Then, off the UI thread and within one 160 ms budget, the file name (WM_GETTEXT) and file types (CB_ messages, each given the remaining budget). `KnownFolderNames` builds the "Address: Downloads"/"Адрес: Загрузки" table from desktop.ini LocalizedResourceName and the shell display name for the user's and system's UI languages plus en-US, within a 2 s total deadline, in the background (never blocks READY).
- `PreparedPicker` in `DialogWarmWorker`: one MainWindow shown once and DWM-cloaked (WS_EX_NOACTIVATE, out of the taskbar, on the last dialog's monitor or the primary; a test copy's own secondary monitor), placeholder folder = last dialog's folder or the profile. After each dialog the used window is closed and the next prepared at ApplicationIdle. `MainWindow.Warmup.cs` (ctor-only shell) removed.
- `ActivationGuard`: thread-local WH_CBT hook refusing activation of prepared windows from creation (HCBT_CREATEWND inside the prepare scope) until the keyboard is handed over. Found with an in-context WinEvent trace: WPF's `HwndTarget.OnDpiChanged` moves a just-created window onto a monitor of another scale with a SetWindowPos lacking SWP_NOACTIVATE, which raised EVENT_SYSTEM_FOREGROUND for the cloaked window (the foreground watch failed every case before the guard).
- `MainWindow.RebindAsync(session, sameDialog)` in MainWindow.NativeProxy.cs: repeatable picker binding. Fresh: session chrome through the new shared `ShowPickerSession` (title, footer, type row, New folder, pinned places, PickerPlaces cleared), selection limit reset, history cleared (`NavigationHistory.Clear`), selection cleared, nested roots replaced by the one folder (never appended), rules after the old root is gone, camera `FlyTo(0.92)` as a new picker's Loaded, then the tree's reveal (address, selection, first Back step) while the canvas already reads/draws. sameDialog: keeps view, selection and completion; a name typed after selecting is kept (`NativeDialogRules.SelectionNamesFile`). `WhenFolderDrawnAsync` waits for folder loaded + a canvas frame + the next distinct frame. Accept gate: MultiBinding CanAccept AND contract, and `FinishIfValidAsync` awaits the contract (Enter/double-click wait, Cancel works). Focus: `WithholdsKeyboard` replaces IsTestWindow at the two picker focus sites.
- `NativeDialogProxy` order: glance -> bounded background folder check + details -> UIA capture started -> early bind (place, owner, rebind, draw while cloaked) -> uncloak only when drawn and still in front (DwmFlush, HWND_TOP, uncloak, DwmFlush) -> real use: keyboard handed over now (original still visible; given back if the dialog stays with Windows) -> capture raced with the picker's result (cancel/"use Windows" do not wait for the read; an unfinished read recycles the worker) -> contract compare (`SameContract`; sameDialog rebind for multiselect/labels/types, fresh rebind if folder/mode differ) -> options -> lease armed with the proxy handle in its first write -> original hidden (still in front) -> file types watched -> OK enabled. Unsupported or not in front: ours cloaked then closed; the original never hidden or written. Built path (no prepared window, per-dialog process) unchanged apart from DwmFlush before hiding, the in-front recheck and no Activate fallback. BringForward now checks the foreground source before asking Windows.
- Lease/guardian: guardian waits on an optional named wake event (fallback 100 ms sleep), `.ready` written whole (tmp + move) and read tolerantly, `ArmAsync` polls 4 ms for 200 ms; `Write()` retries a replace denied right after the first write (was UnauthorizedAccessException on AttachProxy once the guardian answered instantly).
- Timing: the agent passes `Stopwatch.GetTimestamp()` at recognition (pipe `hwnd ticks`, cold `--recognised`); every replacement logs `timing path=... recognised->frame=... recognised->ready=... read=... roots=...; glance details placed bound revealed drawn`. The native suite prints them per case and a median/worst summary. New case `save-sequence`: three Save dialogs in folders one/two/three, same worker, own lease and picker window each, bare typed name comes back in the right folder, every one `prepared-early` with roots=1.

Verification (build `--artifacts-path` in Claude's scratch, state dirs there, all windows on the secondary monitor):

- Release solution 0 warnings / 0 errors. Headless `--only DialogIntegrationChecks,Picker`: 211/211 (new: Win32 read, known-folder names from resources, contract comparison, typed name kept on rebind).
- Full native suite, prepared worker (`NATIVE_DIALOG_SMOKE_PREWARM=1`): 186/186, 19 cases incl. save-sequence, no foreground taken, no canvas state written. Without prewarm (per-dialog process for most cases): 186/186.
- Timings from recognition, same machine. Before (2f12571 built in scratch, warm worker, "presented and protected" from proxy start, where frame = ready): median 459 ms, worst 709 ms (11 dialogs). After, prepared-early (19 dialogs): our frame on screen median 114 ms, worst 166 ms; ready to accept median 198 ms, worst 372 ms. Per-dialog process: frame/ready ~1 s (includes process start). Stage lines show ~10 ms dispatch, ~5 ms details (60-80 ms when the dialog was shown a moment earlier and its thread is busy), ~10 ms bind, ~60 ms until the canvas has drawn the folder, ~15 ms for the two compositor waits.

Not covered automatically (would take the user's keyboard): the early keyboard hand-over and its return in a normal copy. Early Cancel/fallback/off are now timed by the Codex final checks below. scripts/install.ps1 was not run by Claude's session.

## Codex final staged-window QA, 2026-09-30

- Reviewed a9f4f6c, then corrected capture deadline: bounded Capture is created before racing against the picker result; cold/no-early awaits the same bounded task. Cancelling an unfinished read retires that worker. A provisional overlay starts a pulse immediately and responds to mode-off/caller disappearance before a lease exists. Initial PID is retained for early cancellation/focus checks.
- FinishIfValidAsync checks closed state and completion identity before/after the contract await, before validation and after reentrant caller validation. An unrelated rebind discards the stale attempt; a same-dialog rebind preserves a valid waiting attempt.
- New no-HWND live-window probe23/23: typed beta after selected alpha remains beta across full rebind, both cancellation orders have no late validation/remembering, unrelated rebind remains pending, same-dialog pending accept uses the fully read session. artifacts/rebind-review-probe/results-fixed.log.
- Release solution0 warnings/errors; selected headless211/211, artifacts/final-prepared-headless.log.
- Four delayed-provider native cases50/50: early Cancel148ms, fallback164ms, mode-off115ms; read-timeout dismissed7685ms after pending detection. Original caller is preserved, native cancellation HRESULT verified, unfinished worker retired. artifacts/final-pending-capture-qa/native.log.
- Final full prepared native23cases **236/236**, artifacts/final-prepared-native.log. Frame median114ms worst200ms (23 presented dialogs), ready median319ms worst653ms among confirmed contracts. Every window on secondary monitor, no test foreground or workspace changes. Earlier independent cold subset open/save/Downloads/folder36/36, frame median1108ms; cold process creation is still slower.
- Guardian alternatives compared in an isolated managed checkout: publication-to-ready baseline median44.23ms, per-token prototype12.48ms, Claude simple wake+4ms12.26ms. Kept Claude's simpler implementation; prototype not merged. Evidence42files under artifacts/guardian-registration-evidence; managed checkout archive confirmed.
- Installed final code9da5a6f with reviewed590b123 installer at14:06 after fresh preflight found no foreign integration copies. Normal old window closed through its save path; -Launch opened the installed window. Verified ProductVersion1.0.0+9da5a6f, Enabled=true, exclusions unchanged, LastRecovery empty, HKCU Run points at installed executable, exact installed agent/guardian/worker + ordinary window running, and production log reports Prepared dialog worker is ready. artifacts/final-install.log. No source changes followed the tested code snapshot; final documentation records deployment separately.

## Timing review of the prepared picker (Claude, 2026-09-30, review only)

No product file changed. Built 2f12571 (before) and a9f4f6c (after) from worktrees into scratch, NATIVE_DIALOG_SMOKE_PREWARM=1, cases open, save, save-downloads, folder, save-repeat, 15 runs each per build in interleaved passes (90 dialogs per build, every pass 265/265), current HEAD d55813d 5 runs more (265/265). An external observer (WinEvent hooks on a thread of its own, no window) timed the dialog's EVENT_OBJECT_SHOW and our window's show/uncloak, and copied only our own window with PrintWindow; a scratch copy with extra stage marks gave the breakdown. Secondary monitor 1920x1080 at 239 Hz. Headless DialogIntegrationChecks,Picker on a9f4f6c 211/211.

- After (a9f4f6c, all prepared-early): recognised -> our frame on screen median 116 ms (worst 261), -> ready to accept 218 (worst 745). Per case frame/ready: open 116/192, save 111/211, save-downloads 103/245, folder 107/181, save-repeat first 117/215, second (dialog shown while the worker was warm) 164/243. d55813d: 106/200, second repeat dialog 177/261.
- Before (2f12571, built after the full read, frame = ready): proxy start -> presented and protected median 538 (worst 845); open 473, save 569, save-downloads 581, folder 446, repeat 542 and 488. Recognition precedes proxy start by ~2 ms (warm) to ~10 ms (first request).
- From the dialog appearing (observer, second repeat dialog): our frame on screen 181 ms after (worst 238) against 461 (695) before, ready 260 (348) against 513 (762). Dialog shown -> recognised is 19 ms median (5-28) and is not part of the logged numbers. The observed uncloak comes 3-5 ms before the logged frame (the post-uncloak DwmFlush), so the self-reported frame is accurate.
- First frame: 18/18 captures of our window 0.1-1.1 ms after the uncloak show the dialog's folder with its tiles, address and name, identical to the picture 600 ms later except OK turning enabled (<=0.26% of pixels). Before: 6/6 blank at show, then "This PC - Reading drives...", the folder's tiles 0.1-0.25 s later.
- Breakdown, idle dialog on a fresh worker (n=23): dispatch 5, glance 3, details 5, place+session 16, tree reveal 5, dispatcher gap 13, first WPF render after the rebind 42 (the canvas's own draw 0-2 ms), two more ticks 8, compositor waits ~7 -> frame 111; UIA 149, lease 27 (first lease of a new guardian; 5 when warm) -> ready 197. Fresh dialog on a warm worker (n=9): details 102 (70-155; each message to the dialog's thread waits for a pump of its own: first reply median 29, labels and CB_GETCURSEL 35-64 more), place+session 6, first render 41, ticks 15 -> frame 172; UIA 133, lease 5 -> ready 245.
- UIA capture: 110 ms alone in a quiet worker, 140 ms overlapping the early bind, 146-160 ms in the suite because the first request comes right after READY while the worker is still settling (2f12571: 112). After a dialog the next prepared picker is ready 319-571 ms (median 336) after the dialog closed (DONE 111-174, cloaked Show +115-155, placeholder draw +80-340). With no prepared window at all (forced in the scratch copy) the built path takes 733-843 ms against 446-581 at 2f12571: the ctor-only warm-up is gone.
- Remaining avoidable delay, largest first: the serial Win32 detail reads on a just-shown dialog (FastDialogRead.ReadDetails, ~70 ms of a fresh dialog's frame); binding and the first render (~60 ms) waiting for those details although folder, mode and labels are known at the glance; the ~40 ms first WPF render after RebindAsync; the suite timing an idle, pre-opened dialog on a fresh worker rather than a just-shown one; the 19 ms recognition lag (hook callback re-posted at Background priority); the 0.3-0.6 s until the next prepared picker is usable; first-use costs (guardian's first lease +22 ms, first request ~15 ms JIT).

Scratch: prep/review2 (runs/*, framewatch, analyze.py, table.py, stages.py, instrumentation.patch).

## Timing review findings fixed (Claude, 2026-09-30)

Working tree over e6e3593; Integration owned by Claude. Built with `--artifacts-path` in Claude's scratch (prep/fix/build), every process with its own ULTRAEXPLORER_STATE_DIR there, every window on the secondary monitor, nothing activated, the user's installed copy and state untouched, install.ps1 not run.

Safety:
- Quick and full read agree on folders shown by name (review: "Address: User" shown early, then withdrawn). `NativeDialogAutomation.ReadCurrentFolder` now resolves the toolbar caption through the same `KnownFolderNames` table as `FastDialogRead` (every known folder, the profile by the user's name, user/system languages + English), after the drive/UNC path and before the English/Russian breadcrumb fallback. READY waits for the table (its own 2 s deadline), so a request can never find it in the quick read and not in the full one. New native case `open-profile`: served, protected, cancelled, no "stays with Windows".
- 8 s bound on the early path and the pending accept after cancel: already fixed in 9da5a6f (bounded capture raced; `IsCurrentAttempt` before and after the contract await); covered by `read-timeout` and Codex's probe. No change.
- Resident cost of the prepared worker, causes measured with a dispatcher-operation profiler in a scratch copy: (1) Nodify's auto-pan `DispatcherTimer` (1 ms interval, so every clock tick, 64/s) runs from `OnApplyTemplate` in every window, dragged or not: 610 of the worker's ~800 ms CPU a minute. `MainWindow.ConfigureAutoPanning` keeps `DisableAutoPanning` true except while the mouse is captured inside the editor, exactly when Nodify pans (all windows). (2) `ShellMenuWarmUp` is not started for native-proxy pickers (a picker builds its menus cold). (3) The placeholder watched the profile and every folder on the way to it (AppData\Local, Temp): each change there reread and redrew the cloaked window. The placeholder is now the application's own folder, and a prepared picker detaches from the change hub entirely (`MainWindow.DetachChanges`: no volume armed, no folder registered); a rebind watches the canvas's folders before the dialog's root is made (roots take their volume's watch at creation) and the tree behind it after the frame (`WatchChanges`, posted at Background after the uncloak; its registrations cost 6-7 ms). Keeping only the canvas watched still cost 0.73 s a minute under churn; fully detached 0-16 ms. (4) The test copy's `--capture` poll never starts for a waiting picker. Result (tools/Idle.ps1, listener limited to a dummy process, 60 s after READY + 30 s): CPU 750 -> 31 ms/min idle, 1,609 -> 47 ms/min under about 130,000 file creates/deletes; private 230 -> 181 MB, handles 1,970 -> 1,198, USER 81 -> 32, GDI 166 -> 62, dedicated GPU about 110 -> 58 MB. New native case `idle-worker` (no dialog; window only cloaked; under 100 ms CPU in 15 s) and headless `PreparedPickerWindowChecksAsync` in SettingsChecks (nothing watched while waiting, canvas watched after the bind, tree after WatchChanges, auto-pan off).
- Per-dialog leak (USER +3, thread +1, handles +10 per dialog): a census of the worker's threads by name showed one "UltraExplorer Shell icons" STA thread per closed window. `MainViewModel.Dispose` never disposed its `ShellIconService`, whose worker waits for work forever (with COM's hidden window). Disposed now; 30 dialogs through one worker: USER 37 -> 38, threads 133 -> 133, handles 1,273 -> 1,318 (before: USER 92 -> 266 and threads 151 -> 197 over 60). Backstop: the worker answers RECYCLE after 400 dialogs or at 3,000 USER objects or 10,000 handles (`WornOut`); new native case `save-recycle` (test-only limit 1: the next dialog is served by a new worker).

Timing:
- `FastDialogRead.ReadDetails` asks everything at once: the name, CB_GETCOUNT (on the calling thread), CB_GETCURSEL and the first six labels (length, then text) each on a short-lived 64 KB thread, the rest in at most 12 runs; one 160 ms budget, read-only messages. DetailBench on a just-shown Save dialog (10 runs each): serial median 76 ms with 3 budget failures, concurrent 40 ms with 1; an idle dialog 1-2 ms -> 2-4 ms (thread starts).
- While the details are read, the prepared picker is taken, placed over the dialog, owned, and its footer names the application (`FileVersionInfo`, about 3 ms). Binding itself still waits for the details: a bind before the name and types would need a second rebind and a re-filter of the drawn folder, and with the concurrent reads the wait is 5-45 ms.
- The review's "~40 ms first WPF render after RebindAsync" is a test artifact: `RebindAsync` started the `--capture` window capture (RenderTargetBitmap + PNG, 37-52 ms at ContextIdle) between the first two frames the suite times; real use never had it. `MainWindow.CaptureForTests` now runs after "ready to accept". The first render op itself is 14-26 ms (layout of the rebound chrome).
- A raise stall showed once the details were fast: `RaiseWithoutActivation` without SWP_NOOWNERZORDER raised the owner too and waited 110-200 ms for the application's thread, busy with the dialog it had just shown. Only our window is raised now; the pending cases check the provisional picker is above the original (`IsAbove`).
- First-use costs moved ahead of the first dialog: the placeholder has a file type (the type row is realized), `WarmFirstDialog` (footer name, a throwaway ChangeHub register/unregister: the first `WatchCanvasChanges` went from 6 ms to 0), `DialogLease.WarmUp` in the guardian and the worker (lease JSON).
- Recognition: WinEvent hooks on a dedicated thread (`DialogEventThread`); the callback keeps only top-level #32770 windows (of the test copy's process) and posts `Probe(window, eventTimestamp)` at Send priority; recognition is stamped when the event arrives. Dialog shown -> recognised: 18 ms median (31 worst) -> 0 (the listener's stamp and the runner's own hook coincide).
- The prepared picker is ready as soon as it is shown cloaked (the placeholder's drawing goes on; a dialog replaces it); the follow-up poll went from 60 to 20 ms. A bound but not yet shown early picker is confirmed (rebound to the full contract when needed) before it is uncloaked, so nothing on screen changes after its first frame.
- Not changed: the built fallback (in every real path the worker has made and shown a picker before a built one is needed; the review's 733-843 ms came from a copy with no prepared window at all); the two compositor waits around the uncloak (kept for the first-frame guarantee; 0-5 ms each at 239 Hz); the AutomationThread priority (the contention was not isolated).

Suite: new cases `open-fresh` and `save-fresh` (the fixture waits on a named event until the worker is ready + 2 s), `open-profile`, `save-recycle`, `idle-worker`; the timing line carries `recognised-qpc`; the runner times each fixture dialog's EVENT_OBJECT_SHOW with its own hook and reports dialogs already open and dialogs just shown (appeared after the listener started) separately, with shown -> recognised, -> frame and -> ready; `NATIVE_DIALOG_SMOKE_NO_CAPTURE=1` for timing runs; result JSON read with a retry (a save-sequence read raced the fixture's write once).

Verification:
- Release and Debug solution 0 warnings / 0 errors. Headless `--only DialogIntegrationChecks,Picker` 211/211; `--only SettingsChecks` 198/198 (7 new prepared-picker checks).
- Full native suite with prewarm, 28 cases: 287/287 (twice); no test window took the foreground, no canvas state written. Its summary (test capture on, now after ready): dialogs already open (22) recognised -> frame median 64 (worst 96) ms, -> ready 145 (201); dialogs just shown (5) shown -> frame 63 (138), -> ready 264 (311); idle worker 0 ms CPU in 15 s. Cold subset without prewarm (open, save, save-downloads, folder, cancel, unsupported, open-profile, overwrite, save-repeat): 84/84, built frame = ready median 962 ms (process start included).
- Timings, 6 interleaved passes per build over open, save, save-downloads, folder, open-fresh, save-fresh, save-repeat and save-sequence, prewarmed, test capture off (before = e6e3593 with the recognition timestamp added to its timing line), machine under 20-60% load from other work. Dialogs already open, recognised -> frame median 86 (worst 172) -> 68 (123) ms, -> ready 235 (382) -> 160 (643, a UI Automation read outlier) ms. Dialogs just shown (n=29/30), from the dialog's appearance: our frame 178 (522) -> 100 (140) ms, ready to accept 307 (549) -> 294 (692) ms. Ready for a just-shown dialog is bounded by its own UI Automation answers (the read takes 250-340 ms while it sets itself up).

Scratch: prep/fix (build, before-qpc, runs/*, tools/Idle.ps1, tools/suite.sh, tools/ab.sh, ab.py, detailbench, leakrunner, instr2-src with patch_instr2.py and patch_instr3.py for the stage marks, opprof-*.log).

## Dialogs shown the way real programs show them (Claude, 2026-09-30, tests only)

No product file changed. Built with `--artifacts-path` in Claude's scratch (coverage/build), every process with its own ULTRAEXPLORER_STATE_DIR there, the user's installed copy and state untouched, install.ps1 not run.

- New fixture producers, `tests/NativeDialogProxySmoke/DialogProducers.cs` (compiled only into the native runner, which now has UseWindowsForms): WinForms OpenFileDialog (no Filter; three types with FilterIndex 2; Multiselect; InitialDirectory), SaveFileDialog (DefaultExt + AddExtension without types; OverwritePrompt with types), the Vista FolderBrowserDialog (UseDescriptionForTitle); WPF Microsoft.Win32 OpenFileDialog, SaveFileDialog (FilterIndex 2, DefaultExt) and OpenFolderDialog; GetOpenFileNameW / GetSaveFileNameW with OFN_EXPLORER and no hook (ten types with long Russian labels, OFN_ALLOWMULTISELECT); SHBrowseForFolder (BIF_NEWDIALOGSTYLE | BIF_EDITBOX). IFileDialog edge modes in `NativeDialogFixture.cs`: 24 types with type 17 chosen, a type labelled "Photoshop image" for *.psd, a Save proposing "Quarterly report" accepted untouched, a folder path with spaces and Cyrillic letters, a folder picker in C:\, FOS_OVERWRITEPROMPT on a proposed existing file.
- Runner: a case table (`Producers`): what is typed (a bare name where the picker's folder matters), the exact paths the program must receive, the file type it must get back (the picker hands its own choice back, so a lost FilterIndex shows), the overwrite warning answered on the secondary monitor, and a Save's proposed name compared with the name box of the program's own dialog. `shbrowse` must stay with Windows: not taken for a file dialog, no log line, nothing shown, no lease, and its own Cancel reaches the program. The overwrite answer is shared with `overwrite` (`AnswerOverwriteWarning`).
- Findings. All 19 file dialogs are served (prepared-early with the worker, built without it); every program received exactly the chosen paths and its own file type. Windows adds the pattern to a type label given without one ("Photoshop image" is shown as "Photoshop image (*.psd)", also "(layered)" and "[PSD]" labels), so no label an IFileDialog program can give reached the "pattern cannot be read" rule on this machine (Explorer shows extensions here, HideFileExt=0; not tried with them hidden, which would change the user's setting). With extensions shown a Save dialog shows its name with the type's extension ("export" as "export.log", "Quarterly report" as "Quarterly report.csv"), and the picker shows the same. A drive root's address reads "Address: C:\" and is served by the path rule. SHBrowseForFolder is never recognised (no SHELLDLL_DefView or NamespaceTreeControl; its edit is id 14148) and says nothing in the log.
- A test window on the primary monitor, once: in the first run the three WPF dialogs opened at x -853..347, partly on the primary (never activated). The fixture's owner was made while the process was DPI-unaware; WPF's first window made the process system-aware and the owner was measured again, 1.5 times smaller, across the monitors' edge. Fixed in the fixture: the WinForms and WPF producers make the process system-DPI-aware before anything is measured (as those programs are), and their guard keeps every top-level #32770 inside the target work area (CREATESTRUCT at creation, WM_WINDOWPOSCHANGING after, each correction logged; proven on a hidden window; the runs never needed it). It is opt-in: subclassing the COM fixture's dialog as well raised the early cases' z-order failures from HEAD's 6 of 26 to 12 of 26.
- Verification: both test projects 0 warnings / 0 errors; headless `--only DialogIntegrationChecks,Picker` 211/211. The 20 new cases: 203/203 without the prepared worker (twice) and 203/203 with it. Whole native suite, 50 cases: without the prepared worker 508/508; with it 505/508, the three failures the existing check "the provisional proxy is above the original dialog it covers" in early-cancel, early-fallback and early-off. That race is not new: HEAD's own build (b263cb3, built in a scratch worktree) failed the same check in 6 of 26 early runs on this machine today; the delayed provider (1.5 s) leaves the just-shown original time to rise over the provisional picker. No test window took the foreground in any run.
- Timings with the prepared worker, dialogs already open (43): recognised -> our frame median 68 ms (worst 115), -> ready to accept median 150 ms (worst 365).

Scratch: coverage (build, runs/*, states/*, *.log, probe-*.ps1, suite-one.ps1, suite-base.ps1).

## The provisional picker under its dialog (Claude, 2026-09-30)

The one failure left by the coverage work: "the provisional proxy is above the original dialog it covers" in early-cancel, early-fallback, early-off and read-timeout, about 1 run in 4 (HEAD 6 of 26, then 2 of 8 again here). Built with `--artifacts-path` in Claude's scratch (coverage/build), every process with its own ULTRAEXPLORER_STATE_DIR there, the user's installed copy and state untouched, install.ps1 not run.

- Cause, from a tracer outside the test (coverage/ztrace: out-of-context WinEvents plus the z-order of the fixture's and the test copy's visible windows every millisecond, nothing moved or activated): in every failing run the prepared picker had been shown, cloaked, under the fixture's dialog, and `SetWindowPos(HWND_TOP, SWP_NOACTIVATE | SWP_NOOWNERZORDER)` at the uncloak changed nothing, so it was uncloaked under the dialog; in the passing runs it had happened to be shown over it. The original never moved. A probe (coverage/zprobe: two cloaked, never-activated windows of threads of their own on the secondary monitor) saw HWND_TOP from a thread that is not the foreground's leave a window where it was in one run and raise it in another, while HWND_TOPMOST + HWND_NOTOPMOST and an explicit insert-after always moved it, and a window placed after the last topmost window stayed an ordinary one. In real use the keyboard hand-over right after the uncloak activates the picker and so puts it on top, but the frame composed between the two could still show the dialog over it.
- Fix: `DialogNative.RaiseAbove(window, below)` places the picker after the window just above the dialog (`GW_HWNDPREV`), still without activation and without the owner; nothing moves when it is over the dialog already; false when either window is gone (the uncloak is then refused and logged, and the dialog stays with Windows). `RaiseWithoutActivation` is gone. While the dialog is not yet hidden, the proxy's pulse puts the uncloaked picker back over a dialog that has come up over it, and in real use hands it the keyboard again (only from the dialog's own thread, as at the uncloak).
- Checks: headless `DialogIntegrationChecks` "over the dialog it covers" (8: two cloaked stand-ins; under -> directly over, over already -> nothing moves, a dialog just under the topmost windows -> over it and still not topmost, a gone picker -> false). Native: in `early-off` and `read-timeout` the runner puts the provisional picker just under its original (`SetWindowPos` with SWP_NOACTIVATE) and the picker must be back over it within a second (54-251 ms); against the unchanged app it stayed under (1055 ms, fail).
- For Codex's request (20260930-1730-codex-producer-coverage-and-native-progress.md): the ordinary runner now holds `Local\UltraExplorer.NativeBench.Secondary` for the whole run, its windows and cleanup; the fixture it starts does not take it. It waits at most NATIVE_DIALOG_SMOKE_BENCH_WAIT_MINUTES (30 by default) and then refuses to run.

Verification (final code):
- Release and Debug solution 0 warnings / 0 errors. Headless `--only DialogIntegrationChecks,Picker,SettingsChecks` 417/417 (409 + 8 new).
- Full native suite, 50 cases: without the prepared worker 510/510, with it (`NATIVE_DIALOG_SMOKE_PREWARM=1`) 510/510 (508 + the 2 new re-raise checks), both twice (the first pair on the build before the last log line was added). No test window took the foreground; every window on the secondary monitor.
- The pending cases alone, prepared worker: 57 more runs of early-cancel/early-fallback/early-off/read-timeout (26 + 5 + 26); with the 16 in the full suites, "the provisional proxy is above the original dialog it covers" 73/73 (before: about 1 in 4 failed). The tracer caught one run whose picker had been shown cloaked under the dialog: it was uncloaked over it.
- The placement costs nothing measurable: from "drawn" to the frame on screen (the two compositor waits with the placement between them) median 8 ms before and after, 49 dialogs each. Prepared worker, dialogs already open (43): recognised -> frame median 73 and 87 ms (worst 175, 155), -> ready 172 and 212 ms, against 68 and 150 in the coverage run; the difference is in the stages before the placement (machine load).

Scratch: coverage (ztrace, zprobe, z1-trace.log before, z2-trace.log after, fix1-*.log, final3-*.log, early7-warm.log, headless3.log, z4-oldapp.log).

## Review of b263cb3..e331cb1 (Claude, 2026-09-30)

An adversarial pass over the two commits (the producers, the placement over the dialog): a wrong path or type handed back, a result reaching another dialog, an original left transparent, a dialog accepted that cannot be served completely, unbounded waits, the tests' focus and monitor rules. Built with `--artifacts-path` in Claude's scratch (coverage/build), every process with its own ULTRAEXPLORER_STATE_DIR there, the user's installed copy and state untouched, install.ps1 not run.

- The placement in real use. The tests' dialog is never the foreground window; the user's is. A probe (coverage/review/zfg: one cloaked, never-activated window on the secondary monitor, nothing else touched) placed itself after the window just above the user's foreground window and landed directly over it, three times of three. So `RaiseAbove` holds for a dialog in the foreground as well.
- A program kept on top. With an owner made topmost, a window it owns through CreateWindowEx is topmost, and `RaiseAbove` puts the picker directly over it, topmost itself (it is placed inside the topmost band), never under it (coverage/review/ztop). The common item dialog does not take its owner's topmost state (tried in the fixture, owner hidden and shown), so nothing changes for the dialogs replaced and no case was kept.
- The older Explorer-style dialog (GetOpenFileNameW with a hook procedure, as programs with a preview or a template of their own have it) passes `LooksLikeFileDialog` but its folder is not a readable path, so it stays with Windows ("This is a virtual location or its path cannot be read"). That is the right outcome - its template's controls are not in `AppControlsModuleInner` and would not be mirrored - but it rested on nothing tested. New case `win32-hook`: looked at and left with Windows, the log saying why, nothing shown or hidden, no lease, its own Cancel reaching the program.
- `DialogNative.IsAbove` walked the z-order with GetWindow until the end, on the picker's thread, five times a second while a provisional picker is up. GetWindow in a loop can come round again while windows are reordered (the documented risk of calling it so); the walk is now bounded (65536 steps). Headless check: the bound is far above this desktop's 539 top-level windows.
- Nothing else found. A failed placement returns before the uncloak (the original never hidden, the dialog left with Windows); the pulse's re-raise hides and writes nothing; the new waits are bounded (the bench lock 30 min by default, the re-raise check 1 s); the tests' new windows stay on the secondary monitor and are never activated.
- One cold run's `save-nofilter` saw its built picker become the foreground window (18:55:12, 184 ms after it was presented, before the runner touched it), from Telegram, while the user was working; the same case passed in every other run, and nothing in the built path activates a test copy's window. The foreground watch now says, for a test window taking the foreground, how long before the user last touched the mouse or keyboard and whether the pointer was over that window, so a click on a test window can be told from a regression.

Verification:
- Release build 0 warnings / 0 errors. Headless `--only DialogIntegrationChecks,Picker,SettingsChecks` 418/418 (417 + the bound).
- Full native suite, 51 cases: prewarmed 520/520; without the prepared worker 519/520 (the foreground change above) and then 520/520. A second prewarmed run 513/515: `early-fallback` could not act because its early read missed the 220 ms budget under load (details=292 ms; the dialog was shown after the full read, as designed), and `open` took the same path. The prewarmed suite again on the final build: 520/520 (prepared-early, dialogs already open, 43: recognised -> frame median 72 ms, worst 117; -> ready median 153 ms).

Scratch: coverage (review/zfg, review/ztop, review1-*.log, review2-*.log, review3-warm.log, review-headless.log, r1hook/r2hook/r3top/r4top-*.log).

## Original hidden before the prepared picker (Codex, 2026-10-01)

The provisional picker could appear quickly, then Windows raised its still
visible original over it while full UI Automation capture was pending. The
prepared path now acquires the existing independent watchdog lease as soon as
its HWND is reserved, hides the source before placement and presentation, and
reuses that exact lease when the full contract enables OK. Failed fast metadata
restores/disposes the provisional lease before the ordinary full-read path.
Hidden capture requires its immutable protected lease; delayed provider actions
are rejected after cancellation/fallback. A provisional Cancel keeps the source
transparent until native cancellation closes it, with bounded cleanup if ignored.

Chrome's picker is hosted by UtilWin with a browser-process owner. The prepared
proxy avoids adopting that busy foreign owner queue; Windows' original retains
modality and the proxy checks the original's lifetime/foreground. Its actual
owned Chrome test required explicit test-only direct-child admission, bound to
parent FILETIME, kernel parent lineage, user/session and isolated test state.
Ordinary process admission is unchanged.

Verification so far:
- Release builds: zero warnings/errors. Headless picker/settings/integration
  with isolated state: 418/418. Direct-child scope: 7/7.
- Ordinary Open/Save/custom/unsupported/overwrite: 47/47; prepared custom,
  folder, WinForms Save, WPF Open and Win32 Save: 53/53.
- Final early Cancel/fallback/off: 88/88. Source alpha0 precedes the UE-visible
  sample, remains so after native z-order changes, and does not flash on Cancel.
- Worker/guardian loss and read-timeout restore source transparency/options;
  affected worker/timeout retest: 66/66. Worker loss can be healed by the agent
  while mode stays on; the restored dialog was not reclaimed for one second.
- Actual Chrome, owned inactive desktop: Save/Cancel/unsupported return exact
  bytes/native outcomes and preserve physical foreground. Final isolated Save
  frame: 148 ms from recognition; provisional Cancel frame: 122 ms. These are
  individual observations, not click-to-frame promises. An initial Windows
  frame before out-of-context event delivery is still possible.

Native child-launch/import modification was not installed and was stopped after
an automatic cybersecurity-risk rejection. The installed fix uses only the
existing external window adapter and independent watchdog.

Final release validation: full prepared suite53 scenarios,651/652 initial
assertions. The sole worker-loss failure required marker0 even after the
watchdog had paused the mode; its exact recovery cookie is allowed until Heal
or source close. The corrected test rejects foreign cookies, new lease tokens,
opaque/disabled/dead source and any result. Affected release rerun33/33 passes,
including the exact paused/old-cookie path, with no automatic reclaim for1s.
The original failed log is retained. All53 foreground/keyboard checks passed;
prepared first frame median89ms/worst105ms for already-open dialogs, fresh
shown dialogs median84ms/worst156ms. Latest release headless418/418, build0/0.

## Independent Win+E and picker navigation (Codex, 2026-10-01)

Win+E now uses a separate persisted setting and resident own-process keyboard
listener. General replacement no longer owns the Home launcher CLSID: the
typed journal restores earlier owned Home values while leaving folder routes
enabled. Both sign-in roles and both user preferences survive installation.
Test executables cannot start resident integration roles after fixture state
is restored. No remote process patching or injected DLL is used.

Picker startup frames the caller's folder after its final placement. This PC
retains all drive roots without loading unfocused drives. Pending file choices
are distinct from visual selection and retain full-path provenance through
deselection and navigation; a filename/location hint and Clear show that state.

Release verification: 475/475 combined picker/settings/integration assertions,
1596/1596 keyboard policy and owned message-pump assertions, 69/69 disposable
registry transaction assertions, and 44/44 native Open/custom Save/early Cancel
assertions. Builds have zero warnings/errors. Real isolated WPF frames inspected
for initial folder, resize, all roots, retained selection and bounded multiselect
footer. No live Telegram conversation was controlled, and synthetic keyboard
tests do not claim a physical Win+E press.

The first upgrade attempt stopped before application replacement because an
ordinary window was still open. Its rollback exposed an unsupported-role
restart against the older executable; the installer now starts the shortcut
role after a failed upgrade only when the prior version had that sign-in entry.

Six earlier Home launch helpers were also found blocked before constructing
any normal window. TryForward synchronously awaited an IPC method that captured
the blocked WPF dispatcher. Its synchronous bridge now runs the client on a
worker, leaving the startup dispatcher free of captured continuations. Only the
verified old installed pre-window helpers (no visible windows) were stopped
after the user closed the normal windows and authorised closure for updating.

The second installation succeeded. Both preferences remained enabled, both
sign-in entries name the installed executable, and the independent keyboard
listener reported Ready with its exact live PID/FILETIME. The installed `--home`
opened its normal window; a second installed `--home` helper forwarded to that
window and exited successfully in 270 ms. This measures process forwarding,
not a physical-key-to-drawn-frame latency. Installed executable SHA256:
CFD84ABD354CCDACBE998FE54488C7B46756F95F891C99A38E2914A577037FA3.

Final folder IPC regression: 68/68 on an isolated inactive desktop, including
two assertions exercising synchronous forwarding from a real STA
DispatcherSynchronizationContext. The production identity checks are unchanged.
