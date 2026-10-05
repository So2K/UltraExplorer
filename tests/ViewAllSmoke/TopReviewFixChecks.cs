using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The findings of the 2026-10-02 review that took every window of the process
/// down with them.  A search walk that met a file or folder dated past the year
/// 9999 ended the process (I001).  A new window asked every drive whether it
/// was ready on the UI thread all windows share, so a drive mapped to a server
/// that is off froze them all for some twenty seconds (I002).  A folder window
/// looked its folder up for the Shell on that thread too, on every move and
/// then every second while the folder did not answer (I003).
/// </summary>
internal static partial class Program
{
    /// <summary>Set for the copy of this harness the search-walk check starts: the folder that copy walks.</summary>
    private const string TopReviewWalkVariable = "ULTRAEXPLORER_TOP_REVIEW_WALK";

    private static async Task TopReviewFixChecks()
    {
        if (Environment.GetEnvironmentVariable(TopReviewWalkVariable) is { Length: > 0 } walked)
        {
            await TopReviewWalkChildAsync(walked);
            return;
        }

        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerTopReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TopReviewWalkChecksAsync(root);
            RunOnSta("navigation pane off the UI thread", () => TopReviewSidebarChecksAsync(root));
            RunOnSta("shell registration off the UI thread", () => TopReviewRegistrationChecksAsync(root));
            RunOnSta("integration settings raised only on a change", TopReviewSettingsChangedChecksAsync);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Once something has been <paramref name="asked"/>, whether the UI thread
    /// of <paramref name="dispatcher"/> runs a no-op within two seconds; then
    /// lets whatever was asked go on.
    /// </summary>
    private static Task<bool> TopReviewAnswersWhileHeld(Dispatcher dispatcher, ManualResetEventSlim asked, ManualResetEventSlim released)
        => Task.Run(() =>
        {
            try
            {
                return asked.Wait(TimeSpan.FromSeconds(20))
                    && dispatcher.InvokeAsync(() => { }).Task.Wait(TimeSpan.FromSeconds(2));
            }
            finally
            {
                released.Set();
            }
        });

    // ---- I001: the search walk and a date past the year 9999 ----------------------

    /// <summary>
    /// The walk lists folders on threads of its own, where an exception nobody
    /// catches ends the process.  So it is run in a second copy of this
    /// harness, and what is checked is what is left of that copy: a process
    /// that ended, or one that says what it found.
    /// </summary>
    private static async Task TopReviewWalkChecksAsync(string root)
    {
        Section("search walk: a bad date or a folder that cannot be read ends neither the walk nor the process (I001)");
        var walked = Path.Combine(root, "walked");
        var bad = Path.Combine(walked, "bad");
        var deeper = Path.Combine(bad, "deeper");
        var folder = Path.Combine(walked, "needle-folder");
        var future = Path.Combine(bad, "needle-future.txt");
        Directory.CreateDirectory(deeper);
        Directory.CreateDirectory(folder);
        File.WriteAllText(future, "x");
        File.WriteAllText(Path.Combine(bad, "needle-plain.txt"), "x");
        File.WriteAllText(Path.Combine(deeper, "needle-deep.txt"), "x");

        const long farFuture = 0x7FFF_0000_0000_0000;
        if (!TrySetWriteTime(future, farFuture) || !TrySetWriteTime(folder, farFuture))
        {
            Console.WriteLine("  note  could not set a write time past the year 9999 here; the walk is not checked");
            return;
        }

        var state = Path.Combine(root, "walk-state");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--only");
        start.ArgumentList.Add(nameof(TopReviewFixChecks));
        start.Environment[TopReviewWalkVariable] = walked;

        // A state folder of its own, whatever this run was given: the walk's
        // log is written there, and nowhere near the user's.
        start.Environment["ULTRAEXPLORER_STATE_DIR"] = state;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(90_000)))
        {
            process.Kill(entireProcessTree: true);
            Check("the copy that walks finishes within 90 s", false);
            return;
        }

        var lines = (await output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var survived = process.ExitCode == 0 && lines.Contains("walk: done");
        Check($"the process lives through a file and a folder dated past the year 9999, and a folder that cannot be listed at all (exit code {process.ExitCode})",
            survived);
        if (!survived)
        {
            foreach (var line in lines.Concat((await errors).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Take(8))
            {
                Console.WriteLine($"        {line}");
            }
        }

        Check("the file so dated is found, with no date", lines.Contains("hit: needle-future.txt, no date"));
        Check("so is the folder so dated", lines.Contains("hit: needle-folder, no date"));
        Check("the file beside it keeps its date", lines.Contains("hit: needle-plain.txt, dated"));
        Check("the walk goes on into the folders below", lines.Contains("hit: needle-deep.txt, dated"));
        var log = Path.Combine(state, "crash.log");
        Check("the folder that could not be listed is written to the log",
            File.Exists(log) && File.ReadAllText(log).Contains("search walk", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The second copy's part: walks <paramref name="walked"/> for "needle",
    /// and a folder whose path cannot even be opened, and prints what it found.
    /// A walk thread that dies ends this copy with exit code 3 - quietly, so
    /// that Windows offers no crash report or debugger for it.
    /// </summary>
    private static async Task TopReviewWalkChildAsync(string walked)
    {
        _ = TopReviewSetErrorMode(0x0001 | 0x0002);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"walk: a thread ended with {(e.ExceptionObject as Exception)?.GetType().Name}");
            Environment.Exit(3);
        };

        var walk = new FolderWalk(SearchQuery.Parse("needle"), 100);
        await walk.RunAsync(null, [Path.Combine(walked, "no\0such"), walked], [], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        foreach (var hit in walk.Drain().OrderBy(hit => hit.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"hit: {hit.Name}, {(hit.Modified is null ? "no date" : "dated")}");
        }

        Console.WriteLine("walk: done");
    }

    // ---- I002: the navigation pane's drives and folders, off the UI thread -------

    /// <summary>
    /// A window lists the drives, the known folders and the WSL distributions
    /// for its navigation pane as it starts.  Each is a question to the disk,
    /// which a drive mapped to a server that is off answers only after some
    /// twenty seconds.  Here every such question waits until the UI thread has
    /// been seen to answer, or two seconds have passed without it; then the
    /// pane must hold what it always did.
    /// </summary>
    private static async Task TopReviewSidebarChecksAsync(string root)
    {
        Section("navigation pane: a drive slow to answer does not hold up the UI thread (I002)");
        var folder = Path.Combine(root, "sidebar");
        Directory.CreateDirectory(folder);
        var workspace = Path.Combine(folder, "workspace.json");
        await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
        File.WriteAllText(workspace + ".marks.json", "{}");

        var dispatcher = Dispatcher.CurrentDispatcher;
        var ui = Thread.CurrentThread;
        var askedOnUi = new ConcurrentQueue<string>();
        using var asked = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var rendererBefore = GpuBootstrap.Preference;
        var exists = FileSystemService.FolderExists;
        FileSystemService.FolderExists = path =>
        {
            if (Thread.CurrentThread == ui)
            {
                askedOnUi.Enqueue(path);
            }

            asked.Set();
            released.Wait(TimeSpan.FromSeconds(15));
            return exists(path);
        };
        try
        {
            using var model = new MainViewModel(Path.Combine(folder, "view-all.json"), false, workspace);
            var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
            var initializing = model.InitializeAsync(folder);
            var uiAnswered = await answered;
            await initializing.WaitAsync(TimeSpan.FromSeconds(60));
            FileSystemService.FolderExists = exists;

            Check($"the drives, known folders and WSL are asked about off the UI thread ({askedOnUi.Count} asked on it)", askedOnUi.IsEmpty);
            Check("the UI thread answers while a drive is slow to say whether it is ready", uiAnswered);

            using var icons = new ShellIconService();
            var expected = new FileSystemService(icons);
            static string Line(FavoriteItemViewModel item) =>
                $"{item.Name}|{item.Path}|{item.Glyph}|{item.AccentHex}|{item.Kind}|{item.OpensInShell}|{item.Icon is null}";
            var drives = expected.GetDrives().Select(Line).ToArray();

            // The window does not wait for its pane's drives: they come in
            // once they have answered.
            await LiveWait(() => model.Drives.Select(Line).SequenceEqual(drives), 10_000);
            Check($"once they answer, the pane has the same drives in the same order, with the same names and icons ({model.Drives.Count} of {drives.Length})",
                drives.Length > 0 && model.Drives.Select(Line).SequenceEqual(drives));
            Check("the same known folders, Home first",
                model.HomeItems.Concat(model.QuickAccess.Where(item => !item.IsCustom)).Select(Line)
                    .SequenceEqual(expected.GetQuickAccess().Select(Line)));
            Check("and the same network places", model.NetworkLocations.Select(Line).SequenceEqual(expected.GetNetworkLocations().Select(Line)));
        }
        finally
        {
            released.Set();
            FileSystemService.FolderExists = exists;
            GpuBootstrap.UseSavedPreference(rendererBefore);
        }
    }

    // ---- I003: a folder window's Shell registration, off the UI thread -----------

    /// <summary>
    /// With folder replacement on, a window tells the Shell which folder it
    /// shows each time it moves, and looking that folder up is a question to
    /// the disk.  The registration is made with no window handle: a folder that
    /// is never there is never registered, so nothing of this reaches the Shell.
    /// </summary>
    private static async Task TopReviewRegistrationChecksAsync(string root)
    {
        Section("shell registration: a folder slow to answer is looked up off the UI thread, and not every second (I003)");
        var gone = Path.Combine(root, "registration", "gone");
        var other = Path.Combine(root, "registration", "other");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var ui = Thread.CurrentThread;
        var lookups = 0;
        var lookupsOnUi = 0;
        using var asked = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var registration = new FolderShellViewRegistration(0, dispatcher, _ => Task.FromResult(false));
        registration.FolderExists = path =>
        {
            Interlocked.Increment(ref lookups);
            if (Thread.CurrentThread == ui)
            {
                Interlocked.Increment(ref lookupsOnUi);
            }

            asked.Set();
            released.Wait(TimeSpan.FromSeconds(15));
            return false;
        };
        try
        {
            var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
            var navigating = registration.NavigateAsync(gone);
            var uiAnswered = await answered;
            var registered = await navigating.WaitAsync(TimeSpan.FromSeconds(20));
            Check($"the folder is looked up off the UI thread ({Volatile.Read(ref lookupsOnUi)} of {Volatile.Read(ref lookups)} on it)",
                Volatile.Read(ref lookups) > 0 && Volatile.Read(ref lookupsOnUi) == 0);
            Check("the UI thread answers while the folder is slow to", uiAnswered);
            Check("a folder that is not there is not registered", !registered && !registration.IsRegistered);

            // What the settings' refresh every second, and a second event for
            // the same move, used to do.
            var failed = Volatile.Read(ref lookups);
            for (var index = 0; index < 3; index++)
            {
                await registration.NavigateAsync(gone);
            }

            var settled = Volatile.Read(ref lookups);
            Check($"asked again at once for the folder that just failed, it is not looked up again ({settled - failed} more)", settled == failed);

            var waited = Stopwatch.StartNew();
            while (Volatile.Read(ref lookups) == settled && waited.Elapsed < TimeSpan.FromSeconds(6))
            {
                await Task.Delay(50);
            }

            Check($"it is tried again later by itself, while the window stays there ({waited.ElapsedMilliseconds} ms)",
                Volatile.Read(ref lookups) > settled);

            var before = Volatile.Read(ref lookups);
            await registration.NavigateAsync(other);
            Check("a move to another folder looks that one up at once", Volatile.Read(ref lookups) > before);

            registration.Dispose();
            var closed = Volatile.Read(ref lookups);
            await Task.Delay(2500);
            Check("once the registration is closed, nothing is looked up any more", Volatile.Read(ref lookups) == closed);
        }
        finally
        {
            released.Set();
        }
    }

    /// <summary>
    /// The integration's settings are read again every second, for a change
    /// made by another process, and each window answers a change by telling the
    /// Shell its folder again; so a change is raised only when there is one.
    /// A controller of its own, made the way the shared one is, so the check
    /// neither depends on nor disturbs the one the windows use.
    /// </summary>
    private static async Task TopReviewSettingsChangedChecksAsync()
    {
        Section("dialog integration: the settings are raised as changed only when they change (I003)");
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  note  needs a state folder of its own (ULTRAEXPLORER_STATE_DIR); not checked");
            return;
        }

        var path = DialogIntegrationStore.SettingsPath;
        var saved = File.Exists(path) ? File.ReadAllBytes(path) : null;
        DispatcherTimer? refresh = null;
        try
        {
            var controller = (DialogIntegrationController)Activator.CreateInstance(typeof(DialogIntegrationController), nonPublic: true)!;
            refresh = typeof(DialogIntegrationController).GetField("_refresh", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller) as DispatcherTimer;
            var raised = 0;
            controller.Changed += () => raised++;
            await Task.Delay(3500);
            Check($"left alone, three reads of the settings raise no change ({raised} raised)", raised == 0);

            var current = DialogIntegrationStore.Read();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(current with
            {
                ExcludedApplications = [.. current.ExcludedApplications, @"C:\TopReview\excluded.exe"]
            }));
            var waited = Stopwatch.StartNew();
            while (raised == 0 && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }

            Check("a change made from outside is still raised", raised > 0);
        }
        finally
        {
            refresh?.Stop();
            if (saved is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, saved);
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetErrorMode")]
    private static extern uint TopReviewSetErrorMode(uint mode);
}
