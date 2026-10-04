using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the 2026-10-04 review found in the main window and the app's start
/// (cluster k-window-app), and what was done about it.
/// A window opened for a folder flew out towards This PC before it landed on the folder (J013).
/// A close while a drop was still copying did nothing anybody could see (J102).
/// A new window on a 100 % monitor beside a 150 % primary opened in that monitor's corner (J158).
/// Several windows opened together opened on top of one another, and looked like one (J159).
/// A dialog worker or proxy drew its names with WPF and built its pipelines on its first frame (J025).
///
/// <para>The windows need the app, of which a process can only ever have the
/// one: these checks run in a process of their own, as the main window
/// review's do.  A window that is shown opens on a monitor that is not the
/// primary one, cloaked and never active; without a second monitor none is
/// shown.  The starts of the dialog roles run in processes of their own
/// again, whose dispatcher is never run: what the role posts to it - the role
/// itself - never starts.</para>
/// </summary>
internal static partial class Program
{
    /// <summary>The roles whose start <see cref="WindowAppRoleStartChecks"/> looks at, one process each.</summary>
    private static readonly string[] WindowAppProbedRoles = ["--dialog-worker", "--dialog-proxy", "--dialog-agent"];

    private static Task WindowAppReviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(WindowAppReviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(WindowAppReviewChecks));
            return Task.CompletedTask;
        }

        var arguments = Environment.GetCommandLineArgs();
        if (WindowAppProbedRoles.FirstOrDefault(arguments.Contains) is { } role)
        {
            WindowAppRoleStartProbe(role);
            return Task.CompletedTask;
        }

        WindowAppRoleStartChecks();
        RunOnSta("window and app review", WindowAppReviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task WindowAppReviewOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the window and app review checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        // The windows read the drives as the app does; what that throws on
        // the interface thread outside these checks is written down and
        // survived, rather than ending them half way.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerWindowAppReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Every window made from here on refuses activation.
            using (ActivationGuard.GuardWindowsCreated())
            {
                await WindowAppCloseDuringDropChecksAsync(root);
                await WindowAppFolderOpenChecksAsync(root);
                await WindowAppCentredChecksAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- J102: closed while a drop is still copying --------------------------------------

    /// <summary>
    /// A drop's copy can take minutes, and the window waits for it before it
    /// closes, as it must: the source's files are only there until the drop
    /// returns.  A close meanwhile said nothing - the close button and Alt+F4
    /// seemed to do nothing at all - and now says at once that the window
    /// closes when the drop has finished, which it then does.  The window is
    /// never shown.
    /// </summary>
    private static async Task WindowAppCloseDuringDropChecksAsync(string root)
    {
        Section("main window: closed while a drop is still copying (J102)");
        var folder = Path.Combine(root, "drop-close");
        Directory.CreateDirectory(folder);
        var main = new MainWindow(null, Path.Combine(root, "drop-close.workspace.json"));
        var shell = (MainViewModel)main.DataContext;
        var closed = false;
        main.Closed += (_, _) => closed = true;
        new WindowInteropHelper(main).EnsureHandle();
        await shell.InitializeAsync(folder);

        var transfer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? said = null;
        var openWhenSaid = false;
        _ = main.Dispatcher.BeginInvoke(() =>
        {
            // The close button, pressed while the drop below is copying.
            main.Close();
            said = shell.Toast.IsVisible ? shell.Toast.Message : null;
            openWhenSaid = !closed;
            _ = Task.Delay(300).ContinueWith(_ => transfer.TrySetResult(true), TaskScheduler.Default);
        }, DispatcherPriority.Background);

        // The drop, as the canvas hands it to the window: held until its copy ends.
        var dropped = ((INestedPaneHost)main).CompleteExternalDrop(() => transfer.Task);
        var clock = Stopwatch.StartNew();
        while (!closed && clock.ElapsedMilliseconds < 10_000)
        {
            await Task.Delay(20);
        }

        Check($"a close while a drop is copying says at once that the window closes when it is done (\"{said ?? "nothing"}\")",
            openWhenSaid && said is { } message && message.Contains("clos", StringComparison.OrdinalIgnoreCase));
        Check($"and the window closes once the drop has finished ({clock.ElapsedMilliseconds} ms)", dropped && closed);
    }

    // ---- J013: a folder window's first view ----------------------------------------------

    /// <summary>
    /// A window opened for a folder - every Explorer-replacement window - has
    /// no camera of its own to go back to, so its start framed all of This PC
    /// with a flight, from the folder outwards, while the folder was still on
    /// its way; the flight ran until the folder landed and the view snapped
    /// back to it.  The start leaves the camera on the folder now: no flight
    /// towards This PC is under way at any frame before the folder lands.
    /// (How much of the view the folder fills on those frames is written
    /// down, not judged: the canvas's very first frame, drawn before its
    /// camera is put on the folder, may show This PC either way.)
    /// </summary>
    private static async Task WindowAppFolderOpenChecksAsync(string root)
    {
        Section("folder window: opens on its folder, without flying out to This PC first (J013)");
        if (ReviewSecondMonitor() is null)
        {
            Check("no second monitor: no window is opened on the main one", true);
            return;
        }

        var folder = Path.Combine(root, "opened", "inner folder");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 12; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"file {index:D2}.txt"), "x");
        }

        Check("the folder can be opened", FolderCommandLine.TryOpenFolder(folder, out var open, out _));
        var id = Guid.NewGuid();
        var invocation = open with { DestinationId = id };
        var window = new MainWindow(null, ExplorerLaunchRouter.FolderWorkspacePath(id)) { WindowState = WindowState.Normal };
        window.FolderDestinationId = id;
        window.PrepareFolderInvocation(invocation);
        window.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
        var canvas = window.ActivePane.Canvas;
        var flightField = typeof(NestedCanvas).GetField("_flight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool FlyingToThisPc() => flightField.GetValue(canvas) is { } flight
            && flight.GetType().GetProperty("Target")?.GetValue(flight) is NestedFolder { IsComputer: true };

        var frames = 0;
        var outwardFrames = 0;
        var narrowFrames = 0;
        var narrowest = double.MaxValue;
        var landed = false;
        var outwardAtReady = false;
        var readySeen = false;
        var clock = Stopwatch.StartNew();
        double firstOutwardMs = -1;
        void Sample(object? sender, EventArgs e)
        {
            if (landed)
            {
                return;
            }

            frames++;
            if (FlyingToThisPc())
            {
                outwardFrames++;
                if (firstOutwardMs < 0) firstOutwardMs = clock.Elapsed.TotalMilliseconds;
            }

            if (!readySeen && window.IsFolderWindowReady)
            {
                readySeen = true;
                outwardAtReady = FlyingToThisPc();
            }

            // How much of the view the folder fills, once the canvas has a
            // camera of its own (asked before then, it would make one).
            if (canvas.CaptureCamera() is not null && window.ActivePane.Tree.Find(folder) is { } cell && canvas.ScreenRectOf(cell) is { } rect)
            {
                var fill = rect.Width / canvas.ActualWidth;
                narrowest = Math.Min(narrowest, fill);
                if (fill < 0.45) narrowFrames++;
            }
        }

        CompositionTarget.Rendering += Sample;
        bool opened;
        try
        {
            window.Show();
            opened = await window.ApplyFolderInvocationAsync(invocation).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            landed = true;
            CompositionTarget.Rendering -= Sample;
        }

        var landedMs = clock.Elapsed.TotalMilliseconds;
        try
        {
            Console.WriteLine($"        {frames} frames before the folder landed at {landedMs:0} ms: {outwardFrames} with a flight towards This PC"
                + $" (the first at {(firstOutwardMs < 0 ? "none" : $"{firstOutwardMs:0} ms")}), {narrowFrames} with the folder under 45 % of the view"
                + $" (narrowest {(narrowest == double.MaxValue ? "never seen" : $"{narrowest:P0}")})");
            Check("the folder window opens its folder", opened);
            Check("when the window is ready for its folder, no flight towards This PC is under way", readySeen && !outwardAtReady);
            Check($"no frame before the folder lands flies towards This PC ({outwardFrames} of {frames})", outwardFrames == 0);
            var camera = canvas.CaptureCamera();
            Check($"the camera ends on the folder ({camera?.AnchorPath}, {camera?.Width:0.00} of the view)",
                camera is { Width: >= 0.4 and <= 1.5 } && ViewAllPath.Equals(camera.AnchorPath, folder));
        }
        finally
        {
            await WindowAppCloseAsync(window);
        }
    }

    // ---- J158, J159: where a new window opens --------------------------------------------

    /// <summary>
    /// A new window opens in the middle of the work area of the monitor it
    /// starts on.  One WPF centred is left exactly where it put it.  One
    /// started on a 100 % monitor beside a 150 % primary was centred at the
    /// primary's scale - at one and a half times its size - shrunk to its
    /// own as it moved over, and pushed into the monitor's corner; it is
    /// centred at the size it has there now.  A second window opened while
    /// the first is in that spot - as several folders opened together are -
    /// opens a caption's height lower and further right, not exactly over
    /// it, where the two looked like one window.  Without a monitor that is
    /// not the primary none is opened.
    /// </summary>
    private static async Task WindowAppCentredChecksAsync()
    {
        Section("main window: where a new window opens (J158, J159)");
        if (ReviewSecondMonitor() is not { } second)
        {
            Check("no second monitor: no window is opened on the main one", true);
            return;
        }

        // As WPF centres a window: in the middle of the work area, within its rounding.
        var plain = ShowCentredReviewWindow(second, asScaleChangeLeavesIt: false);
        try
        {
            await SettingsSettle();
            var handle = new WindowInteropHelper(plain).Handle;
            var monitor = DialogNative.MonitorOf(handle);
            var bounds = DialogNative.WindowBounds(handle);
            Check($"a centred window started on the second monitor opens on it ({bounds} on {monitor?.Work})",
                monitor is { Primary: false } && bounds is { } inside && monitor.Value.Work.Contains(inside));
            Check($"in the middle of its work area, as WPF put it ({bounds})",
                monitor is { } on && bounds is { } placed && WindowAppIsCentred(placed, on.Work, exactly: false));
        }
        finally
        {
            // Gone, not saving on its way out, before the next one opens.
            await WindowAppCloseAsync(plain);
        }

        // J158: as a change of scale leaves it.
        var first = ShowCentredReviewWindow(second, asScaleChangeLeavesIt: true);
        MainWindow? next = null;
        try
        {
            await SettingsSettle();
            var handle = new WindowInteropHelper(first).Handle;
            var monitor = DialogNative.MonitorOf(handle);
            var bounds = DialogNative.WindowBounds(handle);
            if (monitor is not { } on || bounds is not { } placed)
            {
                Check("the window opened on a monitor", false);
                return;
            }

            Check($"a window centred at another scale and shrunk to its own is centred again, not pushed into the corner ({placed.Left},{placed.Top} {placed.Width}x{placed.Height} in {on.Work})",
                WindowAppIsCentred(placed, on.Work, exactly: true));

            // J159: a second window, opened while the first is there.
            next = ShowCentredReviewWindow(second, asScaleChangeLeavesIt: false);
            await SettingsSettle();
            var nextBounds = DialogNative.WindowBounds(new WindowInteropHelper(next).Handle);
            var step = (int)Math.Round(40 * on.Scale);
            Check($"a second window opened while the first is there is not exactly over it ({nextBounds})", nextBounds != placed);
            Check($"it is a caption's height ({step} px) lower and further right",
                nextBounds is { } shifted && shifted.Left == placed.Left + step && shifted.Top == placed.Top + step
                && shifted.Width == placed.Width && shifted.Height == placed.Height);
        }
        finally
        {
            await WindowAppCloseAsync(next);
            await WindowAppCloseAsync(first);
        }
    }

    /// <summary>Closes <paramref name="window"/> and waits until it has closed: a close saves first, and a window still saving is still there.</summary>
    private static async Task WindowAppCloseAsync(MainWindow? window)
    {
        if (window is null)
        {
            return;
        }

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        var clock = Stopwatch.StartNew();
        while (!closed && clock.ElapsedMilliseconds < 10_000)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Whether <paramref name="bounds"/> is in the middle of <paramref name="work"/>: to the pixel, or within WPF's rounding.</summary>
    private static bool WindowAppIsCentred(NativeRect bounds, NativeRect work, bool exactly)
    {
        var left = work.Left + (work.Width - bounds.Width) / 2;
        var top = work.Top + (work.Height - bounds.Height) / 2;
        var slack = exactly ? 0 : 1;
        return work.Contains(bounds) && Math.Abs(bounds.Left - left) <= slack && Math.Abs(bounds.Top - top) <= slack;
    }

    /// <summary>
    /// A window as anything but a test copy opens one: in the middle of the
    /// screen it starts on - created on the second monitor here, by asking
    /// for a place on it - cloaked from its first moment and never active.
    /// A test copy's own placement (PlaceForDiagnostics) is what is being
    /// left out, so the test-window switch is off while it is shown.
    /// <para>With <paramref name="asScaleChangeLeavesIt"/> the window is, by
    /// the time the app places it, where a window started on a 100 % monitor
    /// beside a 150 % primary is by then: centred at one and a half times its
    /// size, then shrunk to its own, its corner kept.  This program is aware
    /// of the system's scale only - the app is aware of each monitor's - so
    /// no window of it ever changes scale, and the move that change makes is
    /// made here by hand.</para>
    /// </summary>
    private static MainWindow ShowCentredReviewWindow(MainWindow.MonitorInfo second, bool asScaleChangeLeavesIt)
    {
        var window = new MainWindow { WindowState = WindowState.Normal };
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var scale = WindowAppSystemDpi() / 96.0;
        window.Left = (second.Work.Left + second.Work.Right) / 2.0 / scale;
        window.Top = (second.Work.Top + second.Work.Bottom) / 2.0 / scale;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            DialogNative.CloakOwn(handle, true);
            if (asScaleChangeLeavesIt && DialogNative.MonitorOf(handle) is { } monitor && DialogNative.WindowBounds(handle) is { } made)
            {
                var left = monitor.Work.Left + (int)Math.Round((monitor.Work.Width - made.Width * 1.5) / 2);
                var top = monitor.Work.Top + (int)Math.Round((monitor.Work.Height - made.Height * 1.5) / 2);
                DialogNative.Place(handle, new NativeRect(left, top, left + made.Width, top + made.Height));
            }
        };
        var testWindow = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", null);
        try
        {
            window.Show();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testWindow);
        }

        return window;
    }

    // ---- J025: a dialog role's GPU ---------------------------------------------------------

    /// <summary>
    /// A dialog worker or proxy shows the canvas as the app's window does, so
    /// its start readies the GPU as the app's does, before the role runs: the
    /// label atlases and the card's pipelines are warmed, and a dialog draws
    /// its names from the atlases from its first frame.  It returned before
    /// that, and the GPU then started lazily, without the atlases - every
    /// name was drawn with WPF - and with its pipelines made on the first
    /// frame.  A role that shows nothing, the agent, still makes no device.
    /// </summary>
    private static void WindowAppRoleStartChecks()
    {
        Section("dialog roles: their start readies the GPU the canvas draws with (J025)");
        foreach (var role in WindowAppProbedRoles)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "--only", nameof(WindowAppReviewChecks), role, "window-app-review-probe" })
            {
                start.ArgumentList.Add(argument);
            }

            using var child = Process.Start(start)!;
            child.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line) Console.WriteLine($"  | {line}");
            };
            child.BeginOutputReadLine();
            var ended = child.WaitForExit(TimeSpan.FromMinutes(2));
            if (!ended) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            Check($"the {role} start probe passed in a process of its own", ended && child.ExitCode == 0);
        }
    }

    /// <summary>
    /// The app's own start with this process's command line - a dialog role -
    /// on a thread whose dispatcher is never run, so the role it posts there
    /// never starts: what the start itself did is all there is to see.
    /// </summary>
    private static void WindowAppRoleStartProbe(string role)
    {
        Section($"the start of {role}");
        var drawsCanvas = role is "--dialog-worker" or "--dialog-proxy";
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var startup = (StartupEventArgs)Activator.CreateInstance(typeof(StartupEventArgs), nonPublic: true)!;
                Check($"the start is given the role ({string.Join(' ', startup.Args)})", startup.Args.Contains(role));
                var clock = Stopwatch.StartNew();
                typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [startup]);
                var started = clock.Elapsed.TotalMilliseconds;
                if (!drawsCanvas)
                {
                    Check($"{role} shows no canvas and makes no device", !GpuBootstrap.IsStarted && !GpuLabelAtlases.IsStarted);
                    return;
                }

                if (SystemParameters.IsRemoteSession || RenderCapability.Tier >> 16 < 2)
                {
                    Check("this session draws the canvas on the CPU: nothing of the GPU to ready", true);
                    return;
                }

                Check($"by the time {role} runs, the GPU is warming up with the label atlases (start took {started:0} ms)",
                    GpuBootstrap.IsStarted && GpuLabelAtlases.IsStarted);
                if (!GpuBootstrap.IsStarted)
                {
                    // As before: nothing is warmed until a canvas first draws, and then without the atlases.
                    Console.WriteLine($"        label atlases: {(GpuLabelAtlases.IsStarted ? "started" : "never started")}, current {(GpuLabelAtlases.Current is null ? "none (names drawn with WPF)" : "ready")}");
                    return;
                }

                var atlases = GpuLabelAtlases.Ready.Wait(TimeSpan.FromSeconds(30)) ? GpuLabelAtlases.Ready.Result : null;
                var atlasesMs = clock.Elapsed.TotalMilliseconds;
                var set = GpuBootstrap.Ready.Wait(TimeSpan.FromSeconds(30)) ? GpuBootstrap.Ready.Result : null;
                var setMs = clock.Elapsed.TotalMilliseconds;
                Console.WriteLine($"        label atlases ready at {atlasesMs:0} ms, first device set ready at {setMs:0} ms: {set?.Description ?? GpuBootstrap.LastFailure}");
                Check("the label atlases are made", atlases is not null && GpuLabelAtlases.Current is not null);
                Check("and the first card is handed out with its copies of them, so names are drawn from them from the first frame",
                    set is not null && set.TryGetAttached<GlyphAtlasTexture>(out _) && set.TryGetAttached<IconAtlasTexture>(out _));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "role start probe"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check("the start probe finished within two minutes", thread.Join(TimeSpan.FromMinutes(2)));
        if (failure is not null)
        {
            Console.WriteLine($"  FATAL {failure}");
            Check("the start probe ran to the end", false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    private static extern uint WindowAppSystemDpi();
}
