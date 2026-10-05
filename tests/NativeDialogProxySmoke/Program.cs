using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using UltraExplorer.Picker.Integration;
using ViewAllSmoke;

namespace NativeDialogProxySmoke;

/// <summary>Drives real Windows Open/Save dialogs, shown by a separate fixture
/// process, through a test copy of UltraExplorer's dialog replacement.
/// Everything it opens belongs on a secondary monitor and must never take the
/// user's foreground or keyboard: the fixture's dialog is owned by an invisible
/// window there and refuses activation, UltraExplorer's copies run with
/// ULTRAEXPLORER_TEST_WINDOW=1, and this runner never activates or focuses
/// anything. It checks all of that as it goes: every window's rectangle against
/// the monitors, and every foreground change while a case runs.</summary>
internal static class Program
{
    private static int _checks, _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--native-dialog-fixture"))
            return DialogProducers.Handles(args) ? DialogProducers.Run(args) : ViewAllSmoke.NativeDialogFixture.Run(args);
        if (args.Length < 2) { Console.WriteLine("NativeDialogProxySmoke <UltraExplorer.exe> <output-folder> [case[,case...]]"); return 2; }
        // Window and monitor rectangles in physical pixels, both the same way.
        SetProcessDpiAwarenessContext(-4);
        var app = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var stateRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("NATIVE_DIALOG_SMOKE_STATE_ROOT") is { Length: > 0 } root
            ? root : Path.GetDirectoryName(output) ?? output);
        var realState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        if (stateRoot.StartsWith(realState, StringComparison.OrdinalIgnoreCase) || output.StartsWith(realState, StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("Refusing to run: the test state would be inside the user's " + realState); return 2; }
        // Anything in this process that asks for the state folder gets a scratch one.
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(stateRoot, "state-runner"));

        foreach (var monitor in TestScreen.All()) Console.WriteLine($"monitor {monitor.Bounds} work {monitor.Work}{(monitor.Primary ? " PRIMARY" : "")}");
        if (TestScreen.Target() is null && Environment.GetEnvironmentVariable("NATIVE_DIALOG_SMOKE_ALLOW_PRIMARY") != "1")
        { Console.WriteLine("Refusing to run: there is no secondary monitor, and test windows may not open on the primary one."); return 3; }

        var cases = args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["open", "open-fresh", "open-large", "open-profile", "multi", "save", "save-fresh", "save-repeat", "save-sequence", "save-recycle", "save-downloads", "save-custom", "save-hidden-options", "save-extension", "open-nofilter", "save-nofilter", "folder", "cancel", "fallback", "exclude", "unsupported", "crash", "guard-crash", "stall", "overwrite", "early-cancel", "early-fallback", "early-off", "read-timeout", "idle-worker",
                "early-worker-loss", "early-guard-loss", .. Producers.Keys];
        // One set of test windows on the secondary monitor at a time: another
        // team's bench measures pixels there. Held for the whole run, its
        // windows and their cleanup; the fixture this starts never takes it.
        using var bench = new Mutex(false, @"Local\UltraExplorer.NativeBench.Secondary");
        if (!TakeBench(bench)) { Console.WriteLine("Refusing to run: another test run has held the secondary monitor for too long."); return 4; }
        try
        {
            foreach (var mode in cases)
            {
                try { RunCase(app, output, stateRoot, mode); }
                catch (Exception ex) { _checks++; _failures++; Console.WriteLine($"  FAIL  {mode}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        finally { bench.ReleaseMutex(); }
        ReportTimingSummary();
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures > 0 ? 1 : 0;
    }

    /// <summary>Waits for the secondary monitor's bench lock (NATIVE_DIALOG_SMOKE_BENCH_WAIT_MINUTES, 30 by default).</summary>
    private static bool TakeBench(Mutex bench)
    {
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("NATIVE_DIALOG_SMOKE_BENCH_WAIT_MINUTES"), out var asked) && asked >= 0 ? asked : 30;
        try
        {
            if (bench.WaitOne(0)) return true;
            Console.WriteLine($"waiting for the secondary monitor: another test run holds it (at most {minutes} min)");
            return bench.WaitOne(TimeSpan.FromMinutes(minutes));
        }
        catch (AbandonedMutexException)
        {
            Console.WriteLine("the previous holder of the secondary monitor ended without releasing it");
            return true;
        }
    }

    private static void Check(string name, bool condition)
    { _checks++; if (!condition) _failures++; Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + name); }

    private static void RunCase(string app, string output, string stateRoot, string mode)
    {
        Console.WriteLine("== " + mode + " ==");
        // Production never activates anything in a test copy
        // (DialogNative.MayActivate); the foreground watch below proves it.
        var self = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(output, mode + "-" + id);
        var state = Path.Combine(stateRoot, "state-" + mode + "-" + id);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":true}");
        File.WriteAllText(Path.Combine(root, "sample.txt"), "fixture");
        File.WriteAllText(Path.Combine(root, "second.txt"), "fixture");
        Directory.CreateDirectory(Path.Combine(root, "subfolder"));
        // A folder of thousands of files: the dialog's own list must not slow
        // reading it past the replacement's time limit.
        if (mode == "open-large")
            for (var index = 0; index < 4000; index++) File.WriteAllText(Path.Combine(root, $"bulk-{index:D4}.txt"), "");
        // A dialog shown the way a real program shows it (see Producers).
        var spec = Producers.GetValueOrDefault(mode);
        foreach (var file in spec?.Files ?? [])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
            File.WriteAllText(Path.Combine(root, file), "fixture");
        }
        foreach (var folder in SequenceFolders)
            if (mode == "save-sequence") Directory.CreateDirectory(Path.Combine(root, folder));
        var resultPath = Path.Combine(root, "result.json");
        // "-fresh": the dialog appears only once the prepared worker is ready
        // and has settled - as dialogs appear in real use - and is timed from
        // its appearance. "open-profile" opens in the user's own folder and is
        // cancelled in the picker. "idle-worker" never shows a dialog at all.
        // "-nofilter": the same flow, the caller's dialog without any file-type list.
        var noFilter = mode.EndsWith("-nofilter", StringComparison.Ordinal);
        if (noFilter) mode = mode[..^"-nofilter".Length];
        var fresh = mode.EndsWith("-fresh", StringComparison.Ordinal);
        var idleWorker = mode == "idle-worker";
        var flow = spec is not null ? (spec.StaysNative ? "stays-native" : "producer")
            : fresh ? mode[..^"-fresh".Length] : mode == "open-profile" ? "cancel" : mode == "save-recycle" ? "save-repeat" : mode;
        var pendingCaptureCase = flow is "early-cancel" or "early-fallback" or "early-off" or "early-worker-loss" or "early-guard-loss" or "read-timeout";
        var fixtureMode = mode == "open-profile" ? mode
            : flow is "save-custom" or "save-hidden-options" or "save-repeat" or "save-sequence" or "save-extension" or "save-downloads" or "save" or "folder" or "multi" or "unsupported" ? flow
            : pendingCaptureCase ? "save-custom" : flow == "overwrite" ? "save" : "open";
        if (spec is not null) fixtureMode = mode;
        if (noFilter) fixtureMode += "-nofilter";
        using var watch = new ForegroundWatch([app, self]);
        using var showSignal = fresh || idleWorker ? new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\UltraExplorer.NativeDialogSmoke." + id) : null;
        using var fixture = Start(self, ["--native-dialog-fixture", root, resultPath, fixtureMode], state,
            showSignal is null ? null : new() { ["NATIVE_DIALOG_FIXTURE_SHOW_SIGNAL"] = @"Local\UltraExplorer.NativeDialogSmoke." + id });
        watch.Own(fixture.Id);
        // When each of the fixture's dialogs appeared, for the timings.
        using var shown = new ShowWatch((uint)fixture.Id);
        Process? agent = null;
        // When the listener was started: a dialog that appears after it is one
        // it was waiting for, as in real use; one before, already open.
        long listening = 0;
        nint original = 0;
        using var thread = new AutomationThread();
        UIA3Automation? automation = null;
        DialogLeaseRecord? lease = null;
        var passed = false;
        try
        {
            var environment = new Dictionary<string, string> { ["ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS"] = fixture.Id.ToString(),
                ["ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DIR"] = root };
            // The user's own folder by name is the prepared worker's (a per-dialog
            // process knows the common known folders only): always served by it.
            if (flow is "save-repeat" or "save-sequence" || pendingCaptureCase || fresh || idleWorker || mode == "open-profile"
                || Environment.GetEnvironmentVariable("NATIVE_DIALOG_SMOKE_PREWARM") == "1")
                environment["ULTRAEXPLORER_DIALOG_TEST_WAIT_FOR_WARM"] = "1";
            if (flow == "stall") environment["ULTRAEXPLORER_DIALOG_TEST_STALL_ON_PRESENT"] = "1";
            if (mode == "save-recycle") environment["ULTRAEXPLORER_DIALOG_TEST_RECYCLE_AFTER"] = "1";
            // Measured as it runs for the user: no test picture of it either
            // (and none at all for a timing run that asks for none).
            if (idleWorker || Environment.GetEnvironmentVariable("NATIVE_DIALOG_SMOKE_NO_CAPTURE") == "1")
                environment.Remove("ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DIR");
            if (pendingCaptureCase)
                environment["ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DELAY_MS"] = flow == "read-timeout" ? "10000" : "1200";
            if (showSignal is not null)
            {
                // The worker first, ready and settled; the dialog after.
                agent = Start(app, ["--dialog-agent"], state, environment);
                listening = Stopwatch.GetTimestamp();
                watch.Own(agent.Id);
                var agentLog = Path.Combine(state, "dialog-integration", "integration.log");
                Wait(() => ReadLog(agentLog).Contains("Prepared dialog worker is ready.", StringComparison.Ordinal), 20000, "the prepared worker");
                if (idleWorker)
                {
                    RunIdleWorkerCase(agent);
                    passed = true;
                    return;
                }
                Thread.Sleep(2000);
                showSignal.Set();
            }
            Wait(() =>
            {
                DialogNative.EnumWindows((window, _) =>
                {
                    // A dialog the integration never takes for a file dialog
                    // (SHBrowseForFolder's tree) is found as the dialog it is.
                    if (DialogNative.ProcessId(window) == fixture.Id && (spec is { StaysNative: true }
                            ? DialogNative.ClassName(window) == "#32770" && DialogNative.IsWindowVisible(window)
                            : DialogNative.LooksLikeFileDialog(window))) original = window;
                    return true;
                }, 0);
                return original != 0;
            }, 10000, "the native dialog");
            PrintFile(flow == "save-repeat" ? resultPath + ".first.json.fixture.log" : flow == "save-sequence" ? resultPath + ".1.json.fixture.log"
                : resultPath + ".fixture.log", "fixture");
            Console.WriteLine($"  native dialog {original:X} {TestScreen.Describe(original)}");
            Check("the native dialog opens on a secondary monitor", TestScreen.OnSecondary(original));
            using var earlyVisibility = pendingCaptureCase ? new EarlyVisibilityWatch(original, (uint)fixture.Id,
                Path.Combine(state, "dialog-integration", "sessions")) : null;
            if (agent is null)
            {
                agent = Start(app, ["--dialog-agent"], state, environment);
                listening = Stopwatch.GetTimestamp();
                watch.Own(agent.Id);
            }
            var sessions = Path.Combine(state, "dialog-integration", "sessions");
            if (pendingCaptureCase)
            {
                RunPendingCaptureCase(mode, state, resultPath, fixture, agent, original, watch, thread, earlyVisibility!, ref automation, ref lease);
                passed = true;
                return;
            }
            if (flow == "stays-native")
            {
                RunStaysNativeCase(spec!, state, resultPath, original);
                passed = true;
                return;
            }
            if (flow == "unsupported")
            {
                Thread.Sleep(3500);
                Check("unsupported export controls leave the original dialog visible", !DialogNative.IsHidden(original) && DialogNative.IsWindow(original));
                Check("the application's dialog was not accepted", !File.Exists(resultPath));
                Check("the native dialog stays on a secondary monitor", TestScreen.OnSecondary(original));
                passed = true;
                return;
            }
            Wait(() =>
            {
                if (!Directory.Exists(sessions)) return false;
                foreach (var file in Directory.EnumerateFiles(sessions, "*.json"))
                    if (DialogLease.ReadRecord(file) is { ProxyWindow: not 0 } record && record.NativeProcess == fixture.Id) lease = record;
                return lease is not null && DialogNative.IsHidden(original);
            }, 12000, "the protected proxy");
            watch.Own(lease!.WorkerProcess);
            Check("the original window is cloaked only with a recovery marker", DialogLease.BelongsToWindow(lease));
            File.WriteAllText(Path.Combine(root, "lease.json"), JsonSerializer.Serialize(lease));
            var proxy = (nint)lease.ProxyWindow;
            Thread.Sleep(300);
            Console.WriteLine($"  proxy window {proxy:X} {TestScreen.Describe(proxy)}");
            Check("the proxy opens on a secondary monitor", TestScreen.OnSecondary(proxy));

            if (flow is "crash" or "guard-crash" or "stall")
            {
                if (flow == "crash") { using var worker = Process.GetProcessById(lease.WorkerProcess); worker.Kill(); }
                if (flow == "guard-crash")
                {
                    var ready = Path.Combine(sessions, lease.Token + ".json.ready");
                    using var guardian = ExactGuardianProcess(ready);
                    watch.Own(guardian.Id);
                    guardian.Kill();
                }
                Wait(() => !DialogNative.IsHidden(original), 10000, "automatic recovery");
                Check("the original application remains alive and its chooser is available", !fixture.HasExited && DialogNative.IsWindow(original) && DialogNative.IsWindowEnabled(original));
                Wait(() => !JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json"))).RootElement.GetProperty("Enabled").GetBoolean(), 3000, "integration paused");
                Check("a failed integration is paused", true);
                passed = true;
                return;
            }

            var window = thread.Run(() =>
            {
                automation = new UIA3Automation { ConnectionTimeout = TimeSpan.FromSeconds(4), TransactionTimeout = TimeSpan.FromSeconds(4) };
                return automation.FromHandle(proxy);
            }).GetAwaiter().GetResult();
            if (flow == "save-repeat")
            {
                RunRepeatSave(root, state, resultPath, sessions, fixture, watch, thread, automation!, window, ref original, ref lease,
                    recycled: mode == "save-recycle");
                passed = true;
                return;
            }
            if (flow == "save-sequence")
            {
                RunSaveSequence(root, state, resultPath, sessions, fixture, watch, thread, automation!, window, ref original, ref lease);
                passed = true;
                return;
            }
            if (flow == "producer")
            {
                RunProducerCase(spec!, root, state, resultPath, fixture, original, thread, automation!, window);
                passed = true;
                return;
            }
            if (flow is "fallback" or "exclude")
            {
                var button = flow == "fallback" ? "UseNativeDialog" : "ExcludeNativeDialogApp";
                thread.Run(() => Required(window.FindFirstDescendant(button)).AsButton().Invoke()).GetAwaiter().GetResult();
                Wait(() => !DialogNative.IsHidden(original), 5000, "return to Windows");
                Check("returning to Windows preserves the outstanding native request", !fixture.HasExited && !File.Exists(resultPath));
                if (flow == "exclude")
                {
                    using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json")));
                    Check("the application exception was saved", settings.RootElement.GetProperty("ExcludedApplications").GetArrayLength() == 1);
                }
                passed = true;
                return;
            }
            if (flow == "cancel")
            {
                thread.Run(() => Required(window.FindFirstDescendant("PickerCancelButton")).AsButton().Invoke()).GetAwaiter().GetResult();
            }
            else
            {
                if (flow == "save-hidden-options")
                {
                    Check("an app-hidden checkbox is absent from the proxy", thread.Run(() =>
                        Required(window.FindFirstDescendant("NativeDialogOptions")).FindFirstDescendant(cf =>
                            cf.ByName("Keep layers").And(cf.ByControlType(ControlType.CheckBox))) is null).GetAwaiter().GetResult());
                    thread.Run(() =>
                    {
                        var types = Required(window.FindFirstDescendant("PickerTypeBox")).AsComboBox();
                        types.Expand(); types.Select(1); types.Collapse();
                    }).GetAwaiter().GetResult();
                    Wait(() => thread.Run(() => Required(window.FindFirstDescendant("NativeDialogOptions"))
                        .FindFirstDescendant(cf => cf.ByName("Keep layers").And(cf.ByControlType(ControlType.CheckBox))) is not null)
                        .GetAwaiter().GetResult(), 6000, "the newly visible export checkbox");
                    Check("format change exposes the application's checkbox", true);
                    thread.Run(() => Required(Required(window.FindFirstDescendant("NativeDialogOptions"))
                        .FindFirstDescendant(cf => cf.ByName("Keep layers").And(cf.ByControlType(ControlType.CheckBox))))
                        .AsCheckBox().IsChecked = false).GetAwaiter().GetResult();
                }
                if (flow == "save-custom")
                    thread.Run(() =>
                    {
                        var controls = Required(window.FindFirstDescendant("NativeDialogOptions"));
                        Required(controls.FindFirstDescendant(cf => cf.ByName("Embed sRGB profile").And(cf.ByControlType(ControlType.CheckBox)))).AsCheckBox().IsChecked = true;
                        var combo = Required(controls.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)));
                        // Chosen through its item, without opening the list.
                        var items = combo.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                        if (items.Length > 1 && items[1].Patterns.SelectionItem.IsSupported) items[1].Patterns.SelectionItem.Pattern.Select();
                        else
                        {
                            Console.WriteLine("  note  the proxy's option list shows no items while closed; opening it to choose");
                            var list = combo.AsComboBox();
                            list.Select(1); list.Collapse();
                        }
                        Required(controls.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))).AsTextBox().Text = "Proxy author";
                        Required(controls.FindFirstDescendant(cf => cf.ByName("Flattened").And(cf.ByControlType(ControlType.RadioButton)))).Patterns.SelectionItem.Pattern.Select();
                    }).GetAwaiter().GetResult();
                var name = flow == "multi" ? $"\"{Path.Combine(root, "sample.txt")}\" \"{Path.Combine(root, "second.txt")}\""
                    : flow == "folder" ? Path.Combine(root, "subfolder")
                    : flow == "overwrite" ? Path.Combine(root, "sample.txt")
                    : flow.StartsWith("save", StringComparison.Ordinal) ? Path.Combine(root, "saved-result") : Path.Combine(root, "sample.txt");
                Thread.Sleep(700);
                thread.Run(() => Required(Required(window.FindFirstDescendant("PickerNameBox")).FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))).AsTextBox().Text = name).GetAwaiter().GetResult();
                Thread.Sleep(200);
                Console.WriteLine($"  proxy window before accepting {TestScreen.Describe(proxy)}");
                thread.Run(() => Required(window.FindFirstDescendant("PickerAcceptButton")).AsButton().Invoke()).GetAwaiter().GetResult();
                if (flow == "overwrite") AnswerOverwriteWarning(original, fixture, thread, automation!);
            }
            try { Wait(() => File.Exists(resultPath), 10000, "the native result"); }
            catch (TimeoutException)
            {
                DialogNative.EnumWindows((candidate, _) =>
                {
                    if (DialogNative.ProcessId(candidate) == fixture.Id)
                        Console.WriteLine($"  native window {candidate:X} class={DialogNative.ClassName(candidate)} visible={DialogNative.IsWindowVisible(candidate)} enabled={DialogNative.IsWindowEnabled(candidate)} hidden={DialogNative.IsHidden(candidate)} title={DialogNative.Title(candidate)}");
                    return true;
                }, 0);
                thread.Run(() =>
                {
                    var native = automation!.FromHandle(original);
                    foreach (var edit in native.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Take(5))
                        Console.WriteLine($"  native edit {edit.Properties.AutomationId.ValueOrDefault}: {edit.Patterns.Value.IsSupported} {edit.Patterns.Value.IsSupported switch { true => edit.Patterns.Value.Pattern.Value.Value, _ => "" }}");
                }).GetAwaiter().GetResult();
                throw;
            }
            using var result = JsonDocument.Parse(ReadResult(resultPath));
            var data = result.RootElement;
            if (flow == "cancel") Check("Cancel reaches the original native caller", data.GetProperty("result").GetInt32() == unchecked((int)0x800704C7));
            else
            {
                Check("the original native caller accepts the selected path", data.GetProperty("result").GetInt32() == 0);
                var paths = data.GetProperty("paths").EnumerateArray().Select(item => item.GetString()!).ToArray();
                Check("the exact number of selections reaches the application", paths.Length == (flow == "multi" ? 2 : 1));
                var expected = flow == "folder" ? Path.Combine(root, "subfolder") : flow.StartsWith("save") ? Path.Combine(root, "saved-result.txt") : Path.Combine(root, "sample.txt");
                if (flow == "save-extension") expected = Path.Combine(root, "saved-result.custom");
                if (flow == "save-downloads") expected = Path.Combine(root, "saved-result.html");
                if (flow == "save-hidden-options") expected = Path.Combine(root, "saved-result.png");
                // No types and so no default extension: the name comes back as typed, as with Windows.
                if (noFilter && flow.StartsWith("save")) expected = Path.Combine(root, "saved-result");
                Check("the application returns the intended file or folder", paths.Length > 0 && string.Equals(paths[0], expected, StringComparison.OrdinalIgnoreCase));
                if (paths.Length > 0 && !string.Equals(paths[0], expected, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"        expected {expected}, got {string.Join(" | ", paths)}");
                if (flow == "save-custom")
                {
                    var options = data.GetProperty("onAccept");
                    Check("sRGB checkbox reaches the application's acceptance callback", options.GetProperty("profile").GetBoolean());
                    Check("the export choice reaches the application's acceptance callback", options.GetProperty("quality").GetUInt32() == 2);
                    Check("the radio selection reaches the application", options.GetProperty("radio").GetUInt32() == 2);
                    Check("the text option reaches the application", options.GetProperty("author").GetString() == "Proxy author");
                }
            }
            if (mode == "open-profile")
            {
                // Its address names the folder without a path: the quick read
                // and the full one both know it, so it is replaced - never
                // shown early and then taken back.
                var text = ReadLog(Path.Combine(state, "dialog-integration", "integration.log"));
                Check("the user's own folder, named without a path, is served (the full read resolves what the quick read showed)",
                    !text.Contains("stays with Windows", StringComparison.Ordinal) && text.Contains("replacement presented and protected", StringComparison.Ordinal));
            }
            if (flow == "save-hidden-options")
            {
                Check("the changed file type reaches the native caller", data.GetProperty("typeIndex").GetInt32() == 2);
                Check("the newly visible checkbox value reaches native OnFileOk", !data.GetProperty("onAccept").GetProperty("layers").GetBoolean());
            }
            using (var finalSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json"))))
                Check("a completed native handoff leaves replacement enabled", finalSettings.RootElement.GetProperty("Enabled").GetBoolean());
            File.Copy(resultPath, Path.Combine(root, "verified-result.json"));
            passed = true;
        }
        finally
        {
            if (original == 0)
                DialogNative.EnumWindows((candidate, _) =>
                {
                    if (DialogNative.ProcessId(candidate) == fixture.Id)
                    {
                        Console.WriteLine($"  fixture window: {candidate:X} {DialogNative.ClassName(candidate)} visible {DialogNative.IsWindowVisible(candidate)} {TestScreen.Describe(candidate)} title {DialogNative.Title(candidate)}");
                        DialogNative.EnumChildWindows(candidate, (child, _) =>
                        {
                            if (DialogNative.ClassName(child) is "Edit" or "Button" or "SHELLDLL_DefView") Console.WriteLine($"  fixture child {DialogNative.ClassName(child)} id {DialogNative.GetDlgCtrlID(child)}");
                            return true;
                        }, 0);
                    }
                    return true;
                }, 0);
            Cleanup(() => File.WriteAllText(Path.Combine(state, "dialog-integration.json"), "{\"Enabled\":false}"));
            // Put the original back without activating it or anything it owns
            // (DialogLease.RestoreRecord would activate its prompts), then cancel it.
            Cleanup(() =>
            {
                if (lease is not null && DialogLease.BelongsToWindow(lease))
                    DialogNative.RestoreTransparency(original, lease.WasLayered, lease.OriginalColour, lease.OriginalAlpha, lease.OriginalTransparencyFlags);
                if (original != 0 && DialogNative.IsWindow(original)) { DialogNative.Restore(original, false); DialogNative.Click(original, 2); }
            });
            Cleanup(() => { if (!fixture.WaitForExit(2500)) { Console.WriteLine($"  cleanup: fixture {fixture.Id} did not close; ending it"); fixture.Kill(); } });
            var started = new List<(int Id, DateTime Start, string Image)>();
            if (agent is not null)
            {
                Cleanup(() => { if (!agent.WaitForExit(3000)) { Console.WriteLine($"  cleanup: agent {agent.Id} did not stop; ending it"); agent.Kill(); } });
                // The proxy and the guardian are the agent's children (or its
                // children's): only processes this run started are ended.
                Cleanup(() => started.AddRange(ProcessTree.Descendants(agent.Id, agent.StartTime)));
                agent.Dispose();
            }
            foreach (var (childId, childStart, image) in started)
                Cleanup(() =>
                {
                    using var child = Process.GetProcessById(childId);
                    if (child.StartTime != childStart || child.HasExited) return;
                    if (!child.WaitForExit(3000)) { Console.WriteLine($"  cleanup: {Path.GetFileName(image)} {childId} still running; ending it"); child.Kill(); }
                });
            if (automation is not null) Cleanup(() => thread.Run(automation.Dispose).Wait(3000));
            watch.Stop();
            var taken = watch.Report();
            Check("no window of the test took the foreground or the keyboard", taken == 0);
            if (lease is not null || pendingCaptureCase)
            {
                // The replacement is a picker over the user's own state: it may
                // read the workspace and marks, never write them back.
                var written = new[] { "workspace.json", "view-all.workspace.json", "folder-marks.json" }
                    .Select(name => Path.Combine(state, name)).Where(File.Exists)
                    .Concat(Directory.Exists(Path.Combine(state, "dialog-integration"))
                        ? Directory.EnumerateFiles(Path.Combine(state, "dialog-integration"), "proxy-*") : []).ToArray();
                foreach (var path in written) Console.WriteLine("  wrote " + Path.GetFileName(path));
                Check("the replacement wrote none of the canvas state (workspace, marks)", written.Length == 0);
            }
            var log = Path.Combine(state, "dialog-integration", "integration.log");
            if (File.Exists(log)) Cleanup(() => File.Copy(log, Path.Combine(root, "integration.log"), true));
            Cleanup(() => ReportTimings(log, mode, shown, listening));
            if (!passed) PrintFile(log, "integration.log");
            PrintFile(resultPath + ".fixture.log", "fixture", onlyActivations: true);
            Thread.Sleep(300);
        }
    }

    /// <summary>Input in the already-drawn provisional picker must not wait
    /// for its provider, and must never accept an unconfirmed native contract.</summary>
    private static void RunPendingCaptureCase(string mode, string state, string resultPath, Process fixture,
        Process agent, nint original, ForegroundWatch watch, AutomationThread thread, EarlyVisibilityWatch visibility,
        ref UIA3Automation? automation, ref DialogLeaseRecord? lease)
    {
        var driver = thread.Run(() => new UIA3Automation
            { ConnectionTimeout = TimeSpan.FromSeconds(2), TransactionTimeout = TimeSpan.FromSeconds(2) }).GetAwaiter().GetResult();
        automation = driver;
        nint proxy = 0;
        AutomationElement? window = null;
        Wait(() =>
        {
            var workers = ProcessTree.Descendants(agent.Id, agent.StartTime).Select(process => process.Id).ToHashSet();
            var candidates = new List<nint>();
            DialogNative.EnumWindows((candidate, _) =>
            {
                if (workers.Contains((int)DialogNative.ProcessId(candidate)) && DialogNative.IsWindowVisible(candidate)
                    && !DialogNative.IsHidden(candidate)) candidates.Add(candidate);
                return true;
            }, 0);
            foreach (var candidate in candidates)
            {
                var found = thread.Run(() =>
                {
                    try
                    {
                        var element = driver.FromHandle(candidate);
                        return element.Properties.AutomationId.ValueOrDefault == "UltraExplorerNativeDialogProxy" ? element : null;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
                }).GetAwaiter().GetResult();
                if (found is null) continue;
                proxy = candidate; window = found; return true;
            }
            return false;
        }, 12000, "the uncloaked provisional picker");
        var workerPid = (int)DialogNative.ProcessId(proxy);
        watch.Own(workerPid);
        using var worker = Process.GetProcessById(workerPid);
        Console.WriteLine($"  provisional proxy {proxy:X}, worker {workerPid}, {TestScreen.Describe(proxy)}");
        Check("the provisional proxy is genuinely on screen, not the cloaked idle window",
            DialogNative.IsWindowVisible(proxy) && !DialogNative.IsHidden(proxy));
        Check("the provisional proxy opens on a secondary monitor", TestScreen.OnSecondary(proxy));
        Check("the provisional proxy is above the original dialog it covers", IsAbove(proxy, original));
        Wait(() => visibility.Record is { ProxyWindow: not 0 }, 1000, "the provisional recovery lease");
        lease = visibility.Record;
        Check("the unfinished native request already has an exact recovery lease", lease is not null && DialogLease.BelongsToWindow(lease)
            && lease.NativeProcess == fixture.Id && lease.ProxyWindow == proxy.ToInt64());
        var transparency = DialogNative.Transparency(original);
        Check("the source is alpha-zero before provider completion", transparency.Layered && transparency.Alpha == 0 && (transparency.Flags & 2) != 0);
        WaitQuietly(() => visibility.FirstVisible != 0, 500);
        Console.WriteLine($"  visibility hidden={visibility.FirstHidden} proxyVisible={visibility.FirstVisible} hiddenProxySeen={visibility.SawHiddenProxy} opaqueSamples={visibility.OpaqueWhileProxyVisible}");
        Check("source alpha-zero is observed before the first visible UE sample", visibility.FirstHidden != 0
            && visibility.FirstVisible > visibility.FirstHidden && visibility.SawHiddenProxy);
        Check("no sampled UE-visible interval exposes the original", visibility.OpaqueWhileProxyVisible == 0);
        var pending = thread.Run(() => !Required(window!.FindFirstDescendant("PickerAcceptButton")).IsEnabled).GetAwaiter().GetResult();
        Check("OK is disabled while the native contract is still being read", pending);
        if (!pending) throw new InvalidOperationException("The provisional contract finished before the early-input test could act.");
        if (mode is "early-cancel" or "early-fallback" or "early-off" or "early-worker-loss" or "early-guard-loss" or "read-timeout")
        {
            // The application bringing its dialog forward over the provisional
            // picker: reorder only our two exact fixture windows, without
            // activation. The original must stay transparent even while above.
            SetWindowPos(proxy, original, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200); // no move, no size, no activation, not the owner
            var lowered = IsAbove(original, proxy);
            Check("the original can be reordered above UE without activation", lowered);
            Thread.Sleep(80);
            var reordered = DialogNative.Transparency(original);
            Check("reordering the original above UE does not reveal it", reordered.Layered && reordered.Alpha == 0
                && DialogLease.BelongsToWindow(lease!) && visibility.OpaqueWhileProxyVisible == 0);
        }
        Check("the source request remains alive and protected before its contract is confirmed",
            !fixture.HasExited && DialogNative.IsWindow(original) && DialogNative.IsHidden(original));

        if (mode != "early-cancel") visibility.Stop(); // Recovery can restore before UE closes.

        var response = Stopwatch.StartNew();
        if (mode == "early-cancel")
        {
            visibility.BeginCancel();
            thread.Run(() => Required(window!.FindFirstDescendant("PickerCancelButton")).AsButton().Invoke()).GetAwaiter().GetResult();
        }
        else if (mode == "early-fallback")
            thread.Run(() => Required(window!.FindFirstDescendant("UseNativeDialog")).AsButton().Invoke()).GetAwaiter().GetResult();
        else if (mode == "early-off")
        {
            var settings = Path.Combine(state, "dialog-integration.json");
            var temporary = settings + ".test-tmp";
            File.WriteAllText(temporary, "{\"Enabled\":false}");
            File.Move(temporary, settings, true);
        }
        else if (mode == "early-worker-loss") worker.Kill();
        else if (mode == "early-guard-loss")
        {
            var ready = Path.Combine(state, "dialog-integration", "sessions", lease!.Token + ".json.ready");
            Wait(() => File.Exists(ready), 1000, "the exact provisional guardian");
            using var guardian = ExactGuardianProcess(ready);
            var ownedGuardian = ProcessTree.Descendants(agent.Id, agent.StartTime).Any(child => child.Id == guardian.Id && child.Start == guardian.StartTime);
            Check("the killed guardian belongs only to this test agent", ownedGuardian);
            if (!ownedGuardian) throw new InvalidOperationException("Refusing to stop a guardian outside this exact test process tree.");
            watch.Own(guardian.Id); guardian.Kill();
        }
        var loss = mode is "early-worker-loss" or "early-guard-loss";
        var displayDeadline = mode == "read-timeout" ? 10000 : loss ? 5000 : 1000;
        Wait(() => !DialogNative.IsWindow(proxy) || !DialogNative.IsWindowVisible(proxy) || DialogNative.IsHidden(proxy),
            displayDeadline, mode == "read-timeout" ? "the bounded provider read returning to Windows" : "immediate provisional dismissal");
        Console.WriteLine($"  provisional dismissal took {response.Elapsed.TotalMilliseconds:F1} ms");
        Check(mode == "read-timeout" ? "the full read obeys its eight-second budget" : "early dismissal does not wait for provider completion",
            response.ElapsedMilliseconds <= displayDeadline);

        if (mode == "early-cancel")
        {
            Wait(() => File.Exists(resultPath) && fixture.HasExited, 1000, "the original caller's early cancellation result");
            visibility.Stop();
            Check("early cancellation never reveals the still-live original", visibility.OpaqueAfterCancel == 0);
            var contents = ReadResult(resultPath);
            using var result = JsonDocument.Parse(contents);
            var data = result.RootElement;
            Check("early Cancel returns the native cancellation HRESULT", data.GetProperty("result").GetInt32() == unchecked((int)0x800704C7));
            Check("early Cancel returns no selected paths and never invokes native OK",
                data.GetProperty("paths").GetArrayLength() == 0 && data.GetProperty("onAccept").ValueKind == JsonValueKind.Null);
            Check("the cancellation result comes from the exact original caller", data.GetProperty("processId").GetInt32() == fixture.Id);
            Thread.Sleep(150);
            Check("the original caller finishes once, with no later result mutation", fixture.HasExited && ReadResult(resultPath) == contents);
            File.Copy(resultPath, Path.Combine(Path.GetDirectoryName(resultPath)!, "verified-result.json"));
        }
        else
        {
            Wait(() => !DialogNative.IsHidden(original) && DialogNative.IsWindowEnabled(original), 5000, "the restored original");
            Check("returning early preserves the outstanding native request without killing its caller",
                !fixture.HasExited && DialogNative.IsWindow(original) && DialogNative.IsWindowEnabled(original)
                    && !DialogNative.IsHidden(original) && !File.Exists(resultPath));
            Check("the available original dialog stays on a secondary monitor", TestScreen.OnSecondary(original));
            var restored = DialogNative.Transparency(original);
            Check("recovery restores the original transparency metadata", restored.Layered == lease!.WasLayered
                && (!restored.Layered || restored.Alpha == lease.OriginalAlpha && restored.Colour == lease.OriginalColour && restored.Flags == lease.OriginalTransparencyFlags));
            File.WriteAllText(resultPath + ".inspect-options", "read the fixture's own COM state");
            Wait(() => File.Exists(resultPath + ".options-after.json"), 2000, "the restored native option state");
            using var before = JsonDocument.Parse(ReadResult(resultPath + ".options-before.json"));
            using var after = JsonDocument.Parse(ReadResult(resultPath + ".options-after.json"));
            foreach (var key in new[] { "options", "typeIndex", "profile", "layers", "quality", "radio", "author", "profileCode", "layersCode", "qualityCode", "radioCode", "authorCode" })
                Check("recovery preserves the native " + key, before.RootElement.GetProperty(key).ToString() == after.RootElement.GetProperty(key).ToString());
            Check("option observation comes from the exact original caller", after.RootElement.GetProperty("processId").GetInt32() == fixture.Id);
        }
        var expectedEnabled = mode is not ("early-off" or "early-guard-loss");
        if (!expectedEnabled)
            Wait(() => !JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json"))).RootElement.GetProperty("Enabled").GetBoolean(),
                2000, "the requested mode-off or protected failure pause");
        using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json"))))
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(resultPath)!, "settings-before-cleanup.json"), settings.RootElement.GetRawText());
            var enabled = settings.RootElement.GetProperty("Enabled").GetBoolean();
            Console.WriteLine($"  settings before cleanup: enabled={enabled}; {settings.RootElement.GetRawText()}");
            if (mode != "early-worker-loss")
                Check(expectedEnabled ? "normal dismissal preserves enabled settings" : "mode-off or protected failure stays paused", enabled == expectedEnabled);
        }
        if (mode == "early-worker-loss")
        {
            // The guardian may pause on worker loss; agent Heal may win first
            // and restore without pausing. Both must leave this native request
            // visible without automatic reclaim. An already-paused guardian
            // can retain this exact old cookie until Heal/source close; that
            // does not permit another lease or an enabled replacement to own it.
            var stable = true; var recovery = Stopwatch.StartNew();
            var recoveryLease = lease!;
            var observedPolicy = new HashSet<string>();
            while (recovery.ElapsedMilliseconds < 1000)
            {
                using var policy = JsonDocument.Parse(ReadResult(Path.Combine(state, "dialog-integration.json")));
                var paused = !policy.RootElement.GetProperty("Enabled").GetBoolean();
                var marker = DialogNative.GetProp(original, DialogNative.LeaseProperty).ToInt64();
                var allowedMarker = marker == 0 || paused && marker == recoveryLease.Cookie;
                var newLease = Directory.EnumerateFiles(Path.Combine(state, "dialog-integration", "sessions"), "*.json")
                    .Select(DialogLease.ReadRecord).Any(record => record is not null && record.NativeProcess == fixture.Id
                        && record.NativeWindow == original.ToInt64() && record.Token != recoveryLease.Token);
                var observation = $"marker={marker} paused={paused} oldCookie={recoveryLease.Cookie} newLease={newLease}";
                if (observedPolicy.Add(observation)) Console.WriteLine("  worker-loss recovery policy: " + observation);
                stable &= !fixture.HasExited && DialogNative.IsWindow(original) && DialogNative.IsWindowEnabled(original)
                    && DialogNative.ProcessId(original) == fixture.Id && !DialogNative.IsHidden(original)
                    && allowedMarker && !newLease && !File.Exists(resultPath);
                Thread.Sleep(10);
            }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(resultPath)!, "worker-loss-stability.json"),
                JsonSerializer.Serialize(new { stable, observations = observedPolicy.ToArray(), elapsedMs = recovery.ElapsedMilliseconds }));
            Check("worker-loss recovery leaves the original available without automatic reclaim", stable);
        }
        Check("a worker with an unfinished provider read is retired before it can serve another request", worker.WaitForExit(2000));
    }

    /// <summary>Read-only sampling begins before the agent starts. It records
    /// source alpha and the exact leased proxy independently of UIA capture.</summary>
    private sealed class EarlyVisibilityWatch : IDisposable
    {
        private readonly ManualResetEvent _stop = new(false);
        private readonly Thread _thread;
        private readonly nint _original;
        private readonly uint _process;
        private readonly string _sessions;
        private DialogLeaseRecord? _record;
        private long _hidden, _visible;
        private int _sawHiddenProxy, _opaque, _cancelStarted, _cancelOpaque;
        internal DialogLeaseRecord? Record => Volatile.Read(ref _record);
        internal long FirstHidden => Interlocked.Read(ref _hidden);
        internal long FirstVisible => Interlocked.Read(ref _visible);
        internal bool SawHiddenProxy => Volatile.Read(ref _sawHiddenProxy) != 0;
        internal int OpaqueWhileProxyVisible => Volatile.Read(ref _opaque);
        internal int OpaqueAfterCancel => Volatile.Read(ref _cancelOpaque);
        internal void BeginCancel() => Interlocked.Exchange(ref _cancelStarted, 1);
        internal EarlyVisibilityWatch(nint original, uint process, string sessions)
        {
            _original = original; _process = process; _sessions = sessions;
            _thread = new Thread(Run) { IsBackground = true, Name = "early source transparency sampler" }; _thread.Start();
        }
        private void Run()
        {
            while (!_stop.WaitOne(2))
            {
                try
                {
                    if (!DialogNative.IsWindow(_original) || DialogNative.ProcessId(_original) != _process) continue;
                    var native = DialogNative.Transparency(_original);
                    var alphaZero = native.Layered && native.Alpha == 0 && (native.Flags & 2) != 0;
                    if (Volatile.Read(ref _cancelStarted) != 0 && DialogNative.IsWindowVisible(_original) && !alphaZero && !DialogNative.IsCloaked(_original))
                        Interlocked.Increment(ref _cancelOpaque);
                    if (alphaZero) Interlocked.CompareExchange(ref _hidden, Stopwatch.GetTimestamp(), 0);
                    if (Record is null && Directory.Exists(_sessions))
                        foreach (var file in Directory.EnumerateFiles(_sessions, "*.json"))
                            if (DialogLease.ReadRecord(file) is { ProxyWindow: not 0 } record && record.NativeProcess == _process && record.NativeWindow == _original.ToInt64())
                            { Volatile.Write(ref _record, record); break; }
                    if (Record is not { } lease || DialogNative.ProcessId((nint)lease.ProxyWindow) != lease.WorkerProcess) continue;
                    var visible = DialogNative.IsWindowVisible((nint)lease.ProxyWindow) && !DialogNative.IsHidden((nint)lease.ProxyWindow);
                    if (!visible) Interlocked.Exchange(ref _sawHiddenProxy, 1);
                    else
                    {
                        Interlocked.CompareExchange(ref _visible, Stopwatch.GetTimestamp(), 0);
                        if (!alphaZero) Interlocked.Increment(ref _opaque);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        internal void Stop() { _stop.Set(); _thread.Join(2000); }
        public void Dispose() { Stop(); _stop.Dispose(); }
    }

    /// <summary>Two independent native saves must reuse the same healthy warm
    /// process while keeping each caller result and recovery lease distinct.</summary>
    private static void RunRepeatSave(string root, string state, string resultPath, string sessions, Process fixture,
        ForegroundWatch watch, AutomationThread thread, UIA3Automation automation, AutomationElement firstWindow,
        ref nint original, ref DialogLeaseRecord? lease, bool recycled = false)
    {
        var firstLease = lease!;
        var firstPath = Path.Combine(root, "saved-first.txt");
        AcceptRepeatSave(firstWindow, Path.Combine(root, "saved-first"), thread);
        var firstOutput = resultPath + ".first.json";
        Wait(() => File.Exists(firstOutput), 10000, "the first native save result");
        VerifyRepeatSaveResult(firstOutput, firstPath, fixture.Id, "first");
        File.Copy(firstOutput, Path.Combine(root, "verified-first-result.json"));

        DialogLeaseRecord? secondLease = null;
        Wait(() =>
        {
            foreach (var file in Directory.EnumerateFiles(sessions, "*.json"))
                if (DialogLease.ReadRecord(file) is { ProxyWindow: not 0 } record
                    && record.NativeProcess == fixture.Id && record.Token != firstLease.Token
                    && DialogNative.IsHidden((nint)record.NativeWindow))
                    secondLease = record;
            return secondLease is not null;
        }, 12000, "the second protected proxy in the same fixture process");
        lease = secondLease!;
        original = (nint)secondLease!.NativeWindow;
        watch.Own(secondLease.WorkerProcess);
        File.WriteAllText(Path.Combine(root, "second-lease.json"), JsonSerializer.Serialize(secondLease));
        Console.WriteLine($"  repeat native {original:X}; proxy {secondLease.ProxyWindow:X}; worker {secondLease.WorkerProcess}");
        Check("the second native request comes from the same application PID", secondLease.NativeProcess == firstLease.NativeProcess);
        if (recycled)
            Check("a worker past its limit is retired after its dialog, and the next dialog is served by a new one",
                secondLease.WorkerProcess != firstLease.WorkerProcess || secondLease.WorkerStarted != firstLease.WorkerStarted);
        else
            Check("both saves use the same exact prewarmed worker process",
                secondLease.WorkerProcess == firstLease.WorkerProcess && secondLease.WorkerStarted == firstLease.WorkerStarted);
        Check("the second native dialog has its own protected recovery lease", secondLease.Token != firstLease.Token && DialogLease.BelongsToWindow(secondLease));
        Check("the second native dialog opens on a secondary monitor", TestScreen.OnSecondary(original));
        Check("the second proxy opens on a secondary monitor", TestScreen.OnSecondary((nint)secondLease.ProxyWindow));
        var secondWindow = thread.Run(() => automation.FromHandle((nint)secondLease.ProxyWindow)).GetAwaiter().GetResult();
        var secondPath = Path.Combine(root, "saved-second.txt");
        AcceptRepeatSave(secondWindow, Path.Combine(root, "saved-second"), thread);
        Wait(() => File.Exists(resultPath), 10000, "the second native save result");
        VerifyRepeatSaveResult(resultPath, secondPath, fixture.Id, "second");
        File.Copy(resultPath, Path.Combine(root, "verified-result.json"));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json")));
        Check("two completed saves leave replacement enabled", settings.RootElement.GetProperty("Enabled").GetBoolean());
        PrintFile(firstOutput + ".fixture.log", "first fixture", onlyActivations: true);
    }

    private static readonly string[] SequenceFolders = ["one", "two", "three"];

    /// <summary>
    /// Save dialogs in a row, each in a folder of its own, while one worker
    /// keeps running: every one is served by a picker the worker prepared
    /// (shown cloaked beforehand, bound to the dialog when it came), from the
    /// same process, with its own lease and window, physical volume roots, and in the
    /// dialog's own folder - a bare name typed in it comes back from the
    /// application as a file in that folder.
    /// </summary>
    private static void RunSaveSequence(string root, string state, string resultPath, string sessions, Process fixture,
        ForegroundWatch watch, AutomationThread thread, UIA3Automation automation, AutomationElement firstWindow,
        ref nint original, ref DialogLeaseRecord? lease)
    {
        var first = lease!;
        var previous = first;
        var seen = new HashSet<string> { first.Token };
        var window = firstWindow;
        for (var step = 0; step < SequenceFolders.Length; step++)
        {
            var label = $"dialog {step + 1} of {SequenceFolders.Length}";
            if (step > 0)
            {
                DialogLeaseRecord? next = null;
                Wait(() =>
                {
                    foreach (var file in Directory.EnumerateFiles(sessions, "*.json"))
                        if (DialogLease.ReadRecord(file) is { ProxyWindow: not 0 } record && record.NativeProcess == fixture.Id
                            && !seen.Contains(record.Token) && DialogNative.IsHidden((nint)record.NativeWindow))
                            next = record;
                    return next is not null;
                }, 12000, "the protected proxy of " + label);
                var current = next!;
                seen.Add(current.Token);
                lease = current;
                original = (nint)current.NativeWindow;
                watch.Own(current.WorkerProcess);
                Console.WriteLine($"  {label}: native {original:X}; proxy {current.ProxyWindow:X}; worker {current.WorkerProcess}");
                Check($"{label} is served by the same worker process",
                    current.WorkerProcess == first.WorkerProcess && current.WorkerStarted == first.WorkerStarted);
                Check($"{label} has its own lease and its own picker window",
                    DialogLease.BelongsToWindow(current) && current.ProxyWindow != previous.ProxyWindow);
                Check($"{label}: the native dialog and the picker are on a secondary monitor",
                    TestScreen.OnSecondary(original) && TestScreen.OnSecondary((nint)current.ProxyWindow));
                var handle = (nint)current.ProxyWindow;
                window = thread.Run(() => automation.FromHandle(handle)).GetAwaiter().GetResult();
                previous = current;
            }
            var name = "picked-" + (step + 1);
            AcceptRepeatSave(window, name, thread);
            var output = step == SequenceFolders.Length - 1 ? resultPath : resultPath + "." + (step + 1) + ".json";
            Wait(() => File.Exists(output), 10000, "the native result of " + label);
            // A bare name resolves against the folder the picker was bound to.
            VerifyRepeatSaveResult(output, Path.Combine(root, SequenceFolders[step], name + ".txt"), fixture.Id, label);
        }

        // Each proxy writes its timing line once its dialog has closed.
        var log = Path.Combine(state, "dialog-integration", "integration.log");
        string[] timings = [];
        WaitQuietly(() => (timings = ReadTimingLines(log)).Length >= SequenceFolders.Length, 5000);
        Console.WriteLine($"  sequence: {string.Join("; ", timings.Select(line => line[(line.IndexOf("path=", StringComparison.Ordinal))..]))}");
        Check("every dialog of the sequence was served by a prepared picker, bound before UI Automation finished",
            timings.Length == SequenceFolders.Length && timings.All(line => line.Contains("path=prepared-early ", StringComparison.Ordinal)));
        var rootSnapshots = timings.Select(ReadRootSnapshot).ToArray();
        var completeSnapshots = timings.Length == SequenceFolders.Length && rootSnapshots.All(snapshot => snapshot is { Length: > 0 });
        Check("each prepared picker records the exact nonempty root paths matching its root count",
            completeSnapshots);
        Check("every prepared picker root is a physical drive or share root, with no duplicate root paths",
            completeSnapshots && rootSnapshots.All(snapshot => snapshot is { Length: > 0 } paths && paths.All(IsPhysicalRoot)
                && paths.Select(RootKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length));
        var requestedVolume = Path.GetPathRoot(root)!;
        Check("each prepared picker contains the caller's physical volume exactly once",
            completeSnapshots && rootSnapshots.All(snapshot => snapshot!.Count(path => SameRoot(path, requestedVolume)) == 1));
        Check("repeated dialogs on the same volume keep the same physical roots without accumulating session-folder roots",
            completeSnapshots && rootSnapshots.All(snapshot => snapshot!.Select(RootKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(rootSnapshots[0]!.Select(RootKey)))
            && rootSnapshots.All(snapshot => SequenceFolders.All(folder => snapshot!.All(path => !SameRoot(path, Path.Combine(root, folder))))));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json")));
        Check("the whole sequence leaves replacement enabled", settings.RootElement.GetProperty("Enabled").GetBoolean());
    }

    private static string[]? ReadRootSnapshot(string timing)
    {
        try
        {
            var fields = timing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var encoded = fields.SingleOrDefault(field => field.StartsWith("roots-data=", StringComparison.Ordinal));
            var count = fields.SingleOrDefault(field => field.StartsWith("roots=", StringComparison.Ordinal));
            if (encoded is null || count is null || !int.TryParse(count["roots=".Length..], out var expected) || expected <= 0) return null;
            var paths = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded["roots-data=".Length..]));
            return paths is not null && paths.Length == expected && paths.All(path => !string.IsNullOrWhiteSpace(path)) ? paths : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string RootKey(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SameRoot(string first, string second) => string.Equals(RootKey(first), RootKey(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsPhysicalRoot(string path)
    {
        try { return Path.IsPathFullyQualified(path) && Path.GetPathRoot(path) is { Length: > 0 } root && SameRoot(path, root); }
        catch (ArgumentException) { return false; }
    }

    private static Process ExactGuardianProcess(string ready)
    {
        var identity = DialogLease.ReadGuardianIdentity(ready)
            ?? throw new InvalidOperationException("The owned guardian readiness file has no valid process identity.");
        var guardian = Process.GetProcessById(identity.Process);
        try
        {
            if (guardian.StartTime.ToUniversalTime().Ticks != identity.StartedTicks)
                throw new InvalidOperationException("Refusing to stop a guardian whose process identity no longer matches.");
            return guardian;
        }
        catch
        {
            guardian.Dispose();
            throw;
        }
    }

    /// <summary>Each replacement's timing: from recognition, and - for a dialog that appeared while the
    /// replacement was already waiting - from the dialog's appearance (NaN for one already open).</summary>
    private static readonly List<(string Mode, string Path, bool JustShown, double ShownToRecognised, double Frame, double Ready)> Timings = [];

    private static string ReadLog(string log)
    {
        try
        {
            if (!File.Exists(log)) return string.Empty;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }
    }

    /// <summary>
    /// The fixture's result, once it has finished writing it: the file
    /// exists a moment before the fixture lets go of it.
    /// </summary>
    private static string ReadResult(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = File.ReadAllText(path);
                using (JsonDocument.Parse(text)) return text;
            }
            catch (Exception ex) when (ex is IOException or JsonException && attempt < 40) { Thread.Sleep(50); }
        }
    }

    private static string[] ReadTimingLines(string log) =>
        ReadLog(log).Split('\n').Where(line => line.Contains(" timing path=", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// What the proxy measured from the moment the listener recognised the dialog and, when the
    /// fixture's dialog appeared while the replacement was already waiting for it, what that adds:
    /// the time from its EVENT_OBJECT_SHOW, seen by this runner's own hook, to its recognition.
    /// A dialog already open when the listener started is timed from recognition only.
    /// </summary>
    private static void ReportTimings(string log, string mode, ShowWatch shows, long listening)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in ReadTimingLines(log))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line,
                @"timing path=(\S+) recognised->frame=(-?\d+) ms, recognised->ready=(-?\d+) ms");
            if (!match.Success) continue;
            var frame = double.Parse(match.Groups[2].Value, inv);
            var ready = double.Parse(match.Groups[3].Value, inv);
            var at = System.Text.RegularExpressions.Regex.Match(line, @" recognised-qpc=(\d+)");
            var shownToRecognised = double.NaN;
            // The listener stamps a dialog recognised from its own copy of the
            // same EVENT_OBJECT_SHOW this runner sees: either may be first.
            if (at.Success && long.Parse(at.Groups[1].Value, inv) is var recognised
                && shows.LastBefore(recognised + Stopwatch.Frequency / 20) is { } appeared)
            {
                var gap = Stopwatch.GetElapsedTime(appeared, recognised).TotalMilliseconds;
                // Appeared once the listener was running: a dialog it was waiting for.
                if (listening != 0 && appeared >= listening && gap >= -50) shownToRecognised = Math.Max(0, gap);
            }
            var justShown = !double.IsNaN(shownToRecognised);
            // A dialog that waited for a worker to be replaced says nothing
            // about how quickly one is served: shown, not summed up.
            if (mode == "save-recycle" && justShown)
                Console.WriteLine("  timing  (the next line's dialog waited for its new worker; it is left out of the summary)");
            else Timings.Add((mode, match.Groups[1].Value, justShown, shownToRecognised, frame, ready));
            var readyText = ready < 0 ? "never" : ready.ToString("F0", inv) + " ms";
            Console.WriteLine(justShown
                ? $"  timing  {mode} ({match.Groups[1].Value}, dialog just shown): dialog shown -> recognised {shownToRecognised:F0} ms;"
                    + $" recognised -> our frame on screen {frame:F0} ms, -> ready to accept {readyText};"
                    + $" dialog shown -> our frame {shownToRecognised + frame:F0} ms, -> ready {(ready < 0 ? "never" : (shownToRecognised + ready).ToString("F0", inv) + " ms")}"
                : $"  timing  {mode} ({match.Groups[1].Value}, dialog already open): recognised -> our frame on screen {frame:F0} ms; -> ready to accept {readyText}");
        }
    }

    private static void ReportTimingSummary()
    {
        static string Stat(IEnumerable<double> values)
        {
            var sorted = values.Where(value => value >= 0).Order().ToArray();
            if (sorted.Length == 0) return "-";
            var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            return $"median {median:F0} ms, worst {sorted[^1]:F0} ms";
        }
        foreach (var group in Timings.GroupBy(timing => (timing.Path, timing.JustShown)).OrderBy(group => group.Key.Path).ThenBy(group => group.Key.JustShown))
        {
            var (path, justShown) = group.Key;
            if (justShown)
                Console.WriteLine($"timing summary ({path}, dialogs just shown, {group.Count()}): dialog shown -> recognised {Stat(group.Select(timing => timing.ShownToRecognised))};"
                    + $" recognised -> our frame on screen {Stat(group.Select(timing => timing.Frame))}; -> ready to accept {Stat(group.Select(timing => timing.Ready))};"
                    + $" dialog shown -> our frame {Stat(group.Select(timing => timing.ShownToRecognised + timing.Frame))}; -> ready {Stat(group.Where(timing => timing.Ready >= 0).Select(timing => timing.ShownToRecognised + timing.Ready))}");
            else
                Console.WriteLine($"timing summary ({path}, dialogs already open, {group.Count()}): recognised -> our frame on screen {Stat(group.Select(timing => timing.Frame))};"
                    + $" -> ready to accept {Stat(group.Select(timing => timing.Ready))}");
        }
    }

    /// <summary>
    /// The prepared worker with nothing to do - as it waits for as long as the integration is on:
    /// its window shown only cloaked, and what it costs meanwhile. It must wake for nothing.
    /// </summary>
    private static void RunIdleWorkerCase(Process agent)
    {
        Thread.Sleep(5000);
        var workers = ProcessTree.Descendants(agent.Id, agent.StartTime).Select(process => process.Id).ToHashSet();
        var owner = 0;
        var windows = new List<nint>();
        DialogNative.EnumWindows((window, _) =>
        {
            var process = (int)DialogNative.ProcessId(window);
            if (workers.Contains(process) && DialogNative.IsWindowVisible(window))
            {
                windows.Add(window);
                if (DialogNative.ClassName(window).StartsWith("HwndWrapper[UltraExplorer", StringComparison.Ordinal)) owner = process;
            }
            return true;
        }, 0);
        Check("the waiting worker keeps one prepared picker, shown only cloaked", owner != 0 && windows.All(DialogNative.IsCloaked));
        if (owner == 0) return;
        using var worker = Process.GetProcessById(owner);
        worker.Refresh();
        var before = worker.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        Thread.Sleep(15000);
        worker.Refresh();
        var cpu = (worker.TotalProcessorTime - before).TotalMilliseconds;
        var user = GetGuiResources(worker.Handle, 1);
        var gdi = GetGuiResources(worker.Handle, 0);
        Console.WriteLine($"  idle worker {owner}: {cpu:F0} ms of CPU in {clock.Elapsed.TotalSeconds:F0} s; private memory {worker.PrivateMemorySize64 / 1048576} MB,"
            + $" working set {worker.WorkingSet64 / 1048576} MB, {worker.Threads.Count} threads, {worker.HandleCount} handles, {user} USER and {gdi} GDI objects");
        Check("the waiting worker wakes for nothing: under 100 ms of CPU in 15 s", cpu < 100);
        Check("the waiting worker holds no more than 300 USER objects", user < 300);
    }

    private static void AcceptRepeatSave(AutomationElement window, string name, AutomationThread thread)
    {
        Thread.Sleep(700);
        thread.Run(() => Required(Required(window.FindFirstDescendant("PickerNameBox"))
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))).AsTextBox().Text = name).GetAwaiter().GetResult();
        Thread.Sleep(200);
        thread.Run(() => Required(window.FindFirstDescendant("PickerAcceptButton")).AsButton().Invoke()).GetAwaiter().GetResult();
    }

    private static void VerifyRepeatSaveResult(string output, string expected, int fixturePid, string phase)
    {
        using var result = JsonDocument.Parse(ReadResult(output));
        var data = result.RootElement;
        Check($"the {phase} native caller accepts its save", data.GetProperty("result").GetInt32() == 0);
        var paths = data.GetProperty("paths").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Check($"the {phase} native result is the exact requested file",
            paths.Length == 1 && string.Equals(paths[0], expected, StringComparison.OrdinalIgnoreCase));
        Check($"the {phase} save result was produced by the original fixture PID", data.GetProperty("processId").GetInt32() == fixturePid);
    }

    /// <summary>The program's own "already exists" warning over its dialog: seen, on a secondary monitor, answered Yes.</summary>
    private static void AnswerOverwriteWarning(nint original, Process fixture, AutomationThread thread, UIA3Automation automation)
    {
        nint prompt = 0;
        Wait(() =>
        {
            DialogNative.EnumWindows((candidate, _) =>
            {
                if (candidate != original && DialogNative.ProcessId(candidate) == fixture.Id && DialogNative.ClassName(candidate) == "#32770" && DialogNative.IsWindowVisible(candidate)) prompt = candidate;
                return true;
            }, 0);
            return prompt != 0;
        }, 5000, "overwrite warning");
        Console.WriteLine($"  overwrite warning {prompt:X} {TestScreen.Describe(prompt)}");
        Check("the original overwrite warning is visible", !DialogNative.IsHidden(prompt) && !DialogNative.IsHidden(original));
        Check("the overwrite warning opens on a secondary monitor", TestScreen.OnSecondary(prompt));
        AnswerYes(prompt, thread, automation);
    }

    /// <summary>
    /// A dialog shown the way a real program shows it (the fixture's
    /// producers and its edge modes): what the runner types in the picker
    /// (null: it accepts what the program proposed; ProposedName, the name a
    /// Save dialog was given, checks the picker shows what the dialog shows), what the program must receive (relative to the
    /// case's folder; a bare typed name proves the picker opened in the
    /// program's folder), the file type the program must get back (the picker
    /// hands its own choice back, so a lost FilterIndex shows here), whether
    /// the program asks before replacing, the files the case needs, and
    /// whether the dialog stays with Windows by design: one the listener never
    /// takes for a file dialog, or (Looked) one it looks at and leaves with
    /// Windows, saying why. (A served case that stays with Windows instead
    /// times out waiting for its proxy, and the run prints integration.log.)
    /// </summary>
    private sealed record ProducerCase(string? Typed, string[] Expected, int? TypeIndex = null, bool Overwrite = false,
        string? ProposedName = null, string[]? Files = null, bool StaysNative = false, bool Looked = false);

    private const string TwoFiles = "\"{root}\\sample.txt\" \"{root}\\second.txt\"";

    private static readonly Dictionary<string, ProducerCase> Producers = new()
    {
        // WinForms: no Filter; three types with FilterIndex 2; Multiselect; InitialDirectory; Save with
        // DefaultExt + AddExtension and no types; Save with OverwritePrompt; the Vista FolderBrowserDialog.
        ["winforms-open"] = new(@"{root}\sample.txt", ["sample.txt"]),
        ["winforms-open-filter"] = new(@"{root}\picture.png", ["picture.png"], TypeIndex: 2, Files: ["picture.png"]),
        ["winforms-open-multi"] = new(TwoFiles, ["sample.txt", "second.txt"], TypeIndex: 1),
        ["winforms-open-initialdir"] = new("inner.txt", [@"Initial folder\inner.txt"], Files: [@"Initial folder\inner.txt"]),
        ["winforms-save"] = new(@"{root}\saved-result", ["saved-result.log"], ProposedName: "export"),
        ["winforms-save-overwrite"] = new(@"{root}\sample", ["sample.txt"], TypeIndex: 1, Overwrite: true, ProposedName: "export"),
        ["winforms-folder"] = new(@"{root}\subfolder", ["subfolder"]),
        // WPF, Microsoft.Win32 (the common item dialog since .NET 8).
        ["wpf-open"] = new(@"{root}\sample.txt", ["sample.txt"], TypeIndex: 1),
        ["wpf-save"] = new(@"{root}\saved-result", ["saved-result.csv"], TypeIndex: 2, ProposedName: "export"),
        ["wpf-folder"] = new(@"{root}\subfolder", ["subfolder"]),
        // Win32 GetOpenFileNameW / GetSaveFileNameW, OFN_EXPLORER without a hook: ten types, long Russian labels.
        ["win32-open"] = new(@"{root}\picture.png", ["picture.png"], TypeIndex: 2, Files: ["picture.png"]),
        ["win32-open-multi"] = new(TwoFiles, ["sample.txt", "second.txt"], TypeIndex: 1),
        ["win32-save"] = new(@"{root}\saved-result", ["saved-result.txt"], TypeIndex: 1, ProposedName: "export"),
        // With a hook procedure Windows shows its older Explorer-style dialog, whose
        // own controls (a program's template: a preview, options of its own) the
        // picker cannot mirror: looked at, and left with Windows.
        ["win32-hook"] = new(null, [], StaysNative: true, Looked: true),
        // IFileDialog edge cases.
        ["open-types24"] = new(@"{root}\frame.ppm", ["frame.ppm"], TypeIndex: 17, Files: ["frame.ppm"]),
        ["open-psdlabel"] = new(@"{root}\layers.psd", ["layers.psd"], TypeIndex: 2, Files: ["layers.psd"]),
        ["save-defaultname"] = new(null, ["Quarterly report.csv"], TypeIndex: 1, ProposedName: "Quarterly report"),
        ["open-cyrillic"] = new("отчёт за год.txt", [@"Папка с пробелами\Мои документы 2026\отчёт за год.txt"],
            Files: [@"Папка с пробелами\Мои документы 2026\отчёт за год.txt"]),
        ["folder-driveroot"] = new(@"{root}\subfolder", ["subfolder"]),
        ["save-overwrite-prompt"] = new(null, ["sample.txt"], TypeIndex: 1, Overwrite: true, ProposedName: "sample.txt"),
        // The legacy tree: never taken for a file dialog, left alone.
        ["shbrowse"] = new(null, [], StaysNative: true),
    };

    private static AutomationElement NameEdit(AutomationElement window) =>
        Required(Required(window.FindFirstDescendant("PickerNameBox")).FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)));

    /// <summary>The picker serves the program's dialog and the program gets exactly what was chosen.</summary>
    private static void RunProducerCase(ProducerCase spec, string root, string state, string resultPath, Process fixture,
        nint original, AutomationThread thread, UIA3Automation automation, AutomationElement window)
    {
        Thread.Sleep(700);
        if (spec.ProposedName is { } proposed)
        {
            // What the program's own dialog shows: its name, with the type's
            // extension when Explorer shows extensions ("export" -> "export.log").
            var native = DialogNative.ReadEdit(DialogNative.FindVisibleChild(original, 1001, "Edit")) ?? "";
            var shown = thread.Run(() => NameEdit(window).AsTextBox().Text).GetAwaiter().GetResult();
            Check($"the picker proposes the name the program's dialog shows (\"{native}\", from \"{proposed}\")",
                shown == native && native.StartsWith(proposed, StringComparison.Ordinal));
            if (shown != native) Console.WriteLine($"        the picker shows \"{shown}\"");
        }
        if (spec.Typed is { } typed)
            thread.Run(() => NameEdit(window).AsTextBox().Text = typed.Replace("{root}", root, StringComparison.Ordinal)).GetAwaiter().GetResult();
        Thread.Sleep(200);
        thread.Run(() => Required(window.FindFirstDescendant("PickerAcceptButton")).AsButton().Invoke()).GetAwaiter().GetResult();
        if (spec.Overwrite) AnswerOverwriteWarning(original, fixture, thread, automation);
        Wait(() => File.Exists(resultPath), 10000, "the native result");
        using var result = JsonDocument.Parse(ReadResult(resultPath));
        var data = result.RootElement;
        if (data.TryGetProperty("error", out var error)) throw new InvalidOperationException("The fixture failed: " + error.GetString());
        Check("the program accepts the choice from its own dialog", data.GetProperty("result").GetInt32() == 0);
        var paths = data.GetProperty("paths").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var expected = spec.Expected.Select(name => Path.Combine(root, name)).ToArray();
        var exact = paths.Length == expected.Length && expected.All(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase));
        Check($"the program receives exactly the intended {(expected.Length == 1 ? "path" : expected.Length + " paths")}", exact);
        if (!exact) Console.WriteLine($"        expected {string.Join(" | ", expected)}, got {string.Join(" | ", paths)}");
        if (spec.TypeIndex is { } type)
        {
            var got = data.GetProperty("typeIndex").GetInt32();
            Check($"the program's file type {type} comes back as it chose it", got == type);
            if (got != type) Console.WriteLine($"        the program got file type {got}");
        }
        var log = ReadLog(Path.Combine(state, "dialog-integration", "integration.log"));
        Check("it was served through the picker, never left with Windows",
            log.Contains("dialog shown through UltraExplorer", StringComparison.Ordinal) && !log.Contains("stays with Windows", StringComparison.Ordinal));
        using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json"))))
            Check("a completed native handoff leaves replacement enabled", settings.RootElement.GetProperty("Enabled").GetBoolean());
        File.Copy(resultPath, Path.Combine(root, "verified-result.json"));
    }

    /// <summary>
    /// A dialog that is not a file dialog of the kind replaced stays with
    /// Windows, and gracefully: the listener does not take it for one and says
    /// nothing (the legacy SHBrowseForFolder tree), or looks at it and says why
    /// it leaves it (the older Explorer-style dialog a hook procedure gets);
    /// nothing of ours is put on screen, no lease is taken, the original is
    /// never hidden or written to, and the program's own dialog still answers
    /// the user - here, a Cancel.
    /// </summary>
    private static void RunStaysNativeCase(ProducerCase spec, string state, string resultPath, nint original)
    {
        var log = Path.Combine(state, "dialog-integration", "integration.log");
        // Long enough for its event, several of the listener's polls and a per-dialog process.
        Thread.Sleep(4000);
        var text = ReadLog(log);
        var reasons = text.Split('\n').Select(line => line.Trim()).Where(line => line.Contains("stays with Windows", StringComparison.Ordinal)).ToArray();
        foreach (var line in reasons) Console.WriteLine("  | integration.log: " + line);
        if (spec.Looked)
            Check("it is looked at and left with Windows, and the log says why",
                DialogNative.LooksLikeFileDialog(original) && reasons.Length > 0);
        else
            Check("it is not taken for a file dialog, and the listener says nothing about it",
                !DialogNative.LooksLikeFileDialog(original) && reasons.Length == 0);
        Check("nothing of ours was put on screen for it (no picker frame, no replacement)",
            !text.Contains(" timing path=", StringComparison.Ordinal) && !text.Contains("shown through UltraExplorer", StringComparison.Ordinal)
            && !text.Contains("presented and protected", StringComparison.Ordinal));
        var sessions = Path.Combine(state, "dialog-integration", "sessions");
        Check("no lease was taken on it", !Directory.Exists(sessions) || !Directory.EnumerateFiles(sessions, "*.json").Any());
        Check("the program's dialog stays visible and usable, never hidden",
            DialogNative.IsWindow(original) && DialogNative.IsWindowVisible(original) && DialogNative.IsWindowEnabled(original)
            && !DialogNative.IsHidden(original) && !DialogNative.IsCloaked(original));
        Check("the program's dialog was not answered for it", !File.Exists(resultPath));
        Check("the program's dialog stays on a secondary monitor", TestScreen.OnSecondary(original));
        // The user answers the program's own dialog; the program gets its answer as usual.
        DialogNative.Click(original, 2);
        Wait(() => File.Exists(resultPath), 5000, "the program's own cancellation");
        using var result = JsonDocument.Parse(ReadResult(resultPath));
        var data = result.RootElement;
        if (data.TryGetProperty("error", out var error)) throw new InvalidOperationException("The fixture failed: " + error.GetString());
        Check("its own Cancel reaches the program", data.GetProperty("result").GetInt32() == unchecked((int)0x800704C7)
            && data.GetProperty("paths").GetArrayLength() == 0);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "dialog-integration.json")));
        Check("a dialog left with Windows leaves replacement enabled", settings.RootElement.GetProperty("Enabled").GetBoolean());
    }

    /// <summary>Answers the native overwrite warning "Yes" without activating it:
    /// the button's command goes to its parent as a click would send it. Only
    /// if that is ignored is the button invoked through UI Automation.</summary>
    private static void AnswerYes(nint prompt, AutomationThread thread, UIA3Automation automation)
    {
        var yes = DialogNative.FindChild(prompt, 6, "Button");
        if (yes != 0 && PostMessage(GetParent(yes), 0x111, 6, yes)
            && WaitQuietly(() => !DialogNative.IsWindow(prompt) || !DialogNative.IsWindowVisible(prompt), 3000)) return;
        // The Save dialog's warning is a task dialog: TDM_CLICK_BUTTON presses
        // its Yes (IDYES) exactly as a click would, without activating it.
        if (yes == 0 && PostMessage(prompt, 0x0400 + 102, 6, 0)
            && WaitQuietly(() => !DialogNative.IsWindow(prompt) || !DialogNative.IsWindowVisible(prompt), 3000)) return;
        Console.WriteLine($"  note  the warning's Yes {(yes == 0 ? "is not a Win32 button" : "ignored its command")}; invoking it through UI Automation");
        // A task dialog builds its buttons after its window appears: the tree
        // can be empty for a moment, so it is asked again until they are there.
        var invoked = WaitQuietly(() => thread.Run(() =>
        {
            try
            {
                var warning = automation.FromHandle(prompt);
                var button = warning.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(candidate => candidate.Name.Replace("&", "").Trim().ToLowerInvariant() is "yes" or "да"
                        || candidate.Properties.AutomationId.ValueOrDefault is "6" or "CommandButton_6");
                if (button is null) return false;
                button.AsButton().Invoke();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }).GetAwaiter().GetResult(), 4000);
        if (!invoked)
        {
            Console.WriteLine($"  warning {prompt:X} class {DialogNative.ClassName(prompt)} '{DialogNative.Title(prompt)}' visible {DialogNative.IsWindowVisible(prompt)}");
            try
            {
                thread.Run(() =>
                {
                    foreach (var element in automation.FromHandle(prompt).FindAllDescendants().Take(40))
                        Console.WriteLine($"    {element.ControlType} '{element.Name}' id '{element.Properties.AutomationId.ValueOrDefault}'");
                }).Wait(5000);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine("    (tree unavailable: " + ex.GetBaseException().Message + ")"); }
            throw new InvalidOperationException("The overwrite warning's Yes button was not found.");
        }
    }

    private static void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine($"  cleanup: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void PrintFile(string path, string label, bool onlyActivations = false)
    {
        try
        {
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            if (onlyActivations)
            {
                var refused = lines.Count(line => line.Contains("refused activation", StringComparison.Ordinal));
                Console.WriteLine($"  {label}: {refused} activation(s) of its windows refused");
                foreach (var line in lines.Where(line => line.Contains("refused activation", StringComparison.Ordinal)).Take(6)) Console.WriteLine($"  | {line}");
                return;
            }
            foreach (var line in lines.TakeLast(40)) Console.WriteLine($"  | {label}: {line}");
        }
        catch (IOException) { }
    }

    private static Process Start(string exe, string[] arguments, string state, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["ULTRAEXPLORER_STATE_DIR"] = state;
        // UltraExplorer's test copies open on a secondary monitor and never activate.
        start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        return Process.Start(start) ?? throw new IOException("A fixture could not be started.");
    }
    private static void Wait(Func<bool> ready, int milliseconds, string description)
    {
        if (!WaitQuietly(ready, milliseconds)) throw new TimeoutException("Waiting for " + description);
    }
    private static bool WaitQuietly(Func<bool> ready, int milliseconds)
    {
        var time = Stopwatch.StartNew();
        while (!ready()) { if (time.ElapsedMilliseconds >= milliseconds) return false; Thread.Sleep(50); }
        return true;
    }
    private static AutomationElement Required(AutomationElement? element) => element ?? throw new InvalidOperationException("A required picker control was not found.");

    [DllImport("user32.dll")] private static extern nint SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    /// <summary>Whether <paramref name="upper"/> comes before <paramref name="lower"/> in the z-order (is drawn over it).</summary>
    private static bool IsAbove(nint upper, nint lower)
    {
        for (var window = DialogNative.GetWindow(upper, 2); window != 0; window = DialogNative.GetWindow(window, 2)) // GW_HWNDNEXT
            if (window == lower) return true;
        return false;
    }
}

/// <summary>Every foreground change while a case runs, from a WinEvent hook on
/// a thread of its own. A change to a window of a process this test started -
/// the fixture, UltraExplorer's test copy - is a violation: the user watching
/// something on the primary monitor would have lost the keyboard.</summary>
internal sealed class ForegroundWatch : IDisposable
{
    private readonly string[] _images;
    private readonly HashSet<int> _own = [];
    private readonly List<string> _events = [];
    private readonly List<string> _violations = [];
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private WinEventProc? _callback;

    public ForegroundWatch(string[] images)
    {
        _images = images;
        _thread = new Thread(Pump) { IsBackground = true, Name = "foreground watch" };
        _thread.Start();
        _ready.Wait(3000);
        Record(GetForegroundWindow(), "at start");
    }

    public void Own(int process) { lock (_own) _own.Add(process); }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        _callback = (_, _, window, objectId, _, _, _) => { if (objectId == 0) Record(window, "changed to"); };
        var hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        _ready.Set();
        try { while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message); }
        finally { if (hook != 0) UnhookWinEvent(hook); }
    }

    private void Record(nint window, string when)
    {
        GetWindowThreadProcessId(window, out var process);
        var image = Image(process);
        bool own;
        lock (_own) own = _own.Contains((int)process);
        own |= _images.Any(path => string.Equals(path, image, StringComparison.OrdinalIgnoreCase));
        // The user's own windows are named by process only; a test window in
        // full, and with what the user was doing then: the machine is in use
        // while the tests run, and a click on a test window on the other
        // monitor activates it too (input a few ms before, pointer over it).
        var line = $"{DateTime.Now:HH:mm:ss.fff} foreground {when} {(window == 0 ? "nothing" : $"{window:X}")} of {Path.GetFileName(image)} ({process})"
            + (own ? $" class {DialogNative.ClassName(window)} '{DialogNative.Title(window)}'{UserInput(window)}" : "");
        lock (_events)
        {
            _events.Add(line);
            if (own) _violations.Add(line);
        }
    }

    public void Stop()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12, 0, 0);
        _thread.Join(2000);
        Record(GetForegroundWindow(), "at end");
    }

    public int Report()
    {
        lock (_events)
        {
            foreach (var line in _events) Console.WriteLine("  " + line);
            foreach (var line in _violations) Console.WriteLine("  FOREGROUND TAKEN: " + line);
            return _violations.Count;
        }
    }

    public void Dispose() { if (_thread.IsAlive) Stop(); _ready.Dispose(); }

    /// <summary>How long before now the user last moved the mouse or pressed a key, and whether the pointer is over <paramref name="window"/>.</summary>
    private static string UserInput(nint window)
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input) || !GetCursorPos(out var pointer) || DialogNative.WindowBounds(window) is not { } bounds) return "";
        var over = pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
        return $" (user input {unchecked((uint)Environment.TickCount - input.Time)} ms before, pointer {(over ? "over it" : "elsewhere")} at {pointer.X},{pointer.Y})";
    }

    private static string Image(uint process)
    {
        var handle = OpenProcess(0x1000, false, process);
        if (handle == 0) return "?";
        try
        {
            var text = new StringBuilder(1024);
            var length = text.Capacity;
            return QueryFullProcessImageName(handle, 0, text, ref length) ? text.ToString() : "?";
        }
        finally { CloseHandle(handle); }
    }

    private delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int length);
}

/// <summary>When each top-level dialog of one process appeared (EVENT_OBJECT_SHOW), from a WinEvent
/// hook on a thread of its own, on the clock every process shares (<see cref="Stopwatch.GetTimestamp"/>).</summary>
internal sealed class ShowWatch : IDisposable
{
    private readonly uint _process;
    private readonly List<long> _shown = [];
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private WinEventProc? _callback;

    public ShowWatch(uint process)
    {
        _process = process;
        _thread = new Thread(Pump) { IsBackground = true, Name = "show watch" };
        _thread.Start();
        _ready.Wait(3000);
    }

    /// <summary>The latest appearance at or before <paramref name="timestamp"/>, if any.</summary>
    public long? LastBefore(long timestamp)
    {
        lock (_shown) return _shown.Where(at => at <= timestamp).Select(at => (long?)at).LastOrDefault();
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        _callback = (_, _, window, objectId, childId, _, _) =>
        {
            if (objectId != 0 || childId != 0 || window == 0) return;
            var at = Stopwatch.GetTimestamp();
            if (GetAncestor(window, 2) != window || DialogNative.ClassName(window) != "#32770") return;
            lock (_shown) _shown.Add(at);
        };
        var hook = SetWinEventHook(0x8002, 0x8002, 0, _callback, _process, 0, 0); // EVENT_OBJECT_SHOW, out of context
        _ready.Set();
        try { while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message); }
        finally { if (hook != 0) UnhookWinEvent(hook); }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12, 0, 0);
        _thread.Join(2000);
        _ready.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

/// <summary>Processes descended from one this test started, each verified by its
/// start time so that a reused process id is never mistaken for one of them.</summary>
internal static class ProcessTree
{
    public static List<(int Id, DateTime Start, string Image)> Descendants(int root, DateTime rootStart)
    {
        var parents = new Dictionary<int, int>();
        var images = new Dictionary<int, string>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return [];
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            { parents[(int)entry.Process] = (int)entry.Parent; images[(int)entry.Process] = entry.Image; }
        }
        finally { CloseHandle(snapshot); }
        var found = new List<(int, DateTime, string)>();
        var queue = new Queue<(int Id, DateTime Start)>([(root, rootStart)]);
        while (queue.Count > 0)
        {
            var (parent, parentStart) = queue.Dequeue();
            foreach (var (child, childParent) in parents)
            {
                if (childParent != parent || child == parent) continue;
                try
                {
                    using var process = Process.GetProcessById(child);
                    if (process.StartTime < parentStart) continue;
                    found.Add((child, process.StartTime, images[child]));
                    queue.Enqueue((child, process.StartTime));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size; public uint Usage, Process; public nint Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Image;
    }
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
