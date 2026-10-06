using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the replacement of another program's
/// dialog as it runs for the user, and what was done about it: the moment the
/// picker comes over the dialog (J104), and the user going on in the Windows
/// dialog while the picker is still on its way (J066).
///
/// <para>As in real use: a test copy's dialog listener with its prepared
/// worker, waiting, and then the fixture's own Windows dialog appearing in a
/// process of its own. All of it on the secondary monitor and never
/// activated; the test copy has a state folder of its own and watches only the
/// fixture. Without a second monitor nothing is opened.</para>
/// </summary>
internal static partial class Program
{
    private static Task PickerDialogsAgentReviewChecks()
    {
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (needs ULTRAEXPLORER_STATE_DIR: skipped)");
            return Task.CompletedTask;
        }

        if (!File.Exists(DialogSelfProcess.ExecutablePath))
        {
            Console.WriteLine($"  ({DialogSelfProcess.ExecutablePath} is not beside the checks: skipped)");
            return Task.CompletedTask;
        }

        if (TestScreen.Target() is null)
        {
            Console.WriteLine("  (no secondary monitor for the dialogs: skipped)");
            return Task.CompletedTask;
        }

        PickerAgentUncloakChecks();
        PickerAgentTypedOnChecks();
        return Task.CompletedTask;
    }

    // ---- J104: the picker comes over the dialog ------------------------------------------

    /// <summary>
    /// The prepared picker coming over the Windows dialog: the dialog goes
    /// from the screen as the picker comes on it - never a frame with neither
    /// of them, the application's window or the desktop showing through.
    /// </summary>
    private static void PickerAgentUncloakChecks()
    {
        Section("picker review: the picker comes over the Windows dialog in one frame (J104)");
        using var run = new AgentDialogRun("save", 0);
        Check($"a prepared worker waits for the fixture's dialog ({run.Problem})", run.Picker != 0);
        if (run.Picker == 0) return;

        using var watch = new NeitherShownWatch(run);
        run.Show();
        var shown = WaitFor(() => !DialogNative.IsCloaked(run.Picker) && run.Dialog != 0 && DialogNative.IsHidden(run.Dialog), 10_000);
        Thread.Sleep(150);
        watch.Stop();
        Check("the picker comes over the dialog, which goes from the screen", shown);
        Console.WriteLine($"  sampled both-hidden gap {watch.LongestMilliseconds:F2} ms; DWM vblank timestamp changes during it: {watch.FramesPassed} ({watch.Samples})");
        Check("the screen's refreshes are counted", watch.Counting);
        Check("no sampled interval leaves both the dialog and picker hidden, and no DWM vblank update witnesses such a gap",
            shown && watch.LongestMilliseconds == 0 && watch.FramesPassed == 0);
        var log = run.Finish();
        foreach (var line in log.Split('\n').Where(line => line.Contains("timing path=", StringComparison.Ordinal)))
            Console.WriteLine("  log: " + line.Trim()[..Math.Min(line.Trim().Length, 300)]);
        Check("the prepared picker comes on screen under its early lease, including a budgeted full-read fallback",
            (log.Contains("timing path=prepared-early", StringComparison.Ordinal) || log.Contains("timing path=prepared ", StringComparison.Ordinal))
            && log.Contains(" early-leased=", StringComparison.Ordinal));
    }

    // ---- J066: the user goes on in the Windows dialog ------------------------------------

    /// <summary>
    /// A Save dialog in a folder the picker is slow to draw - longer than the
    /// moment it is given before the full read. Meanwhile the Windows dialog
    /// is on screen and has the keyboard, and the user types on in its name
    /// box. The picker does not come over it with the name it read before:
    /// the dialog stays with Windows, with what the user typed. It used to be
    /// swapped in, and the user's next keys went into the picker's name box.
    /// </summary>
    private static void PickerAgentTypedOnChecks()
    {
        Section("picker review: the user goes on in the Windows dialog while the picker is on its way (J066)");
        using var run = new AgentDialogRun("save-nofilter", SlowFolderFiles, handoffDelay: 1500);
        Check($"a prepared worker waits for the fixture's dialog ({run.Problem})", run.Picker != 0);
        if (run.Picker == 0) return;

        run.Show();
        Check($"the fixture's Windows Save dialog opens on a secondary monitor ({run.Problem})", run.Dialog != 0 && TestScreen.OnSecondary(run.Dialog));
        if (run.Dialog == 0) return;
        var name = DialogNative.FindChild(run.Dialog, 1001, "Edit");
        Check("its name box is found", name != 0);
        if (name == 0) return;

        // The user types on, a key every few milliseconds, as long as the
        // dialog is theirs - or until the picker comes over it.
        var typed = "export";
        long cameAt = -1;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 6000)
        {
            if (DialogNative.IsWindowVisible(run.Picker) && !DialogNative.IsCloaked(run.Picker)) { cameAt = clock.ElapsedMilliseconds; break; }
            try
            {
                DialogNative.SetEdit(name, typed + "s");
                typed += "s";
            }
            catch (IOException) { }

            Thread.Sleep(15);
        }

        Console.WriteLine($"  typed {typed.Length - "export".Length} keys over {clock.ElapsedMilliseconds} ms; the picker {(cameAt >= 0 ? $"came over the dialog after {cameAt} ms" : "never came")}");
        Check("the owned slow-folder fixture keeps a deterministic handoff delay while typing continues", cameAt is < 0 or > 1500);
        Check("the picker does not come over the dialog the user is typing in", cameAt < 0);
        Check("the dialog stays on screen, with what the user typed",
            DialogNative.IsWindow(run.Dialog) && !DialogNative.IsHidden(run.Dialog) && DialogNative.ReadEdit(name) == typed);
        var log = run.Finish();
        foreach (var line in log.Split('\n').Where(line => line.Contains("timing path=", StringComparison.Ordinal)
            || line.Contains("stays with Windows", StringComparison.Ordinal) || line.Contains("Windows dialog", StringComparison.Ordinal)))
            Console.WriteLine("  log: " + line.Trim()[..Math.Min(line.Trim().Length, 300)]);
    }

    /// <summary>Files in the folder of the dialog the picker is slow to draw: more than it reads and draws in its moment.</summary>
    private const int SlowFolderFiles = 40_000;

    /// <summary>
    /// One dialog replaced as in real use: the fixture's Windows dialog, held
    /// until <see cref="Show"/>, and a test copy's listener whose prepared
    /// worker is ready and settled by then. Disposed: the mode is switched off
    /// (the test copy's processes end with it), the dialog cancelled if it is
    /// still there, and everything made for it removed.
    /// </summary>
    private sealed class AgentDialogRun : IDisposable
    {
        private readonly string _state, _root, _output;
        private readonly EventWaitHandle _signal;
        private readonly Process? _fixture, _agent;
        private readonly DateTime _agentStarted;
        private bool _finished;
        private string _log = string.Empty;

        public nint Picker { get; }
        public nint Dialog { get; private set; }
        public string Problem { get; private set; } = "ready";
        public uint FixtureProcess => (uint)(_fixture?.Id ?? 0);

        public AgentDialogRun(string mode, int files, int handoffDelay = 0)
        {
            var id = Guid.NewGuid().ToString("N");
            _state = Path.Combine(AppPaths.StateDirectory, "review-agent-dialog-" + id);
            _root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerAgentReview", id);
            _output = Path.Combine(_root, "result.json");
            Directory.CreateDirectory(_state);
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_state, "dialog-integration.json"), "{\"Enabled\":true}");
            File.WriteAllText(Path.Combine(_root, "sample.txt"), "fixture");
            var made = Stopwatch.StartNew();
            for (var index = 0; index < files; index++) File.Create(Path.Combine(_root, $"bulk-{index:D6}.txt")).Dispose();
            if (files > 0) Console.WriteLine($"  {files:N0} files made in {made.ElapsedMilliseconds} ms");
            var signal = @"Local\UltraExplorer.PickerAgentReview." + id;
            _signal = new EventWaitHandle(false, EventResetMode.ManualReset, signal);

            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--native-dialog-fixture", _root, _output, mode }) start.ArgumentList.Add(argument);
            start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
            start.Environment["NATIVE_DIALOG_FIXTURE_SHOW_SIGNAL"] = signal;
            _fixture = Process.Start(start);
            if (_fixture is null || !WaitFor(() => File.Exists(_output + ".ready") || _fixture.HasExited, 15_000) || _fixture.HasExited)
            {
                Problem = "the fixture did not start";
                return;
            }

            _agent = StartTestCopy(_state, ["--dialog-agent"],
                ("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS", _fixture.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("ULTRAEXPLORER_DIALOG_TEST_HANDOFF_DELAY_MS", handoffDelay.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("ULTRAEXPLORER_DIALOG_TEST_WAIT_FOR_WARM", "1"));
            _agentStarted = _agent.StartTime;
            var log = Path.Combine(_state, "dialog-integration", "integration.log");
            if (!WaitFor(() => ReadShared(log).Contains("Prepared dialog worker is ready.", StringComparison.Ordinal) || _agent.HasExited, 30_000)
                || _agent.HasExited)
            {
                Problem = "the prepared worker did not get ready";
                return;
            }

            // Settled, as a worker is by the time a dialog comes in real use.
            Thread.Sleep(2000);
            Picker = PreparedPickerOf(_agent);
            if (Picker == 0) Problem = "no prepared picker was found";
        }

        /// <summary>The dialog appears; returns once it is there.</summary>
        public void Show()
        {
            _signal.Set();
            nint found = 0;
            WaitFor(() =>
            {
                DialogNative.EnumWindows((window, _) =>
                {
                    if (DialogNative.ProcessId(window) == FixtureProcess && DialogNative.LooksLikeFileDialog(window)) found = window;
                    return found == 0;
                }, 0);
                return found != 0 || _fixture!.HasExited;
            }, 15_000);
            Dialog = found;
            if (found == 0) Problem = "no dialog appeared";
        }

        /// <summary>Ends the replacement (the mode off), and returns what its log says.</summary>
        public string Finish()
        {
            if (_finished) return _log;
            _finished = true;
            File.WriteAllText(Path.Combine(_state, "dialog-integration.json"), "{\"Enabled\":false}");
            if (_agent is not null)
            {
                try { if (!_agent.WaitForExit(8000)) { _agent.Kill(); _agent.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                EndChildren(_agent.Id, _agentStarted);
            }

            if (Dialog != 0 && DialogNative.IsWindow(Dialog))
            {
                try { DialogNative.Click(Dialog, 2); }
                catch (System.ComponentModel.Win32Exception) { }
            }

            _log = ReadShared(Path.Combine(_state, "dialog-integration", "integration.log"));
            return _log;
        }

        public void Dispose()
        {
            Finish();
            _signal.Set();
            if (_fixture is not null)
            {
                try { if (!_fixture.WaitForExit(5000)) { _fixture.Kill(); _fixture.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                _fixture.Dispose();
            }

            _agent?.Dispose();
            _signal.Dispose();
            TryDelete(_state);
            TryDelete(_root);
        }

        private static string ReadShared(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
        }

        /// <summary>The prepared worker's picker: shown and cloaked, in a test copy the listener started.</summary>
        private static nint PreparedPickerOf(Process agent)
        {
            var workers = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DialogSelfProcess.ExecutablePath)))
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(process.MainModule?.FileName, DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                        var basics = new RuntimeProcessBasics();
                        if (QueryProcessBasics(process.Handle, 0, ref basics, Marshal.SizeOf<RuntimeProcessBasics>(), out _) == 0
                            && basics.Parent.ToInt64() == agent.Id) workers.Add((uint)process.Id);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }

            nint picker = 0;
            DialogNative.EnumWindows((window, _) =>
            {
                if (workers.Contains(DialogNative.ProcessId(window)) && DialogNative.IsWindowVisible(window) && DialogNative.IsCloaked(window)
                    && DialogNative.ClassName(window).StartsWith("HwndWrapper", StringComparison.Ordinal)) picker = window;
                return picker == 0;
            }, 0);
            return picker;
        }
    }

    /// <summary>
    /// Samples, as fast as it can, whether the dialog and its picker are both
    /// off the screen - the dialog hidden, the picker still cloaked - and
    /// counts the screen refreshes the compositor makes while they are.
    /// </summary>
    private sealed class NeitherShownWatch : IDisposable
    {
        private readonly Thread _thread;
        private readonly byte[] _timing = new byte[292];
        private readonly AgentDialogRun _run;
        private volatile bool _stop;
        private long _longest;
        private int _frames, _counting, _samples, _hiddenSamples;
        public string Samples => $"{Volatile.Read(ref _samples)} samples, {Volatile.Read(ref _hiddenSamples)} with the dialog hidden";

        public NeitherShownWatch(AgentDialogRun run)
        {
            _run = run;
            _thread = new Thread(Run) { IsBackground = true, Name = "neither shown watch", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public double LongestMilliseconds => Interlocked.Read(ref _longest) * 1000.0 / Stopwatch.Frequency;
        public int FramesPassed => Volatile.Read(ref _frames);
        public bool Counting => Volatile.Read(ref _counting) != 0;

        private void Run()
        {
            long since = 0;
            ulong frameAtStart = 0, firstFrame = RefreshCount();
            nint dialog = 0;
            while (!_stop)
            {
                if (dialog == 0)
                {
                    // Found as soon as it appears: the replacement takes it within tens of milliseconds.
                    DialogNative.EnumWindows((window, _) =>
                    {
                        if (DialogNative.ProcessId(window) == _run.FixtureProcess && DialogNative.LooksLikeFileDialog(window)) dialog = window;
                        return dialog == 0;
                    }, 0);
                    continue;
                }

                var hidden = DialogNative.IsHidden(dialog);
                var cloaked = DialogNative.IsCloaked(_run.Picker);
                Interlocked.Increment(ref _samples);
                if (hidden) Interlocked.Increment(ref _hiddenSamples);
                var now = Stopwatch.GetTimestamp();
                var frame = RefreshCount();
                // Bracket the timestamp with both states, avoiding a mixed
                // observation from opposite sides of the transition.
                var neither = hidden && cloaked && DialogNative.IsHidden(dialog) && DialogNative.IsCloaked(_run.Picker);
                if (frame != 0 && frame != firstFrame) Interlocked.Exchange(ref _counting, 1);
                if (neither && since == 0)
                {
                    since = now;
                    frameAtStart = frame;
                    Interlocked.CompareExchange(ref _longest, 1, 0);
                }
                else if (neither)
                {
                    if (now - since > Interlocked.Read(ref _longest)) Interlocked.Exchange(ref _longest, now - since);
                    if (frame != frameAtStart && frame != 0 && frameAtStart != 0)
                    {
                        Interlocked.Increment(ref _frames);
                        frameAtStart = frame;
                    }
                }
                else
                {
                    since = 0;
                }
            }
        }

        /// <summary>
        /// Last compositor vblank QPC timestamp. On this Windows session
        /// cRefresh advances on every API query, not every physical refresh;
        /// it cannot be used as a frame counter. This timestamp is a timing
        /// witness, not a recording or a count of all physical display frames.
        /// </summary>
        private ulong RefreshCount()
        {
            var info = _timing;
            BitConverter.TryWriteBytes(info.AsSpan(0, 4), info.Length);
            return DwmGetCompositionTimingInfo(0, info) == 0 ? BitConverter.ToUInt64(info, 28) : 0;
        }

        public void Stop()
        {
            _stop = true;
            _thread.Join(2000);
        }

        public void Dispose() => Stop();

        [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(nint window, byte[] info);
    }
}
