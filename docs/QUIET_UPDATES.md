# Quiet GitHub updates

Updates use a small indicator in the top-left title bar. Nothing opens to ask
for an update automatically. Checking, downloading and restarting are separate
steps; only the user's **Install update** click starts installation.

- After the normal explorer window finishes starting, checking waits 30 seconds.
- A persisted UTC timestamp permits one check every 24 hours, including failed
  attempts. Closing and reopening the app does not reset it. Multiple copies
  share one download lease. A timestamp more than 5 minutes ahead of the clock
  (the clock was set back, a profile copied from another machine) counts as due
  now instead of blocking checks until that date.
- If a newer compatible release exists, the running app downloads its portable
  package in the background at up to 512 KiB/s. A small muted ring shows progress.
- Verified bytes become a blue download arrow. Clicking it opens a compact panel
  with the version, **Install update** and **Later**. Leaving it alone keeps the
  current version; the panel never opens by itself.
- **Settings → About → Receive updates** controls the whole mechanism. Turning
  it off cancels checking/downloads, closes the offer and hides the indicator.
  Turning it off does not undo an installation the user already confirmed.
- Closing the app cancels network work. An unfinished package can resume at the
  next daily check when the server supports byte ranges. A verified package is
  reused without another download. Only the latest package is retained. Once
  that version is installed (or the offer is older), the next check deletes the
  offer and its package instead of keeping ~150 MB until the next release.

Stable versions receive newer stable versions. An installed beta can receive
newer betas or stable releases. SemVer precedence prevents downgrades.

## What runs

The scheduler belongs to the open explorer process. Picker windows, COM servers,
dialog agents, shortcut agents, tests and diagnostics do not start it. There is
no updater service, task, sign-in entry, telemetry or machine identifier. Requests
go to the public [GitHub Releases API](https://docs.github.com/en/rest/releases/releases)
and the repository's GitHub download endpoints/CDNs; no GitHub token is needed.

Only an explicit installation click launches one temporary Windows PowerShell
helper. It verifies the package again, checks safe ZIP paths, its complete file
manifest and application version, then asks copies from the same application
folder to close normally. A note conflict, close refusal or timeout stops file
replacement. Existing integration preferences and user data are retained. The
file transaction reuses the manifest-safe update/rollback code; unknown files
in the application folder are preserved. A successful update restarts the app.

### When the new version counts as started

Whether a process is merely alive says little: a broken version can live a few
seconds before it fails. So the old files stay in the update backup until the
new version proves itself:

- When its normal main window has started (`App.StartQuietUpdates` calls
  `QuietUpdateService.Start`), the app writes `updates\started.json` in its
  state folder with its version and process id. This is the only write at startup.
- The helper waits up to 120 seconds for that report from exactly the process
  it restarted and exactly the installed version. Then the backup is deleted.
- If that process ends with an error code first, the update failed. The helper
  hands dialogs back (`--dialog-recover`, bounded, when integration was on),
  stops every process still running from the application folder (a dialog
  agent the new version may have started holds its files), then rolls back.
  The rollback tries every file: one that stays locked is reported and its old
  copy kept in the backup, and the remaining old files still come back. The
  previous application is restarted and the result names any file left behind.
- If the process ends normally before reporting (somebody closed it), or is
  still running without a report after 120 seconds, the update is not undone
  under a running app. It is reported as installed, and `result.json` names the
  retained backup folder (`BackupRetained`) beside the application folder.

After a successful update of a Setup installation, the helper also changes
`DisplayVersion` in Setup's per-user uninstall entry, but only when that entry's
`InstallLocation` is exactly the updated folder; a portable copy has none.

Not covered yet: the package is verified against the release's SHA-256 file and
the GitHub asset digest, both served by the same GitHub release. Neither proves
who built it; a signature over the package (needs a release signing key) remains
to be added. The release workflow pins every action to a full commit SHA.

The same writable folder is updated for installer and portable copies. Protected
or read-only locations safely refuse the operation before any window closes;
use the ordinary installer to update such an installation. If integration
recovery stalls, the confirmed helper waits for its own recovery operation before
restoring preferences, avoiding a late recovery undoing the restored settings.
It does not force-terminate the running application or start itself again at
sign-in; only processes of a new version that failed to start are ended, after
a normal close request to its windows, before the rollback.

## Verification

`QuietUpdateChecks` uses generated HTTP responses and a controlled clock for
startup delay, daily checks, cancellation, channel/version selection, bounded
downloads, hash failures, resumable transfers, cache retention and process leases.
`QuietUpdateUiChecks` exercises an isolated unshown window: every automatic state
keeps the panel closed, while explicit clicks, dismissal and the opt-out switch
control the offer. `tests/QuietUpdateApplyChecks.ps1` tests generated packages,
manifest transactions, rollback, close-veto handling and integration restoration
using process adapters, plus the startup report, a crash before it, and a
rollback past a locked file. `Review1009InstallChecks` covers the future
timestamp, the spent package and the startup report in the app itself. The
ordinary payload regression checks remain included.

These checks do not wait a real day, replace the user's running installation,
or claim that every antivirus, network, protected-folder or recovery-stall case
has been exercised on a real desktop.

The download uses streaming `ResponseHeadersRead`, bounded bodies, explicit
timeouts and an allowlist for redirects, following the
[.NET HTTP contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption).
Velopack was inspected as an existing option, but its
[installation requirements](https://docs.velopack.io/integrating/overview)
would require changing the current packaging. The existing payload transaction
is reused instead.
