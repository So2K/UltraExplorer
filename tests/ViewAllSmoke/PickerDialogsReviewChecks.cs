using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the replacement of other programs' file
/// dialogs, away from the picker window itself, and what was done about it:
/// a file-type label whose own pattern comes before the real one (J156); a
/// replaced dialog's folder taken for another one, a name like a variable's
/// or one ending in a dot (J154); the watchdog after its sessions folder is
/// deleted (J160); a name box read or written while its application is busy
/// for a moment (J153); a dialog with more file types than the picker
/// mirrors (J067); and the answer handed back without walking the dialog's
/// controls again (J155).
///
/// <para>Real windows only where nothing else will do: a message-only edit on
/// a thread of its own, a watchdog of a state folder of its own, and the
/// fixture's own Windows dialog in a process of its own, on the secondary
/// monitor and never activated.</para>
/// </summary>
internal static partial class Program
{
    private static Task PickerDialogsReviewChecks()
    {
        PickerReviewFilterLabelChecks();
        PickerReviewStartFolderChecks();
        PickerReviewBusyEditChecks();
        PickerReviewGuardianFolderChecks();
        PickerReviewNativeDialogChecks();
        return Task.CompletedTask;
    }

    // ---- J156: the last pattern in a type's label is its own ----------------------------

    private static void PickerReviewFilterLabelChecks()
    {
        Section("picker review: a file-type label with its own pattern before the real one (J156)");
        var jpeg = NativeDialogRules.ReadFilter("JPEG (*.jpg) (*.jpg;*.jpeg;*.jpe)");
        Check($"the pattern Windows appended is the one read, not the label's own ({jpeg?.Pattern})",
            jpeg is { Pattern: "*.jpg;*.jpeg;*.jpe" });
        Check("so a .jpeg file is shown", FileDialogFilter.Parse(jpeg?.Pattern).Matches("photo.jpeg"));
        var bitmap = NativeDialogRules.ReadFilter("Bitmap (*.bmp) (*.bmp;*.dib)");
        Check($"and the same for a bitmap ({bitmap?.Pattern})", bitmap is { Pattern: "*.bmp;*.dib" });
        Check("a label with one pattern is read as before",
            NativeDialogRules.ReadFilter("Images (*.png;*.jpg)") is { Pattern: "*.png;*.jpg" }
            && NativeDialogRules.ReadFilter("Text (*.txt)") is { Pattern: "*.txt" });
        Check("a group in parentheses that is not a pattern is passed over",
            NativeDialogRules.ReadFilter("Text (*.txt) (UTF-8)") is { Pattern: "*.txt" });
        Check("a label with no pattern at all still leaves the dialog with Windows",
            NativeDialogRules.ReadFilter("Makefiles (*.mk Makefile)") is null && NativeDialogRules.ReadFilter("Photoshop image") is null);
    }

    // ---- J154: the folder a replaced dialog opens at ------------------------------------

    private static void PickerReviewStartFolderChecks()
    {
        Section("picker review: the folder a replaced dialog opens at (J154)");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerReview", Guid.NewGuid().ToString("N"));
        try
        {
            // A folder whose name is spelt like a variable: the dialog shows it
            // as it is, and the variable it looks like names something else.
            var literal = Path.Combine(root, "x", "%OS%");
            Directory.CreateDirectory(literal);
            var native = new FileDialogRequest { IsNativeProxy = true, Mode = FileDialogMode.Save, InitialFolder = literal };
            native.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            var session = new FileDialogSession(native);
            Check($"a replaced dialog in a folder named %OS% opens there, not in the folder above it ({session.CurrentFolder})",
                string.Equals(session.CurrentFolder, literal, StringComparison.OrdinalIgnoreCase));
            session.FileNameText = "letter.txt";
            var save = session.Prepare([]);
            Check($"and saves there ({string.Join(" | ", save.Paths)})",
                save.Kind == FileDialogActionKind.Accept && save.Paths.Count == 1
                && string.Equals(save.Paths[0], Path.Combine(literal, "letter.txt"), StringComparison.OrdinalIgnoreCase));

            // A program's own dialog may still name a folder by a variable.
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_REVIEW_FOLDER", root);
            try
            {
                var own = new FileDialogRequest { Mode = FileDialogMode.Open, InitialFolder = @"%ULTRAEXPLORER_REVIEW_FOLDER%\x" };
                Check("a folder named by a variable in UltraExplorer's own dialog still opens where the variable leads",
                    string.Equals(new FileDialogSession(own).CurrentFolder, Path.Combine(root, "x"), StringComparison.OrdinalIgnoreCase));
            }
            finally { Environment.SetEnvironmentVariable("ULTRAEXPLORER_REVIEW_FOLDER", null); }

            // "pair." beside "pair": what the dialog's address shows is "pair.",
            // which Windows would read as its neighbour "pair" - the folder the
            // picker opened at and the one a typed name went to.
            var neighbour = Path.Combine(root, "pair");
            var dotted = neighbour + ".";
            Directory.CreateDirectory(neighbour);
            Directory.CreateDirectory(@"\\?\" + dotted);
            Check("a folder named \"pair.\" stands beside \"pair\"", Directory.Exists(@"\\?\" + dotted) && Directory.Exists(neighbour));
            var address = "Address: " + dotted;
            Check($"the full read of a dialog showing it does not take it for its neighbour ({NativeDialogRules.ReadFolder(address) ?? "none: it stays with Windows"})",
                NativeDialogRules.ReadFolder(address) is null);
            Check($"nor does the quick read at recognition ({FastDialogRead.ResolveFolder(address, null, verify: false) ?? "none"})",
                FastDialogRead.ResolveFolder(address, null, verify: false) is null);
            // Inside such a folder: "pair.\inner", which Windows reads as "pair\inner".
            Directory.CreateDirectory(Path.Combine(neighbour, "inner"));
            Directory.CreateDirectory(@"\\?\" + Path.Combine(dotted, "inner"));
            var inside = "Address: " + Path.Combine(dotted, "inner");
            Check("nor a folder inside it", NativeDialogRules.ReadFolder(inside) is null
                && FastDialogRead.ResolveFolder(inside, null, verify: false) is null);
            // Names with dots and spaces inside them are what Windows reads them as.
            var spaced = Path.Combine(root, "notes v1.2 x", "inner");
            Directory.CreateDirectory(spaced);
            Check("an ordinary folder is read as before, one with spaces in its names too",
                NativeDialogRules.ReadFolder("Address: " + neighbour) == neighbour
                && FastDialogRead.ResolveFolder("Address: " + neighbour, null, verify: false) == neighbour
                && NativeDialogRules.ReadFolder("Address: " + spaced) == spaced);
        }
        finally
        {
            foreach (var name in new[] { @"pair.\inner", "pair." })
            {
                try { Directory.Delete(@"\\?\" + Path.Combine(root, name)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            TryDelete(root);
        }
    }

    // ---- J153: a name box whose application is busy for a moment ------------------------

    /// <summary>
    /// The application's name box, read and written while its thread is
    /// busy for a moment - a collection, a slow handler - as when the answer
    /// is handed back: the name is read and written once the thread answers,
    /// and a thread that does not answer at all is still given up on soon.
    /// </summary>
    private static void PickerReviewBusyEditChecks()
    {
        Section("picker review: a name box whose application is busy for a moment (J153)");
        using var box = new BusyEdit("busy.txt");
        Check("a message-only edit on a thread of its own stands in for the dialog's name box", box.Handle != 0);
        if (box.Handle == 0) return;

        box.HoldFor(450);
        var clock = Stopwatch.StartNew();
        var read = Attempt(() => DialogNative.ReadEdit(box.Handle), out var name);
        Check($"a name box whose thread is busy for 450 ms is read once it answers ({read?.GetType().Name ?? name}, {clock.ElapsedMilliseconds} ms)",
            read is null && name == "busy.txt");

        box.HoldFor(450);
        clock.Restart();
        var written = Attempt(() => { DialogNative.SetEdit(box.Handle, "answer.txt"); return ""; }, out _);
        Check($"and written once it answers ({written?.GetType().Name ?? "written"}, {clock.ElapsedMilliseconds} ms)",
            written is null && DialogNative.ReadEdit(box.Handle) == "answer.txt");

        box.HoldFor(4000);
        clock.Restart();
        var hung = Attempt(() => DialogNative.ReadEdit(box.Handle), out _);
        Check($"a thread that does not answer is given up on within about a second ({hung?.GetType().Name}, {clock.ElapsedMilliseconds} ms)",
            hung is IOException && clock.ElapsedMilliseconds < 2000);
        box.WaitIdle();
    }

    private static Exception? Attempt<T>(Func<T> action, out T? value)
    {
        value = default;
        try { value = action(); return null; }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { return ex; }
    }

    /// <summary>A message-only edit whose thread can be held, as an application's is while it is busy.</summary>
    private sealed class BusyEdit : IDisposable
    {
        private const uint Hold = 0x8000 + 0x153; // WM_APP + 0x153
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _holding = new(), _idle = new(true);
        private uint _threadId;
        public nint Handle { get; private set; }

        public BusyEdit(string text)
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                Handle = CreateWindowEx(0, "EDIT", text, 0x80, 0, 0, 0, 0, -3, 0, 0, 0); // ES_AUTOHSCROLL, message-only
                ready.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    if (message.Id == Hold)
                    {
                        _holding.Set();
                        Thread.Sleep((int)message.W);
                        _idle.Set();
                        continue;
                    }

                    DispatchMessage(ref message);
                }

                if (Handle != 0) DestroyWindow(Handle);
            }) { IsBackground = true, Name = "UltraExplorer busy name box" };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        /// <summary>Returns once the thread has stopped answering, for <paramref name="milliseconds"/>.</summary>
        public void HoldFor(int milliseconds)
        {
            WaitIdle();
            _holding.Reset();
            _idle.Reset();
            PostThreadMessage(_threadId, Hold, milliseconds, 0);
            _holding.Wait(TimeSpan.FromSeconds(5));
        }

        public void WaitIdle() => _idle.Wait(TimeSpan.FromSeconds(10));

        public void Dispose()
        {
            if (!_thread.IsAlive) return;
            WaitIdle();
            PostThreadMessage(_threadId, 0x12, 0, 0); // WM_QUIT: the thread destroys its window
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    // ---- J160: the watchdog after its sessions folder is deleted ------------------------

    /// <summary>
    /// The folder the watchdog looks in for leases, deleted while it runs -
    /// by the user, by a cleaner, by deleting the state folder's contents:
    /// the watchdog goes on looking, finds no lease there, and the mode stays
    /// on. It used to end, and pause dialog and folder replacement with it.
    /// The watchdog is a test copy with a state folder of its own.
    /// </summary>
    private static void PickerReviewGuardianFolderChecks()
    {
        Section("picker review: the dialog watchdog after its sessions folder is deleted (J160)");
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (needs ULTRAEXPLORER_STATE_DIR: skipped)");
            return;
        }

        if (!File.Exists(DialogSelfProcess.ExecutablePath))
        {
            Console.WriteLine($"  ({DialogSelfProcess.ExecutablePath} is not beside the checks: skipped)");
            return;
        }

        var state = Path.Combine(AppPaths.StateDirectory, "review-guardian-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":true}");
        var mutex = @"Local\UltraExplorer.DialogGuardian." + InstanceKeyFor(state);
        var integration = Path.Combine(state, "dialog-integration");
        Process? guardian = null;
        try
        {
            guardian = StartTestCopy(state, ["--dialog-guardian"]);
            var started = WaitFor(() => MutexExists(mutex) && Directory.Exists(Path.Combine(integration, "sessions")), 10000);
            Check("the watchdog starts and makes its sessions folder", started);
            if (!started) return;

            var deleted = WaitFor(() =>
            {
                try { Directory.Delete(integration, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return !Directory.Exists(integration);
            }, 3000);
            Check("its sessions folder is deleted while it runs", deleted);
            Thread.Sleep(1500);
            Check($"the watchdog goes on{(guardian.HasExited ? $" (it ended with {guardian.ExitCode})" : "")}", !guardian.HasExited && MutexExists(mutex));
            Check("and the mode stays on", ReadEnabled(state));

            // A lease made after that finds its folder again, as any other.
            Directory.CreateDirectory(Path.Combine(integration, "sessions"));
            Thread.Sleep(300);
            Check("the watchdog is still there once the folder is made again", !guardian.HasExited);
        }
        finally
        {
            File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":false}");
            if (guardian is not null)
            {
                try { if (!guardian.WaitForExit(5000)) { guardian.Kill(); guardian.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                guardian.Dispose();
            }

            TryDelete(state);
        }
    }

    // ---- J067 and J155: the fixture's own Windows dialog --------------------------------

    private static void PickerReviewNativeDialogChecks()
    {
        Section("picker review: a Windows dialog with more file types than the picker mirrors (J067)");
        if (TestScreen.Target() is null)
        {
            Console.WriteLine("  (no secondary monitor for the fixture's dialog: skipped)");
            return;
        }

        using (var fixture = DialogFixtureProcess.Start("open"))
        {
            Check($"the fixture's Windows dialog opens on a secondary monitor ({fixture.Problem})",
                fixture.Dialog != 0 && TestScreen.OnSecondary(fixture.Dialog));
            if (fixture.Dialog != 0) TooManyTypesChecks(fixture);
        }

        Section("picker review: an answer handed back to a Windows dialog (J155)");
        using (var fixture = DialogFixtureProcess.Start("save"))
        {
            Check($"the fixture's Windows Save dialog opens on a secondary monitor ({fixture.Problem})",
                fixture.Dialog != 0 && TestScreen.OnSecondary(fixture.Dialog));
            if (fixture.Dialog != 0) SubmitChecks(fixture);
        }
    }

    /// <summary>
    /// Notepad++ and LibreOffice offer some ninety types. The dialog stays with
    /// Windows, as it must, but at once: no second of reading the list again
    /// and again, and its list is never dropped down to count them.
    /// </summary>
    private static void TooManyTypesChecks(DialogFixtureProcess fixture)
    {
        var types = DialogNative.FindChild(fixture.Dialog, 1136, "ComboBox");
        for (var index = 0; types != 0 && index < 67; index++) SendText(types, 0x143, 0, $"Extra type {index} (*.x{index})"); // CB_ADDSTRING
        var count = types == 0 ? 0 : (long)SendText(types, 0x146, 0, null); // CB_GETCOUNT
        Check($"the dialog's type list holds more than 64 types ({count})", count > 64);
        if (count <= 64) return;

        var dropped = 0;
        using var stop = new ManualResetEventSlim();
        var watch = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                if (SendMessageTimeout(types, 0x157, 0, 0, 2, 100, out var state) != 0 && state != 0) Interlocked.Exchange(ref dropped, 1); // CB_GETDROPPEDSTATE
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "type list drop-down watch" };
        watch.Start();
        using var thread = new AutomationThread();
        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        try { thread.Run(() => { using var automation = new NativeDialogAutomation(); automation.Capture(fixture.Dialog); }).Wait(TimeSpan.FromSeconds(20)); }
        catch (AggregateException ex) { failure = ex.InnerException; }
        var took = clock.ElapsedMilliseconds;
        stop.Set();
        watch.Join(2000);
        Console.WriteLine($"  read gave up after {took} ms: {failure?.GetType().Name}: {failure?.Message}");
        Check("the dialog stays with Windows", failure is NotSupportedException);
        Check($"it is given up on at once, not after reading the list again and again ({took} ms)", took < 600);
        Check("its type list is never dropped down", Volatile.Read(ref dropped) == 0);
        Check("the dialog is still there for its user, its list closed",
            DialogNative.IsWindow(fixture.Dialog) && SendText(types, 0x157, 0, null) == 0);
    }

    /// <summary>
    /// The answer goes to the dialog, which is back on screen by then: its
    /// file type, its name and its OK, and nothing else - its controls are
    /// not walked again first (UI Automation over the whole dialog, while the
    /// user looks at it, and one more way for the answer to be lost).
    /// </summary>
    private static void SubmitChecks(DialogFixtureProcess fixture)
    {
        using var thread = new AutomationThread();
        var automation = thread.Run(() => new NativeDialogAutomation()).GetAwaiter().GetResult();
        try
        {
            Exception? unread = null;
            try { thread.Run(() => automation.Capture(fixture.Dialog)).Wait(TimeSpan.FromSeconds(20)); }
            catch (AggregateException ex) { unread = ex.InnerException; }
            var chrome = typeof(NativeDialogAutomation).GetField("_chrome", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var before = chrome.GetValue(automation);
            Check($"the fixture's Save dialog is read{(unread is null ? "" : $" ({unread.GetType().Name}: {unread.Message})")}", unread is null && before is not null);
            if (unread is not null || before is null) return;

            var answer = Path.Combine(fixture.Root, "answer.txt");
            var chosen = new FileDialogResult(true, [answer], 1);
            // While the dialog is still hidden under the picker, its controls
            // are looked at once more, for fields its type may have added.
            var clock = Stopwatch.StartNew();
            var prepared = thread.Run(() => automation.PrepareSubmit(chosen)).Wait(TimeSpan.FromSeconds(10));
            var looked = clock.ElapsedMilliseconds;
            before = chrome.GetValue(automation);
            Check($"the dialog's controls are looked at while it is still hidden ({looked} ms)", prepared && before is not null);
            clock.Restart();
            var submitted = thread.Run(() => automation.Submit(chosen)).Wait(TimeSpan.FromSeconds(10));
            var took = clock.ElapsedMilliseconds;
            Console.WriteLine($"  the answer was handed over in {took} ms once the dialog was back");
            Check("the answer is handed to the dialog", submitted);
            Check("without walking the dialog's controls again", ReferenceEquals(before, chrome.GetValue(automation)));
            var result = fixture.WaitForResult(5000);
            Check($"and the program receives exactly that file ({result})", result.Contains("answer.txt", StringComparison.OrdinalIgnoreCase)
                && result.Contains("\"result\":0", StringComparison.Ordinal));
        }
        finally
        {
            thread.Run(() => automation.Dispose()).Wait(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// This program as the fixture's caller: its Windows dialog shown in a
    /// process of its own (<see cref="NativeDialogFixture"/>), owned by an
    /// invisible window on the secondary monitor and refusing activation.
    /// Disposed, a dialog still open is cancelled and the process ended.
    /// </summary>
    private sealed class DialogFixtureProcess : IDisposable
    {
        private readonly Process? _process;
        public nint Dialog { get; }
        public string Root { get; }
        public string Output { get; }
        public string Problem { get; } = "shown";
        public int ProcessId => _process?.Id ?? 0;

        private DialogFixtureProcess(string mode)
        {
            Root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerReviewDialog", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "sample.txt"), "fixture");
            Output = Path.Combine(Root, "result.json");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--native-dialog-fixture", Root, Output, mode }) start.ArgumentList.Add(argument);
            start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
            _process = Process.Start(start);
            if (_process is null) { Problem = "the fixture could not be started"; return; }
            nint found = 0;
            var shown = WaitFor(() =>
            {
                DialogNative.EnumWindows((window, _) =>
                {
                    if (DialogNative.ProcessId(window) == _process.Id && DialogNative.LooksLikeFileDialog(window)) found = window;
                    return found == 0;
                }, 0);
                return found != 0 || _process.HasExited;
            }, 15000);
            Dialog = shown && !_process.HasExited ? found : 0;
            if (Dialog == 0) Problem = _process.HasExited ? $"the fixture ended: {ReadQuietly(Output)}" : "no dialog appeared";
        }

        public static DialogFixtureProcess Start(string mode) => new(mode);

        /// <summary>What the fixture wrote once its dialog returned, or what it has so far.</summary>
        public string WaitForResult(int milliseconds)
        {
            WaitFor(() => _process is null || _process.HasExited, milliseconds);
            return ReadQuietly(Output);
        }

        private static string ReadQuietly(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
            catch (IOException) { return string.Empty; }
        }

        public void Dispose()
        {
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited && Dialog != 0 && DialogNative.IsWindow(Dialog))
                    {
                        try { DialogNative.Click(Dialog, 2); }
                        catch (System.ComponentModel.Win32Exception) { }
                    }

                    if (!_process.WaitForExit(5000)) { _process.Kill(); _process.WaitForExit(3000); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

                _process.Dispose();
            }

            TryDelete(Root);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern nint SendText(nint window, uint message, nint wparam, string? text);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
}
