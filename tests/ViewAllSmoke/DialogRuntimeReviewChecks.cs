using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in the dialog integration's background processes -
/// the settings they share, the watchdog, the listener - and what was done
/// about it, a section each.
///
/// <para>The background processes these checks start are the test copy's own,
/// each with a state folder of its own inside this run's, never the user's;
/// the "dialog" a watchdog protects is a window of this process that is never
/// seen (cloaked and transparent, on the secondary monitor) and never
/// activated.</para>
/// </summary>
internal static partial class Program
{
    private static Task DialogRuntimeReviewChecks()
    {
        DialogSettingsReplaceChecks();
        DialogDiagnosticsRunChecks();
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (the background-process checks need ULTRAEXPLORER_STATE_DIR: skipped)");
            return Task.CompletedTask;
        }
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  (no secondary monitor for the stand-in dialog: the background-process checks are skipped)");
            return Task.CompletedTask;
        }
        if (!File.Exists(DialogSelfProcess.ExecutablePath))
        {
            Console.WriteLine($"  ({DialogSelfProcess.ExecutablePath} is not beside the checks: the background-process checks are skipped)");
            return Task.CompletedTask;
        }
        Check("a state folder's instance key is computed here as UltraExplorer computes it",
            InstanceKeyFor(AppPaths.StateDirectory) == DialogIntegrationStore.InstanceKey);
        GuardianAsleepChecks(target.Work);
        GuardianHeldWithinTurnChecks(target.Work);
        GuardianUnwritableSettingsChecks(target.Work);
        AgentBusySettingsChecks();
        return Task.CompletedTask;
    }

    // ---- the settings file, while it is being read -------------------------------------

    private static void DialogSettingsReplaceChecks()
    {
        Section("review: the integration settings are written while something reads them");
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (needs ULTRAEXPLORER_STATE_DIR: skipped, so the real settings stay untouched)");
            return;
        }
        var before = DialogIntegrationStore.Read();
        try
        {
            // Once, so that the write below is as quick as any later one.
            DialogIntegrationStore.Update(settings => settings with { LastRecovery = "review: before" });
            Exception? failure = null;
            // A reader of the kind every listener and replacement is, several
            // times a second: open for a moment, sharing everything.
            using (var reader = new FileStream(DialogIntegrationStore.SettingsPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var closing = Task.Run(() => { Thread.Sleep(60); reader.Dispose(); });
                try { DialogIntegrationStore.Update(settings => settings with { LastRecovery = "review: after" }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
                closing.Wait();
            }
            Check($"a settings write is not refused because a reader has the file open{(failure is null ? "" : $" ({failure.GetType().Name}: {failure.Message})")}",
                failure is null);
            Check("and what it wrote is what is read afterwards", DialogIntegrationStore.Read().LastRecovery == "review: after");
        }
        finally
        {
            try { DialogIntegrationStore.Update(_ => before); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- a benchmark or snapshot run ---------------------------------------------------

    private static void DialogDiagnosticsRunChecks()
    {
        Section("review: a benchmark or snapshot run never touches the sign-in entries");
        Check("a benchmark run is a diagnostics copy",
            DialogStartup.IsDiagnosticsRun(["UltraExplorer.exe", "--nested-bench", "route.tsv"]));
        Check("a snapshot run is one too, however the switch is written",
            DialogStartup.IsDiagnosticsRun(["UltraExplorer.exe", "--NESTED-SNAPSHOTS", "out"]));
        Check("an ordinary start is not",
            !DialogStartup.IsDiagnosticsRun(["UltraExplorer.exe"]) && !DialogStartup.IsDiagnosticsRun(["UltraExplorer.exe", "--settings"]));
        Check("a folder merely named like the switch is not",
            !DialogStartup.IsDiagnosticsRun(["UltraExplorer.exe", @"C:\work\--nested-bench"]));
        Check("this test copy is never allowed to write them", !DialogStartup.IsAllowed);
    }

    // ---- the watchdog ------------------------------------------------------------------

    /// <summary>
    /// The computer asleep with a replaced dialog open: the watchdog does not
    /// run, the replacement does not pulse, and the clock goes on. Simulated
    /// by holding the watchdog's process for longer than the heartbeat's
    /// limit while the replacement is silent, as sleep holds both: woken, the
    /// watchdog must not take the time it did not run for a hung replacement.
    /// </summary>
    private static void GuardianAsleepChecks(TestScreen.Box work)
    {
        Section("review: the dialog watchdog after the computer slept");
        using var scene = GuardianScene.Start(work);
        if (!scene.Ready) { Check($"the watchdog took the stand-in's lease ({scene.Problem})", false); return; }
        Thread.Sleep(600);
        Check("a replacement that keeps answering is left alone", !scene.Recovered && !scene.Worker.HasExited);

        // Both silent, as when the computer goes to sleep: the last pulse
        // taken by the watchdog, then neither runs.
        scene.Beating = false;
        Thread.Sleep(300);
        if (NtSuspendProcess(scene.Guardian.Handle) != 0) { Check("the watchdog's process could be held", false); return; }
        try { Thread.Sleep(6000); }
        finally { NtResumeProcess(scene.Guardian.Handle); }
        // Woken: the watchdog looks first, the replacement answers a moment later.
        Thread.Sleep(400);
        scene.Beating = true;
        Thread.Sleep(1200);
        Check("waking up is not taken for a replacement that stopped answering: the dialog stays with it",
            !scene.Recovered && !scene.Worker.HasExited);
        Check("and the mode stays on", scene.Enabled);
        Check("the watchdog still guards it", !scene.Guardian.HasExited);
    }

    /// <summary>
    /// The same, with the watchdog held in the middle of one of its turns
    /// rather than between two (<see cref="TurnHold"/>): the time it is held
    /// there counts no more against the replacement than time between turns
    /// does. Nor does a heartbeat it takes right after the hold put off the
    /// end of a replacement that stops answering then.
    /// </summary>
    private static void GuardianHeldWithinTurnChecks(TestScreen.Box work)
    {
        Section("review: the dialog watchdog held in the middle of a turn");
        using (var scene = GuardianScene.Start(work))
        {
            if (!scene.Ready) { Check($"the watchdog took the stand-in's lease ({scene.Problem})", false); return; }
            Thread.Sleep(600);
            scene.Beating = false;
            Thread.Sleep(300);
            using (var hold = new TurnHold(scene.SettingsPath))
            {
                Check($"the watchdog is held in the middle of a turn{(hold.Holding ? "" : $" ({hold.Problem})")}", hold.Holding);
                if (!hold.Holding) return;
                Thread.Sleep(6000);
            }
            Thread.Sleep(400);
            scene.Beating = true;
            Thread.Sleep(1200);
            Check("a hold within a turn is not taken for a replacement that stopped answering: the dialog stays with it",
                !scene.Recovered && !scene.Worker.HasExited);
            Check("and the mode stays on", scene.Enabled);
        }
        using (var scene = GuardianScene.Start(work))
        {
            if (!scene.Ready) { Check($"the watchdog took the stand-in's lease ({scene.Problem})", false); return; }
            Thread.Sleep(600);
            using (var hold = new TurnHold(scene.SettingsPath))
            {
                Check($"the watchdog is held in the middle of a turn again{(hold.Holding ? "" : $" ({hold.Problem})")}", hold.Holding);
                if (!hold.Holding) return;
                Thread.Sleep(6000);
                // The replacement stops answering as the watchdog comes back:
                // its last heartbeat is the one the watchdog takes after the hold.
                scene.Beating = false;
                Thread.Sleep(150);
            }
            var released = Stopwatch.StartNew();
            var recovered = WaitFor(() => scene.Recovered, 9000);
            var took = released.ElapsedMilliseconds;
            Check($"a replacement that stops answering right after the hold is still ended within the heartbeat's limit ({took} ms)",
                recovered && took < 8000);
            Check("and it is ended", WaitFor(() => scene.Worker.HasExited, 1500));
        }
    }

    /// <summary>
    /// A replacement that stops answering while another process holds the
    /// integration settings: the watchdog puts the dialog back and ends the
    /// replacement at once, and a pause it cannot write does not end the
    /// watchdog itself.
    /// </summary>
    private static void GuardianUnwritableSettingsChecks(TestScreen.Box work)
    {
        Section("review: the dialog watchdog when the settings cannot be written");
        using var scene = GuardianScene.Start(work);
        if (!scene.Ready) { Check($"the watchdog took the stand-in's lease ({scene.Problem})", false); return; }
        Thread.Sleep(600);
        using var held = new SettingsLockHolder(scene.Key);
        Check("another process holds the stand-in's settings", held.Taken);
        if (!held.Taken) return;
        scene.Beating = false;
        var recovered = WaitFor(() => scene.Recovered, 9000);
        Check("a replacement that stopped answering has its dialog put back", recovered);
        var recoveredAt = Stopwatch.StartNew();
        var ended = WaitFor(() => scene.Worker.HasExited, 1500);
        Check("the replacement is ended at once, although the pause cannot be written yet", ended);
        // Past the three seconds the pause waits for the settings.
        Thread.Sleep(Math.Max(0, 3600 - (int)recoveredAt.ElapsedMilliseconds));
        held.Release();
        Thread.Sleep(1000);
        Check("the watchdog goes on after a pause it could not write", !scene.Guardian.HasExited);
        Check("the pause it could not write is in the log",
            File.Exists(scene.LogPath) && File.ReadAllText(scene.LogPath).Contains("Could not pause replacement after a recovery", StringComparison.Ordinal));
    }

    /// <summary>
    /// One replaced dialog, as the watchdog sees it: a lease on a window of
    /// this process (never seen: cloaked, transparent, on the secondary
    /// monitor), a heartbeat this process pulses, and as its replacement an
    /// idle UltraExplorer process of the test copy - a watchdog of a state
    /// folder of its own with nothing to guard - so that the one under test
    /// accepts it as one of its own and may end it.
    /// </summary>
    private sealed class GuardianScene : IDisposable
    {
        private readonly string _state, _workerState, _lease, _token = Guid.NewGuid().ToString("N");
        private readonly EventWaitHandle _beat;
        private readonly Thread _pulse;
        private CloakedStandIn? _dialog;
        private Process? _worker, _guardian;
        private volatile bool _stopped;
        private const int Cookie = 0x5EE9;

        public Process Worker => _worker!;
        public Process Guardian => _guardian!;
        public string Key { get; }
        public bool Ready { get; private set; }
        public string Problem { get; private set; } = string.Empty;
        public volatile bool Beating = true;
        public bool Recovered => File.Exists(_lease + ".recovered");
        public bool Enabled => ReadEnabled(_state);
        public string LogPath => Path.Combine(_state, "dialog-integration", "integration.log");
        public string SettingsPath => Path.Combine(_state, "dialog-integration.json");

        private GuardianScene()
        {
            _state = Path.Combine(AppPaths.StateDirectory, "review-guardian-" + Guid.NewGuid().ToString("N"));
            _workerState = _state + "-worker";
            Key = InstanceKeyFor(_state);
            _lease = Path.Combine(_state, "dialog-integration", "sessions", _token + ".json");
            _beat = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UltraExplorer.DialogBeat." + _token);
            _pulse = new Thread(() => { while (!_stopped) { if (Beating) _beat.Set(); Thread.Sleep(100); } })
                { IsBackground = true, Name = "review replacement heartbeat" };
        }

        /// <summary>Everything in place and the watchdog started; <see cref="Ready"/> once it has taken the lease.</summary>
        public static GuardianScene Start(TestScreen.Box work)
        {
            var scene = new GuardianScene();
            try { scene.Arrange(work); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { scene.Problem = ex.GetType().Name + ": " + ex.Message; }
            return scene;
        }

        private void Arrange(TestScreen.Box work)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lease)!);
            Directory.CreateDirectory(_workerState);
            File.WriteAllText(Path.Combine(_state, "dialog-integration.json"), "{\"Enabled\":true}");
            File.WriteAllText(Path.Combine(_workerState, "dialog-integration.json"), "{\"Enabled\":true}");
            _pulse.Start();

            _dialog = new CloakedStandIn(work, "UltraExplorer watchdog check: dialog");
            if (_dialog.Handle == 0) { Problem = "the stand-in dialog could not be made"; return; }
            // Transparent as well as cloaked, and leased as a window that was
            // transparent: whatever the watchdog puts back is never seen.
            DialogNative.SetExStyle(_dialog.Handle, 0x80000, true);
            SetLayeredAttributes(_dialog.Handle, 0, 0, 2);
            DialogNative.SetProp(_dialog.Handle, DialogNative.LeaseProperty, Cookie);

            var worker = _worker = StartTestCopy(_workerState, ["--dialog-guardian"]);
            if (!WaitFor(() => MutexExists(@"Local\UltraExplorer.DialogGuardian." + InstanceKeyFor(_workerState)), 10000))
            { Problem = "the stand-in replacement did not start"; return; }
            var record = new DialogLeaseRecord
            {
                Token = _token, NativeWindow = _dialog.Handle, NativeProcess = (uint)Environment.ProcessId,
                WorkerProcess = worker.Id, WorkerStarted = worker.StartTime.ToUniversalTime().Ticks, Cookie = Cookie,
                Heartbeat = @"Local\UltraExplorer.DialogBeat." + _token,
                WasLayered = true, OriginalColour = 0, OriginalAlpha = 0, OriginalTransparencyFlags = 2
            };
            File.WriteAllText(_lease, JsonSerializer.Serialize(record));
            if (!DialogLease.BelongsToWindow(record) || !DialogNative.IsHidden(_dialog.Handle))
            { Problem = "the stand-in dialog is not hidden under its lease"; return; }
            var guardian = _guardian = StartTestCopy(_state, ["--dialog-guardian"]);
            Ready = WaitFor(() => DialogLease.ReadGuardianIdentity(_lease + ".ready") is { } identity && identity.Process == guardian.Id, 10000);
            if (!Ready) Problem = "no ready marker";
        }

        public void Dispose()
        {
            _stopped = true;
            if (_pulse.IsAlive) _pulse.Join(1000);
            // The lease goes first, so that nothing is put back on the way out.
            foreach (var suffix in new[] { "", ".ready", ".recovered" }) TryDeleteFile(_lease + suffix);
            foreach (var state in new[] { _state, _workerState })
                if (Directory.Exists(state)) File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":false}");
            foreach (var process in new[] { _guardian, _worker })
            {
                if (process is null) continue;
                try { if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            if (_dialog is not null)
            {
                if (_dialog.Handle != 0) DialogNative.RemoveProp(_dialog.Handle, DialogNative.LeaseProperty);
                _dialog.Dispose();
            }
            _beat.Dispose();
            TryDelete(_state);
            TryDelete(_workerState);
        }
    }

    // ---- the listener ------------------------------------------------------------------

    /// <summary>
    /// At sign-in everything starts at once, and another process can hold the
    /// integration settings for longer than the listener's start waits for
    /// them. That is no failure of the listener: it goes on, and the mode
    /// stays on. The listener is a test copy watching only this process.
    /// </summary>
    private static void AgentBusySettingsChecks()
    {
        Section("review: the dialog listener when the settings are busy as it starts");
        var state = Path.Combine(AppPaths.StateDirectory, "review-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":true}");
        var key = InstanceKeyFor(state);
        var agentMutex = @"Local\UltraExplorer.DialogAgent." + key;
        Process? agent = null;
        try
        {
            using (var held = new SettingsLockHolder(key))
            {
                Check("another process holds the listener's settings", held.Taken);
                if (!held.Taken) return;
                var listener = agent = StartTestCopy(state, ["--dialog-agent"],
                    ("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                // Its lock is taken as it starts, before the settings are asked for.
                var starting = WaitFor(() => MutexExists(agentMutex) || listener.HasExited, 10000) && !listener.HasExited;
                Check("the listener starts", starting);
                if (!starting) return;
                // Longer than its start waits for the settings.
                Thread.Sleep(3600);
            }
            Thread.Sleep(2500);
            Check("a listener that found the settings busy as it started is still running",
                !agent.HasExited && MutexExists(agentMutex));
            Check("and the mode is still on", ReadEnabled(state));
            var log = Path.Combine(state, "dialog-integration", "integration.log");
            Check("the busy settings are in the log",
                File.Exists(log) && File.ReadAllText(log).Contains("The integration settings are busy", StringComparison.Ordinal));
        }
        finally
        {
            // Switched off, the listener ends, and with it what it started.
            File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":false}");
            if (agent is not null)
            {
                var agentStarted = agent.StartTime;
                try { if (!agent.WaitForExit(5000)) { agent.Kill(); agent.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                EndChildren(agent.Id, agentStarted);
                agent.Dispose();
            }
            TryDelete(state);
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>As <see cref="DialogIntegrationStore.InstanceKey"/>, for a state folder other than this process's own.</summary>
    private static string InstanceKeyFor(string stateDirectory) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            WindowsIdentity.GetCurrent().User?.Value + "|" + Path.GetFullPath(stateDirectory).ToUpperInvariant())))[..24];

    private static bool MutexExists(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    private static bool ReadEnabled(string state)
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json")));
            return settings.RootElement.GetProperty("Enabled").GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return false; }
    }

    /// <summary>The test copy's own executable in one of its roles, with a state folder of its own, as a test window.</summary>
    private static Process StartTestCopy(string state, string[] arguments, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo(DialogSelfProcess.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[AppPaths.StateDirectoryVariable] = state;
        start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
        foreach (var (name, value) in environment) start.Environment[name] = value;
        return Process.Start(start) ?? throw new IOException("The test copy could not be started.");
    }

    /// <summary>Whatever the given process of this test started (a watchdog, a prepared worker) and left running: those only.</summary>
    private static void EndChildren(int parent, DateTime parentStarted)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DialogSelfProcess.ExecutablePath)))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || process.StartTime < parentStarted) continue;
                    var basics = new RuntimeProcessBasics();
                    if (QueryProcessBasics(process.Handle, 0, ref basics, Marshal.SizeOf<RuntimeProcessBasics>(), out _) != 0
                        || basics.Parent.ToInt64() != parent) continue;
                    if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(3000); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /// <summary>
    /// The integration settings' lock, by name, as another process holds it
    /// while it changes them: on a thread of its own (a mutex belongs to its
    /// thread), until released.
    /// </summary>
    private sealed class SettingsLockHolder : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _release = new();
        public bool Taken { get; private set; }

        public SettingsLockHolder(string key)
        {
            using var taken = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, @"Local\UltraExplorer.DialogSettings." + key);
                try { Taken = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { Taken = true; }
                taken.Set();
                if (!Taken) return;
                try { _release.Wait(TimeSpan.FromSeconds(30)); }
                finally { mutex.ReleaseMutex(); }
            }) { IsBackground = true, Name = "review settings lock" };
            _thread.Start();
            taken.Wait(TimeSpan.FromSeconds(6));
        }

        public void Release()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Release();
    }

    /// <summary>
    /// A watchdog held in the middle of a turn, as sleep can hold it anywhere:
    /// an opportunistic lock on the settings file it opens once a turn, after
    /// the turn has begun. Windows lets that open wait until the lock is let
    /// go, and tells the lock's holder when it has come.
    /// </summary>
    private sealed class TurnHold : IDisposable
    {
        private readonly ManualResetEvent _broken = new(false);
        private readonly nint _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _file;
        public bool Holding { get; }
        public string Problem { get; } = string.Empty;

        public TurnHold(string settings)
        {
            // Granted only while nothing else has the file open, and the
            // watchdog reads it every tenth of a second: tried for a while.
            var error = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                _broken.Reset();
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _broken.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
                // Read, sharing everything, an existing file, overlapped; a batch lock, pending until broken.
                var file = OpenOverlapped(settings, 0x80000000, 7, 0, 3, 0x40000000, 0);
                if (!file.IsInvalid && !RequestLock(file, 0x00090008, 0, 0, 0, 0, out _, _overlapped)
                    && (error = Marshal.GetLastWin32Error()) == 997) { _file = file; break; }
                if (file.IsInvalid) error = Marshal.GetLastWin32Error();
                file.Dispose();
                Thread.Sleep(5);
            }
            if (_file is null) { Problem = $"the settings could not be locked: error {error}"; return; }
            Holding = _broken.WaitOne(3000);
            if (!Holding) Problem = "the watchdog did not come to its settings";
        }

        /// <summary>Lets go: the watchdog's open goes ahead, and the lock's request ends with the file.</summary>
        public void Dispose()
        {
            _file?.Dispose();
            // Windows may otherwise still write to both: left as they are then.
            if (_file is not null && !_broken.WaitOne(2000)) return;
            Marshal.FreeHGlobal(_overlapped);
            _broken.Dispose();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenOverlapped(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    private static extern bool RequestLock(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, nint input, int inputLength, nint output, int outputLength, out int returned, nint overlapped);
    [StructLayout(LayoutKind.Sequential)] private struct RuntimeProcessBasics { public nint ExitStatus, Peb, Affinity, Priority, Process, Parent; }
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int QueryProcessBasics(nint process, int information, ref RuntimeProcessBasics buffer, int length, out int returned);
    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    private static extern bool SetLayeredAttributes(nint window, uint colour, byte alpha, uint flags);
}
