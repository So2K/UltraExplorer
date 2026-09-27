using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// Two developer switches for the nested canvas, so a change to it can be
/// judged by numbers and by pixels rather than by feel:
///
/// <list type="bullet">
/// <item><c>--nested-bench &lt;file.tsv&gt;</c> flies a fixed route - the whole
/// PC, into a big system folder, a pan, back out, a folder of thousands of
/// files - timing every frame, then times selecting a few deep folders, writes
/// the numbers and exits.  <c>--bench-route &lt;name&gt;</c> flies another
/// route instead (see <see cref="BenchRoutes"/>).</item>
/// <item><c>--nested-snapshots &lt;folder&gt;</c> renders a fixed set of views to
/// PNG once everything in them has been read, and exits.  Two builds' PNGs
/// compared pixel for pixel say whether a change altered the picture.</item>
/// </list>
///
/// Both force the nested canvas and a fixed window size.  Run them with
/// <c>ULTRAEXPLORER_STATE_DIR</c> pointing at an empty folder so neither
/// the session nor the user's workspace colours the result.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] BenchDeepFolders =
    [
        @"C:\Windows\System32\drivers\etc",
        @"C:\Windows\System32\DriverStore",
        @"C:\Program Files\Common Files",
        @"C:\Windows\Fonts",
        @"C:\Windows\WinSxS"
    ];

    /// <summary>The orders the bench clicks through, ending where it started.</summary>
    private static readonly ItemSort[] BenchSorts =
    [
        new(SortColumn.Size, true),
        new(SortColumn.Modified, true),
        new(SortColumn.Type, false),
        ItemSort.Default
    ];

    /// <summary>What the clicks on the sort headers cost the UI thread in the bench.</summary>
    private sealed class SortSwitchStats
    {
        public int Clicks { get; private set; }

        /// <summary>Clicks after which folders were still left for the background pass.</summary>
        public int Deferred { get; private set; }

        public double WorstMilliseconds { get; private set; }

        /// <summary>The worst of the nested canvas's own part: the tree's change and the canvas holding its view.</summary>
        public double WorstCanvasMilliseconds { get; private set; }

        public void Switch(MainWindow window, ItemSort sort)
        {
            // One change of the orders the window shares reaches everything
            // a click does - the canvas's tree, the tree view, the list, the
            // headers - so the whole is timed here and the canvas's part is
            // the tree's own measure of its handler.
            var watch = Stopwatch.StartNew();
            window._viewModel.Sort = sort;
            watch.Stop();
            WorstCanvasMilliseconds = Math.Max(WorstCanvasMilliseconds, window._nestedTree.LastOrderChangeMilliseconds);
            Clicks++;
            if (window._nestedTree.IsSorting)
            {
                Deferred++;
            }

            WorstMilliseconds = Math.Max(WorstMilliseconds, watch.Elapsed.TotalMilliseconds);
        }
    }

    private static string? SwitchValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// The diagnostics window's size in DIPs: 1600 x 1000, or what
    /// <c>--bench-window 1920x1040</c> asks for.
    /// </summary>
    private static (double Width, double Height) BenchWindowSize =>
        SwitchValue("--bench-window") is { } size
        && size.Split('x', 'X') is [var width, var height]
        && double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
        && double.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
        && w >= 200 && h >= 200
            ? (w, h)
            : (1600, 1000);

    private static bool BenchWindowAsked => SwitchValue("--bench-window") is not null;


    private static double BenchNumber(string name) =>
        SwitchValue(name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0.1 && value < 8
            ? value
            : 1;

    /// <summary>
    /// The user's screen on a small one, for the bench: <c>--bench-scale 1.5</c>
    /// draws the canvas at 1.5 times the monitor's pixels per DIP, as a 150 %
    /// monitor would, and <c>--bench-layout 0.742</c> shrinks it on screen by
    /// a layout transform so a canvas of the user's size in DIPs fits the
    /// window.  Together with <c>--bench-window 1920x1040</c> on the 100 %
    /// monitor that is the pixel count of a maximised window at 3840 x 2160
    /// and 150 % - the size at which the canvas was measured to lag.
    /// </summary>
    private void EmulateBenchScale()
    {
        var layout = BenchNumber("--bench-layout");
        if (layout != 1)
        {
            Nested.LayoutTransform = new ScaleTransform(layout, layout);
        }

        var boost = BenchNumber("--bench-scale");
        if (boost != 1)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Nested.DpiOverride = new DpiScale(dpi.DpiScaleX * boost, dpi.DpiScaleY * boost);
        }
    }

    /// <summary>True when this process was started for a benchmark or snapshot run.</summary>
    private static bool IsDiagnosticsRun =>
        SwitchValue("--nested-bench") is not null || SwitchValue("--nested-snapshots") is not null;

    /// <summary>
    /// True for a copy started beside the everyday one to try a build
    /// (<c>ULTRAEXPLORER_TEST_WINDOW=1</c>): it opens where a diagnostics run
    /// would, without taking the keyboard, but is an ordinary window after that.
    /// </summary>
    private static bool IsTestWindow =>
        Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";

    /// <summary>
    /// Puts a benchmark or snapshot window where nobody is looking, before it
    /// is first shown.  A run takes minutes and must not cover or steal focus
    /// from what the user is doing on the main screen, so it opens on the
    /// first monitor that is not the primary one - or on the one
    /// <c>ULTRAEXPLORER_DIAGNOSTICS_MONITOR</c> names by its position in
    /// Windows' list - and only falls back to the primary monitor when there
    /// is no other.  Always the same corner and size, so runs stay comparable.
    /// A test window goes to the same place but can still be clicked into.
    /// </summary>
    private static void PlaceForDiagnostics(IntPtr handle, bool neverActivate)
    {
        if (DiagnosticsMonitor() is not { } chosen)
        {
            return;
        }

        var (target, work) = chosen;

        // Never the active window: not when it opens, not when it restores
        // itself from maximised, not if clicked.  The run needs no keyboard.
        if (neverActivate)
        {
            SetWindowLongPtr(handle, ExtendedStyleIndex, GetWindowLongPtr(handle, ExtendedStyleIndex) | NoActivateStyle);
        }

        var scale = GetDpiForMonitor(target, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var (windowWidth, windowHeight) = BenchWindowSize;
        var inset = BenchWindowAsked ? 0 : 8;
        var width = Math.Min((int)Math.Round(windowWidth * scale), work.Right - work.Left);
        var height = Math.Min((int)Math.Round(windowHeight * scale), work.Bottom - work.Top);
        SetWindowPos(handle, IntPtr.Zero, work.Left + inset, work.Top + inset, width, height, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// The monitor a diagnostics or test window opens on (see
    /// <see cref="PlaceForDiagnostics"/>) and its work area; null when
    /// Windows lists none.
    /// </summary>
    private static (IntPtr Monitor, RectL Work)? DiagnosticsMonitor()
    {
        var monitors = new List<(IntPtr Handle, RectL Work, bool IsPrimary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add((monitor, info.Work, (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            return null;
        }

        var chosen = monitors.FindIndex(monitor => !monitor.IsPrimary);
        if (int.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIAGNOSTICS_MONITOR"), out var asked)
            && asked >= 0 && asked < monitors.Count)
        {
            chosen = asked;
        }

        var (handle, work, _) = monitors[Math.Max(0, chosen)];
        return (handle, work);
    }

    /// <summary>
    /// The monitor the main window will open on, known before it exists, so
    /// the GPU warm-up prepares the card that drives it: a diagnostics or
    /// test window's own monitor, otherwise the one under the mouse, which is
    /// where WPF centres a window that opens in the middle of the screen.
    /// </summary>
    internal static IntPtr StartupMonitor()
    {
        if ((IsDiagnosticsRun || IsTestWindow) && DiagnosticsMonitor() is { } chosen)
        {
            return chosen.Monitor;
        }

        return GetCursorPos(out var cursor) ? MonitorFromPoint(cursor, MonitorDefaultToNearest) : IntPtr.Zero;
    }

    private const int MonitorInfoPrimary = 1;
    private const int ExtendedStyleIndex = -20;
    private const nint NoActivateStyle = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(CursorPoint point, int flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);

    /// <summary>Starts a benchmark or snapshot run if one was asked for; true when it did.</summary>
    private bool TryStartNestedDiagnostics()
    {
        var bench = SwitchValue("--nested-bench");
        var snapshots = SwitchValue("--nested-snapshots");
        if (bench is null && snapshots is null)
        {
            return false;
        }

        // Always the same monitor, place and size (PlaceForDiagnostics put the
        // window there before it was shown): two runs on monitors of different
        // scale are different pictures and different amounts of work, and
        // could not be compared.
        WindowState = WindowState.Normal;
        (Width, Height) = BenchWindowSize;
        EmulateBenchScale();
        _viewModel.Layout = CanvasLayout.Nested;
        _viewModel.Tree.FolderList.IsVisible = false;
        if (bench is not null)
        {
            ApplyBenchReadDelay();
        }

        // The bench runs at Normal priority: every await in a route resumes at
        // the priority of the operation that started it, and at ApplicationIdle
        // - below all Background work - a route's own steps waited behind the
        // very work it was measuring.  The snapshots keep the old priority,
        // which their pictures were made with.
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (bench is not null)
                {
                    await RunBenchRouteAsync(bench);
                }
                else
                {
                    await RunNestedSnapshotsAsync(snapshots!);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText((bench ?? Path.Combine(snapshots!, "error")) + ".error.txt", ex.ToString());
            }
            finally
            {
                _benchProbes?.Dispose();
                Application.Current.Shutdown();
            }
        }, bench is not null ? DispatcherPriority.Normal : DispatcherPriority.ApplicationIdle);
        return true;
    }

    /// <summary>
    /// The routes the bench can fly, by the name <c>--bench-route</c> gives:
    /// "default", the route of <see cref="RunNestedBenchAsync"/>, and whichever
    /// others are built in - each in a file of its own, which adds itself
    /// here.  A route writes its report to the file it is handed, and its
    /// frames beside it.
    /// </summary>
    private Dictionary<string, Func<string, Task>> BenchRoutes()
    {
        var routes = new Dictionary<string, Func<string, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = RunNestedBenchAsync
        };

        AddStreamBenchRoute(routes);
        AddLiveBenchRoute(routes);
        AddCameraBenchRoute(routes);
        return routes;
    }

    /// <summary>Adds the "stream" route: folders streaming in at 4K (MainWindow.BenchStream.cs).</summary>
    partial void AddStreamBenchRoute(Dictionary<string, Func<string, Task>> routes);

    /// <summary>Adds the "live" route: changes on disk while the canvas shows them (MainWindow.BenchLive.cs).</summary>
    partial void AddLiveBenchRoute(Dictionary<string, Func<string, Task>> routes);

    /// <summary>Adds the "camera" route: eased wheel, key and drag motion (MainWindow.BenchCamera.cs).</summary>
    partial void AddCameraBenchRoute(Dictionary<string, Func<string, Task>> routes);

    /// <summary>Flies the route <c>--bench-route</c> names - the default one without it - into <paramref name="output"/>.</summary>
    private async Task RunBenchRouteAsync(string output)
    {
        var name = SwitchValue("--bench-route") ?? "default";
        var routes = BenchRoutes();
        if (!routes.TryGetValue(name, out var route))
        {
            throw new ArgumentException($"No bench route is called \"{name}\"; there are: {string.Join(", ", routes.Keys)}.");
        }

        await route(output);
    }

    private Task NextFrameAsync()
    {
        var done = new TaskCompletionSource();
        void OnFrame(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            done.TrySetResult();
        }

        CompositionTarget.Rendering += OnFrame;
        return done.Task;
    }

    /// <summary>
    /// Waits until nothing is being read, no folder is left to place for a
    /// change of order, and nothing has changed for a moment.
    /// </summary>
    private async Task<double> SettleAsync(double timeoutSeconds = 30)
    {
        var watch = Stopwatch.StartNew();
        var quietSince = watch.Elapsed;
        var lastRenders = Nested.RenderCount;
        while (watch.Elapsed.TotalSeconds < timeoutSeconds)
        {
            Nested.Redraw();
            await NextFrameAsync();
            await Task.Delay(30);
            if (_nestedTree.PendingCount > 0
                || _nestedTree.IsSorting
                || Nested.HasPendingWork
                || Nested.RenderCount != lastRenders && _treeChangedRecently)
            {
                quietSince = watch.Elapsed;
            }

            _treeChangedRecently = false;
            lastRenders = Nested.RenderCount;
            if ((watch.Elapsed - quietSince).TotalMilliseconds > 600)
            {
                break;
            }
        }

        return watch.Elapsed.TotalMilliseconds;
    }

    private bool _treeChangedRecently;

    /// <summary>
    /// With a GPU renderer in force, waits - ten seconds at most - for the
    /// card and the names' atlases to be ready and for the canvas to draw on
    /// them, so a run's first scenes and phases are not drawn by the CPU
    /// raster while the rest are on the GPU: a slow warm-up (a cold shader
    /// cache, a glyph cache miss, a slower card) would otherwise put CPU
    /// pictures into a GPU baseline with nothing to say so.  Returns what the
    /// canvas then draws with (<see cref="RendererState"/>).
    /// </summary>
    private async Task<string> WaitForRendererAsync()
    {
        if (Rendering.Gpu.GpuBootstrap.Preference != Rendering.Gpu.RendererPreference.Cpu && Rendering.Gpu.GpuBootstrap.IsStarted)
        {
            var limit = Task.Delay(TimeSpan.FromSeconds(10));
            await Task.WhenAny(Rendering.Gpu.GpuBootstrap.Ready, limit);
            if (Rendering.Gpu.GpuLabelAtlases.IsStarted)
            {
                await Task.WhenAny(Rendering.Gpu.GpuLabelAtlases.Ready, limit);
            }

            var watch = Stopwatch.StartNew();
            var cardReady = Rendering.Gpu.GpuBootstrap.Ready.IsCompletedSuccessfully && Rendering.Gpu.GpuBootstrap.Ready.Result is not null;
            var atlasesReady = Rendering.Gpu.GpuLabelAtlases.Current is not null;
            while (cardReady
                && !(Nested.IsSceneOnGpu && (Nested.AreLabelsOnGpu || !atlasesReady))
                && watch.Elapsed.TotalSeconds < 5)
            {
                Nested.Redraw();
                await NextFrameAsync();
            }
        }

        return RendererState();
    }

    /// <summary>What the canvas's last frame drew with: the scene's renderer, the names', and why.</summary>
    private string RendererState() =>
        $"{(Nested.IsSceneOnGpu ? "gpu" : "cpu")}\t{(Nested.AreLabelsOnGpu ? "gpu" : "wpf")}\t{Nested.RendererReason}";

    private void TrackTreeChanges() => _nestedTree.Changed += (_, _) => _treeChangedRecently = true;

    private sealed class PhaseStats(string name)
    {
        public string Name { get; } = name;
        public List<double> Render { get; } = [];
        public List<double> Interval { get; } = [];
        public List<int> Cells { get; } = [];
        public List<double> Labels { get; } = [];
        public List<double> Layouts { get; } = [];

        // What the frame cost the UI thread in all, how the scene's part
        // splits between the walk and the present, what the GPU did, and
        // the garbage left - the columns added with the GPU renderer.
        public List<double> Frame { get; } = [];
        public List<double> Walk { get; } = [];
        public List<double> Present { get; } = [];
        public List<double> Lock { get; } = [];
        public List<double> GpuWait { get; } = [];
        public List<double> Gpu { get; } = [];
        public long AllocatedBytes { get; set; }
        public int Gen2 { get; set; }
        public int Skipped { get; set; }

        // The names' part, sending to the card, the other collections, and
        // the threads' own clocks: how much CPU the UI thread and WPF's
        // render thread spent per frame of the phase, whoever asked for it.
        public List<double> Upload { get; } = [];
        public List<int> Glyphs { get; } = [];
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public double UiCpuPerFrame { get; set; }
        public double RenderCpuPerFrame { get; set; }
        public bool LabelsOnGpu { get; set; }
        public long SceneAllocated { get; set; }
        public long LabelsAllocated { get; set; }
        public long DecorAllocated { get; set; }
        public long PresentAllocated { get; set; }
        public double GcPauseMilliseconds { get; set; }
        public long HeapBytes { get; set; }

        // Which renderer drew the phase's frames: a phase that began on the
        // CPU and moved to the GPU half way says so here.
        public int SceneGpuFrames { get; set; }
        public int LabelsGpuFrames { get; set; }

        // What the canvas did with the folders read during the phase: the
        // redraws they had, the frames that held one back to loading's rate,
        // the CPU scenes that painted only the changed cells, how often the
        // names were drawn, and the batches with nothing on screen.  For a
        // rest, how long until the canvas's loop let go.
        public long LoadRedraws { get; set; }
        public long LoadRedrawsHeld { get; set; }
        public long ClippedScenes { get; set; }
        public long LabelLayers { get; set; }
        public long UnseenBatches { get; set; }
        public double RestMilliseconds { get; set; } = double.NaN;

        /// <summary>The columns after <see cref="Row"/>'s, in the order of <see cref="BenchPhaseHeader"/>'s tail.</summary>
        public string ScopeColumns()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join('\t',
                LoadRedraws.ToString(inv),
                LoadRedrawsHeld.ToString(inv),
                ClippedScenes.ToString(inv),
                LabelLayers.ToString(inv),
                UnseenBatches.ToString(inv),
                double.IsNaN(RestMilliseconds) ? "-" : RestMilliseconds.ToString("0", inv));
        }

        public string Row()
        {
            static double P(List<double> values, double p)
            {
                if (values.Count == 0)
                {
                    return 0;
                }

                var sorted = values.OrderBy(value => value).ToList();
                return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
            }

            var inv = CultureInfo.InvariantCulture;
            return string.Join('\t',
                Name,
                Render.Count.ToString(inv),
                (Render.Count == 0 ? 0 : Render.Average()).ToString("0.00", inv),
                P(Render, 0.5).ToString("0.00", inv),
                P(Render, 0.95).ToString("0.00", inv),
                (Render.Count == 0 ? 0 : Render.Max()).ToString("0.00", inv),
                (Interval.Count == 0 ? 0 : Interval.Average()).ToString("0.00", inv),
                P(Interval, 0.95).ToString("0.00", inv),
                (Cells.Count == 0 ? 0 : Cells.Average()).ToString("0", inv),
                Interval.Count(value => value > 20).ToString(inv),
                (Interval.Count == 0 ? 0 : Interval.Max()).ToString("0.0", inv),
                (Labels.Count == 0 ? 0 : Labels.Average()).ToString("0.00", inv),
                (Labels.Count == 0 ? 0 : Labels.Max()).ToString("0.00", inv),
                (Layouts.Count == 0 ? 0 : Layouts.Average()).ToString("0.0", inv),
                (Layouts.Count == 0 ? 0 : Layouts.Max()).ToString("0", inv),
                P(Frame, 0.5).ToString("0.00", inv),
                P(Frame, 0.95).ToString("0.00", inv),
                P(Walk, 0.5).ToString("0.00", inv),
                P(Present, 0.5).ToString("0.00", inv),
                P(Lock, 0.5).ToString("0.00", inv),
                P(GpuWait, 0.5).ToString("0.00", inv),
                P(Gpu, 0.5).ToString("0.000", inv),
                (Render.Count == 0 ? 0 : AllocatedBytes / 1024.0 / Render.Count).ToString("0.0", inv),
                Gen2.ToString(inv),
                Skipped.ToString(inv),
                P(Interval, 0.5).ToString("0.00", inv),
                P(Labels, 0.5).ToString("0.00", inv),
                P(Upload, 0.5).ToString("0.00", inv),
                (Glyphs.Count == 0 ? 0 : Glyphs.Average()).ToString("0", inv),
                Gen0.ToString(inv),
                Gen1.ToString(inv),
                UiCpuPerFrame.ToString("0.00", inv),
                RenderCpuPerFrame.ToString("0.00", inv),
                LabelsOnGpu ? "gpu" : "wpf",
                (Render.Count == 0 ? 0 : SceneAllocated / 1024.0 / Render.Count).ToString("0.0", inv),
                (Render.Count == 0 ? 0 : LabelsAllocated / 1024.0 / Render.Count).ToString("0.0", inv),
                (Render.Count == 0 ? 0 : DecorAllocated / 1024.0 / Render.Count).ToString("0.0", inv),
                (Render.Count == 0 ? 0 : PresentAllocated / 1024.0 / Render.Count).ToString("0.0", inv),
                GcPauseMilliseconds.ToString("0.0", inv),
                (HeapBytes / 1024.0 / 1024.0).ToString("0", inv),
                SceneGpuFrames.ToString(inv),
                LabelsGpuFrames.ToString(inv));
        }
    }

    /// <summary>
    /// The CPU time of the UI thread and of WPF's render thread, read from
    /// the threads' own cycle counters, for the bench: a phase's render-thread
    /// cost per frame is what the WPF side of a frame costs - composing the
    /// layers, the D3DImage's copy, the text WPF still draws - and it runs in
    /// parallel with the UI thread, so neither clock alone says it.  The
    /// render thread is the one started in wpfgfx (all of them, should WPF
    /// have more than one).  Cycles become milliseconds by a count taken over
    /// 50 ms of spinning when the bench starts.
    /// </summary>
    private static class BenchThreadClock
    {
        private const uint QueryInformation = 0x0040;
        private const uint QueryLimitedInformation = 0x0800;
        private static IntPtr _ui;
        private static readonly List<IntPtr> RenderThreads = [];
        private static double _cyclesPerMillisecond = 1;

        public readonly record struct Sample(ulong Ui, ulong Render);

        /// <summary>The render threads found, by id, or why none was.</summary>
        public static string RenderThreadName { get; private set; } = "not looked for";

        public static void Start()
        {
            if (_ui != IntPtr.Zero)
            {
                return;
            }

            _ui = OpenThread(QueryLimitedInformation, false, GetCurrentThreadId());
            var found = new List<string>();
            using (var process = Process.GetCurrentProcess())
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    var probe = OpenThread(QueryInformation | QueryLimitedInformation, false, (uint)thread.Id);
                    if (probe == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (NtQueryInformationThread(probe, 9, out var start, IntPtr.Size, IntPtr.Zero) == 0
                        && ModuleOf(start).StartsWith("wpfgfx", StringComparison.OrdinalIgnoreCase))
                    {
                        RenderThreads.Add(probe);
                        found.Add(thread.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        CloseHandle(probe);
                    }
                }
            }

            RenderThreadName = found.Count == 0 ? "no wpfgfx thread found" : "wpfgfx thread " + string.Join(", ", found);

            // Cycles per millisecond: this thread spins and both clocks are read.
            QueryThreadCycleTime(_ui, out var before);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < 50)
            {
            }

            QueryThreadCycleTime(_ui, out var after);
            _cyclesPerMillisecond = Math.Max(1, (after - before) / watch.Elapsed.TotalMilliseconds);
        }

        public static Sample Read()
        {
            ulong ui = 0, render = 0;
            if (_ui != IntPtr.Zero)
            {
                QueryThreadCycleTime(_ui, out ui);
            }

            foreach (var thread in RenderThreads)
            {
                if (QueryThreadCycleTime(thread, out var cycles))
                {
                    render += cycles;
                }
            }

            return new Sample(ui, render);
        }

        public static double Milliseconds(ulong cycles) => cycles / _cyclesPerMillisecond;

        private static string ModuleOf(IntPtr address)
        {
            if (address == IntPtr.Zero || !GetModuleHandleExW(0x4 | 0x2, address, out var module) || module == IntPtr.Zero)
            {
                return string.Empty;
            }

            var name = new StringBuilder(260);
            GetModuleFileNameW(module, name, name.Capacity);
            return Path.GetFileName(name.ToString());
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint id);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(IntPtr thread, int informationClass, out IntPtr information, int length, IntPtr returned);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileNameW(IntPtr module, StringBuilder name, int size);
    }

    /// <summary>
    /// Every frame of the bench, one line each, for looking into the frames a
    /// summary row cannot explain: written beside the report.  Its room is
    /// taken when the bench starts, so logging a frame does not add to the
    /// garbage the frame is measured for.
    /// </summary>
    private StringBuilder _benchFrames = new();

    /// <summary>
    /// The frames file's columns: what the bench saw each frame, then the
    /// canvas's own account of its last frame (<see cref="Controls.FrameStats"/>, phase
    /// by phase, and which layers it drew), whether its loop was hooked, and
    /// the CPU the UI thread and WPF's render thread spent since the row before.
    /// </summary>
    private const string BenchFramesHeader = "phase\tframe\tinterval_ms\trendered\tui_ms\twalk_ms\tlabels_ms\tdecor_ms\tpresent_ms\tlock_ms\tgpu_wait_ms\tupload_ms\tgen0\tgen1\tgen2\talloc_kb\tcells\tglyphs"
        + "\tloop_frame\tfresh\tcamera_ms\thub_ms\ticon_ms\tapply_ms\tcapture_ms\ttransition_ms\tcanvas_ms\thub_items\tapplied\ttransitions\tcamera_active\tskipped\tlayers\tclip_px\thooked\tui_cpu_ms\trender_cpu_ms\n";

    /// <summary>
    /// The summary's columns, one row per phase: <see cref="PhaseStats.Row"/>'s,
    /// then <see cref="PhaseStats.ScopeColumns"/>, then the probes'
    /// (<see cref="BenchProbes.Columns"/>).  Every route writes this header, so
    /// one script reads them all.
    /// </summary>
    private const string BenchPhaseHeader = "phase\tframes\tmean_ms\tp50_ms\tp95_ms\tmax_ms\tinterval_ms\tinterval_p95\tcells\thitches_20ms\tworst_gap_ms\tlabels_ms\tlabels_max\tnew_text\tnew_text_max"
        + "\tui_p50_ms\tui_p95_ms\twalk_p50_ms\tpresent_p50_ms\tlock_p50_ms\tgpu_wait_p50_ms\tgpu_p50_ms\talloc_kb_per_frame\tgen2\tskipped"
        + "\tinterval_p50\tlabels_p50_ms\tupload_p50_ms\tglyphs\tgen0\tgen1\tui_cpu_ms\trender_cpu_ms\tlabels_on\tscene_kb\tlabels_kb\tdecor_kb\tpresent_kb\tgc_pause_ms\theap_mb\tscene_gpu_frames\tlabels_gpu_frames"
        + "\tload_redraws\tload_held\tclipped_scenes\tlabel_layers\tunseen_batches\trest_ms"
        + "\tinput_probes\tinput_p50_ms\tinput_p95_ms\tinput_max_ms\tinput_over_100\tbackground_probes\tbackground_max_ms\tdispatcher_ops";

    /// <summary>A phase's summary row, with the probes' columns: what every route writes per phase.</summary>
    private string BenchPhaseRow(PhaseStats phase) =>
        phase.Row() + "\t" + phase.ScopeColumns() + "\t" + (_benchProbes?.Columns(phase.Name) ?? BenchProbes.NoColumns);

    private long _benchAllocated;
    private BenchThreadClock.Sample _benchFrameCpu;

    /// <summary>The layers a frame drew, as letters: Scene, Labels, Decor, Overlay.</summary>
    private static string LayerLetters(Controls.NestedCanvas.Layers layers) =>
        layers == Controls.NestedCanvas.Layers.None
            ? "-"
            : string.Concat(
                (layers & Controls.NestedCanvas.Layers.Scene) != 0 ? "S" : "",
                (layers & Controls.NestedCanvas.Layers.Labels) != 0 ? "L" : "",
                (layers & Controls.NestedCanvas.Layers.Decor) != 0 ? "D" : "",
                (layers & Controls.NestedCanvas.Layers.Overlay) != 0 ? "O" : "");

    /// <summary>One line of the frames file (<see cref="BenchFramesHeader"/>) for the frame WPF is starting now.</summary>
    private void AppendBenchFrame(string name, int frame, double interval, bool rendered)
    {
        var threadAllocated = GC.GetAllocatedBytesForCurrentThread();
        var stats = Nested.LastFrameStats;
        var clip = Nested.LastSceneClip;
        var cpu = BenchThreadClock.Read();
        var uiCpu = BenchThreadClock.Milliseconds(cpu.Ui - _benchFrameCpu.Ui);
        var renderCpu = BenchThreadClock.Milliseconds(cpu.Render - _benchFrameCpu.Render);
        _benchFrameCpu = cpu;
        _benchFrames.Append(CultureInfo.InvariantCulture,
            $"{name}\t{frame}\t{interval:0.00}\t{(rendered ? 1 : 0)}\t{Nested.LastFrameMilliseconds:0.00}\t{Nested.LastWalkMilliseconds:0.00}\t{Nested.LastLabelsMilliseconds:0.00}\t{Nested.LastDecorMilliseconds:0.00}\t{Nested.LastPresentMilliseconds:0.00}\t{Nested.LastLockMilliseconds:0.00}\t{Nested.LastGpuWaitMilliseconds:0.00}\t{Nested.LastUploadMilliseconds:0.00}\t{GC.CollectionCount(0)}\t{GC.CollectionCount(1)}\t{GC.CollectionCount(2)}\t{(threadAllocated - _benchAllocated) / 1024.0:0.0}\t{Nested.DrawnCellCount}\t{Nested.LastGpuGlyphs}");
        _benchFrames.Append(CultureInfo.InvariantCulture,
            $"\t{Nested.LoopFrameCount}\t{(stats.Fresh ? 1 : 0)}\t{stats.CameraMs:0.000}\t{stats.HubMs:0.000}\t{stats.IconMs:0.000}\t{stats.ApplyMs:0.000}\t{stats.CaptureMs:0.000}\t{stats.TransitionMs:0.000}\t{stats.RenderMs:0.00}\t{stats.HubItems}\t{stats.Applied}\t{stats.Transitions}\t{(stats.CameraActive ? 1 : 0)}\t{(stats.Skipped ? 1 : 0)}\t{LayerLetters(Nested.LastFrameLayers)}\t{(clip.IsEmpty ? 0 : (long)clip.Width * clip.Height)}\t{(Nested.IsFrameHooked ? 1 : 0)}\t{uiCpu:0.00}\t{renderCpu:0.00}\n");
        _benchAllocated = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>What the canvas has done with folders read so far, to be taken from itself at a phase's end.</summary>
    private (long Redraws, long Held, long Clipped, long Labels, long Unseen) ScopeCounts() =>
        (Nested.LoadRedraws, Nested.LoadRedrawsHeld, Nested.ClippedSceneCount, Nested.LabelLayerCount, Nested.LoadBatchesUnseen);

    private void TakeScopeCounts(PhaseStats stats, (long Redraws, long Held, long Clipped, long Labels, long Unseen) atStart)
    {
        var now = ScopeCounts();
        stats.LoadRedraws = now.Redraws - atStart.Redraws;
        stats.LoadRedrawsHeld = now.Held - atStart.Held;
        stats.ClippedScenes = now.Clipped - atStart.Clipped;
        stats.LabelLayers = now.Labels - atStart.Labels;
        stats.UnseenBatches = now.Unseen - atStart.Unseen;
    }

    /// <summary>Runs <paramref name="step"/> once per frame for <paramref name="frames"/> frames, timing each.</summary>
    private Task<PhaseStats> PhaseAsync(string name, int frames, Action<int> step, bool force = true) =>
        PhaseCoreAsync(name, frames, step, force, finished: null, timeoutMilliseconds: double.PositiveInfinity);

    /// <summary>
    /// A rest: the canvas left alone - nothing moved, nothing forced - with
    /// every frame timed until its loop lets go of WPF's frames with nothing
    /// being read or waiting to be taken in, or <paramref name="timeoutSeconds"/>
    /// pass.  <see cref="PhaseStats.RestMilliseconds"/> is how long that took,
    /// or NaN when it never did: after a zoom into a folder of files, the
    /// time its icons and names take to come in and the picture to be still.
    /// </summary>
    private async Task<PhaseStats> RestPhaseAsync(string name, double timeoutSeconds = 20)
    {
        var watch = Stopwatch.StartNew();
        var rested = double.NaN;
        var stats = await PhaseCoreAsync(name, int.MaxValue, _ => { }, force: false, finished: () =>
        {
            if (Nested.IsFrameHooked || _nestedTree.PendingCount > 0 || Nested.HasPendingWork)
            {
                return false;
            }

            rested = watch.Elapsed.TotalMilliseconds;
            return true;
        }, timeoutMilliseconds: timeoutSeconds * 1000);
        stats.RestMilliseconds = rested;
        return stats;
    }

    private Task<PhaseStats> PhaseCoreAsync(string name, int frames, Action<int> step, bool force, Func<bool>? finished, double timeoutMilliseconds)
    {
        // One step per real frame.  WPF raises Rendering more than once per
        // frame when it is asked to, so frames are told apart by their
        // RenderingTime; the gap between two real frames is what a user sees
        // as smooth (16 ms) or as a hitch (anything much longer).
        var stats = new PhaseStats(name);
        var done = new TaskCompletionSource<PhaseStats>();
        var started = Stopwatch.StartNew();
        var frame = 0;
        var lastTime = TimeSpan.MinValue;
        var lastStamp = 0L;
        var lastCount = Nested.RenderCount;
        var allocatedAtStart = 0L;
        var gen0AtStart = 0;
        var gen1AtStart = 0;
        var gen2AtStart = 0;
        var skippedAtStart = 0;
        var cpuAtStart = default(BenchThreadClock.Sample);
        var pauseAtStart = TimeSpan.Zero;
        var scopeAtStart = ScopeCounts();
        if (_benchProbes is { } probes)
        {
            probes.Phase = name;
        }

        void OnFrame(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == lastTime)
            {
                return;
            }

            lastTime = time;
            var now = Stopwatch.GetTimestamp();
            var interval = lastStamp != 0 ? Stopwatch.GetElapsedTime(lastStamp, now).TotalMilliseconds : 0;
            if (lastStamp != 0)
            {
                stats.Interval.Add(interval);
            }

            lastStamp = now;
            var rendered = Nested.RenderCount != lastCount;
            AppendBenchFrame(name, frame, interval, rendered);
            if (rendered)
            {
                stats.Render.Add(Nested.LastRenderMilliseconds);
                stats.Cells.Add(Nested.DrawnCellCount);
                stats.Labels.Add(Nested.LastLabelsMilliseconds);
                stats.Layouts.Add(Nested.NewTextLayouts);
                stats.Frame.Add(Nested.LastFrameMilliseconds);
                stats.Walk.Add(Nested.LastWalkMilliseconds);
                stats.Present.Add(Nested.LastPresentMilliseconds);
                stats.Lock.Add(Nested.LastLockMilliseconds);
                stats.GpuWait.Add(Nested.LastGpuWaitMilliseconds);
                stats.Upload.Add(Nested.LastUploadMilliseconds);
                stats.Glyphs.Add(Nested.LastGpuGlyphs);
                stats.LabelsOnGpu |= Nested.AreLabelsOnGpu;
                stats.SceneGpuFrames += Nested.IsSceneOnGpu ? 1 : 0;
                stats.LabelsGpuFrames += Nested.AreLabelsOnGpu ? 1 : 0;
                var allocations = Nested.LastAllocations;
                stats.SceneAllocated += allocations.Scene;
                stats.LabelsAllocated += allocations.Labels;
                stats.DecorAllocated += allocations.Decor;
                stats.PresentAllocated += allocations.Present;
                if (Nested.IsSceneOnGpu && !double.IsNaN(Nested.LastGpuMilliseconds))
                {
                    stats.Gpu.Add(Nested.LastGpuMilliseconds);
                }

                lastCount = Nested.RenderCount;
            }

            if (frame == 0)
            {
                allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                gen0AtStart = GC.CollectionCount(0);
                gen1AtStart = GC.CollectionCount(1);
                gen2AtStart = GC.CollectionCount(2);
                skippedAtStart = Nested.SkippedPresents;
                cpuAtStart = BenchThreadClock.Read();
                pauseAtStart = GC.GetTotalPauseDuration();
                scopeAtStart = ScopeCounts();
            }

            if (frame >= frames || frame > 0 && (finished?.Invoke() == true || started.Elapsed.TotalMilliseconds > timeoutMilliseconds))
            {
                TakeScopeCounts(stats, scopeAtStart);
                stats.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
                stats.Gen0 = GC.CollectionCount(0) - gen0AtStart;
                stats.Gen1 = GC.CollectionCount(1) - gen1AtStart;
                stats.Gen2 = GC.CollectionCount(2) - gen2AtStart;
                stats.Skipped = Nested.SkippedPresents - skippedAtStart;
                stats.GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pauseAtStart).TotalMilliseconds;
                stats.HeapBytes = GC.GetTotalMemory(forceFullCollection: false);
                var cpu = BenchThreadClock.Read();
                stats.UiCpuPerFrame = BenchThreadClock.Milliseconds(cpu.Ui - cpuAtStart.Ui) / Math.Max(1, frame);
                stats.RenderCpuPerFrame = BenchThreadClock.Milliseconds(cpu.Render - cpuAtStart.Render) / Math.Max(1, frame);
                if (_benchProbes is { } probes)
                {
                    // Waits between phases - settling, flying to the next start -
                    // are nobody's phase.
                    probes.Phase = BenchProbes.BetweenPhases;
                }

                CompositionTarget.Rendering -= OnFrame;
                done.TrySetResult(stats);
                return;
            }

            step(frame++);
            if (force)
            {
                Nested.Redraw();
            }
        }

        CompositionTarget.Rendering += OnFrame;
        return done.Task;
    }

    private async Task RunNestedBenchAsync(string output)
    {
        TrackTreeChanges();
        var fixture = await EnsureBenchFixtureAsync();
        await Task.Delay(300);
        var rendererAtStart = await WaitForRendererAsync();
        var report = new StringBuilder();
        StartBenchInstruments();
        report.AppendLine(BenchPhaseHeader);

        Nested.FitAll(animated: false);
        var settle = await SettleAsync();
        var phases = new List<PhaseStats>
        {
            await PhaseAsync("fit-static", 60, _ => { })
        };

        var windows = await _nestedTree.RevealAsync(@"C:\Windows");
        var system32 = await _nestedTree.RevealAsync(@"C:\Windows\System32");
        if (windows is not null)
        {
            Nested.FlyTo(windows, 0.35, animated: false);
        }

        var centre = new Point(Nested.ActualWidth / 2, Nested.ActualHeight / 2);
        phases.Add(await PhaseAsync("zoom-in", 120, _ =>
        {
            var target = system32 is not null && Nested.ScreenRectOf(system32) is { } rect
                ? new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)
                : centre;
            Nested.ZoomAt(target, 1.035);
        }));

        // The frames right after the camera stops, drawn the way the app
        // draws them - only what is out of date - where the full-quality
        // picture replaces the one drawn in motion.
        phases.Add(await PhaseAsync("zoom-in-settle", 45, _ => { }, force: false));
        phases.Add(await PhaseAsync("pan", 120, frame => Nested.Pan(new Vector(frame < 60 ? -14 : 14, frame < 60 ? -6 : 6))));
        phases.Add(await PhaseAsync("zoom-out", 120, _ => Nested.ZoomAt(centre, 1 / 1.035)));

        if (system32 is not null)
        {
            Nested.FlyTo(system32, 0.96, animated: false);
            await SettleAsync();
            phases.Add(await PhaseAsync("files-static", 60, _ => { }));
            phases.Add(await PhaseAsync("files-zoom", 120, _ => Nested.ZoomAt(new Point(centre.X, Nested.ActualHeight * 0.75), 1.025)));
            phases.Add(await PhaseAsync("files-settle", 45, _ => { }, force: false));
            phases.Add(await PhaseAsync("files-pan", 90, _ => Nested.Pan(new Vector(0, -10))));
        }

        Nested.FitAll(animated: false);
        phases.Add(await PhaseAsync("fly", 90, frame =>
        {
            if (frame == 0 && system32 is not null)
            {
                Nested.FlyTo(system32, 0.8);
            }
        }));

        // A change of order over thousands of files, four times, the way a
        // user clicks through the headers: every frame of it timed, as well
        // as the click itself, which must hand the work to the frames and the
        // background rather than do it there and then.
        var sortSwitch = new SortSwitchStats();
        if (system32 is not null)
        {
            Nested.FlyTo(system32, 0.96, animated: false);
            await SettleAsync();
            phases.Add(await PhaseAsync("sort-switch", 120, frame =>
            {
                if (frame % 30 == 0)
                {
                    sortSwitch.Switch(this, BenchSorts[frame / 30 % BenchSorts.Length]);
                }
            }));

            _viewModel.Sort = ItemSort.Default;
            await _viewModel.SaveNowAsync();
            await SettleAsync();
        }

        if (fixture is not null)
        {
            await FlyBenchFixtureAsync(fixture, phases);
        }

        foreach (var phase in phases)
        {
            report.AppendLine(BenchPhaseRow(phase));
        }

        report.AppendLine();
        report.AppendLine($"sort_click_max_ms\t{sortSwitch.WorstMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}");
        report.AppendLine($"sort_canvas_max_ms\t{sortSwitch.WorstCanvasMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}");
        report.AppendLine($"sort_placed_in_background\t{(sortSwitch.Clicks > 0 && sortSwitch.Deferred == sortSwitch.Clicks ? "yes" : "no")}");

        // Selecting a folder no one has selected before: the time until the
        // address bar, the list and the status bar all have it.
        report.AppendLine();
        report.AppendLine("select\tms");
        if (_benchProbes is { } probes)
        {
            probes.Phase = "select";
        }

        foreach (var path in BenchDeepFolders.Where(Directory.Exists))
        {
            var watch = Stopwatch.StartNew();
            await _viewModel.Tree.SelectPathAsync(path);
            watch.Stop();
            report.AppendLine($"{path}\t{watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        }

        report.AppendLine();
        report.AppendLine($"settle_fit_ms\t{settle.ToString("0", CultureInfo.InvariantCulture)}");
        report.AppendLine($"loaded_folders\t{_nestedTree.LoadedCount}");
        AppendRendererReport(report, rendererAtStart);
        FinishBenchInstruments(report, output);
    }

    /// <summary>
    /// What every route measures besides its phases' frames, started once the
    /// renderer is ready: the threads' clocks, the frames file, and the probes
    /// and the count of dispatcher operations (<see cref="BenchProbes"/>).
    /// </summary>
    private void StartBenchInstruments()
    {
        BenchThreadClock.Start();
        _benchFrames = new StringBuilder(BenchFramesHeader, 1 << 20);
        _benchFrameCpu = BenchThreadClock.Read();
        _benchAllocated = GC.GetAllocatedBytesForCurrentThread();
        _benchProbes?.Dispose();
        _benchProbes = SwitchValue("--bench-probes")?.ToLowerInvariant() switch
        {
            "none" => null,
            "off" => new BenchProbes(Dispatcher, probing: false),
            _ => new BenchProbes(Dispatcher, probing: true)
        };
    }

    /// <summary>
    /// Stops the probes and writes the report to <paramref name="output"/>, and
    /// beside it the frames (<c>.frames.tsv</c>), every probe's wait
    /// (<c>.probes.tsv</c>) and the dispatcher operations by source
    /// (<c>.ops.tsv</c>).
    /// </summary>
    private void FinishBenchInstruments(StringBuilder report, string output)
    {
        if (_benchProbes is { } probes)
        {
            probes.Stop();
            report.AppendLine($"probes\tinput every {BenchProbes.InputPeriodMilliseconds} ms, background every {BenchProbes.BackgroundPeriodMilliseconds} ms\t{probes.SampleCount} waits\t{probes.OperationCount} dispatcher operations run");
            File.WriteAllText(output + ".probes.tsv", probes.SamplesTable());
            File.WriteAllText(output + ".ops.tsv", probes.OperationsTable());
        }

        File.WriteAllText(output, report.ToString());
        File.WriteAllText(output + ".frames.tsv", _benchFrames.ToString());
    }

    /// <summary>
    /// The end of every route's report: where the scene was drawn, on what,
    /// and what the GPU's start-up did, and the canvas's size - so that two
    /// reports are only held against each other when they measured the same
    /// thing.
    /// </summary>
    private void AppendRendererReport(StringBuilder report, string rendererAtStart)
    {
        var dpi = VisualTreeHelper.GetDpi(Nested);
        report.AppendLine();
        report.AppendLine($"renderer\t{(Nested.IsSceneOnGpu ? "gpu" : "cpu")}\t{Nested.RendererReason}\t{Nested.RendererAdapter}\tlabels\t{(Nested.AreLabelsOnGpu ? "gpu" : "wpf")}");
        report.AppendLine($"renderer_at_start\t{rendererAtStart}");
        report.AppendLine($"motion\t{(Nested.SmoothMotion ? "smooth" : "direct")}");
        report.AppendLine($"bench_priority\tnormal");
        report.AppendLine($"read_delay_ms\t{_benchReadDelay}");
        report.AppendLine($"fixture\t{SwitchValue("--bench-fixture") ?? "none"}");
        report.AppendLine($"render_thread\t{BenchThreadClock.RenderThreadName}");
        report.AppendLine($"label_atlases\t{Rendering.Gpu.GpuLabelAtlases.Report}");
        if (Nested.GpuTextShaper is { } shaper)
        {
            report.AppendLine($"text_shaper\t{shaper.Count} names shaped\t{shaper.DirectShapes} of them on the UI thread (not prefetched)\t{shaper.PendingCount} pending");
        }

        if (Rendering.Gpu.GpuLabelAtlases.Current is { } atlases)
        {
            report.AppendLine($"atlases\tglyphs {atlases.Glyphs.EntryCount} on {atlases.Glyphs.PageCount} pages\ticons {atlases.Icons.SlotCount} slots from {atlases.Icons.ExtractionCount} extractions");
        }
        report.AppendLine($"text_tuning\tgamma {Rendering.Gpu.GpuTextTuning.Gamma.ToString(CultureInfo.InvariantCulture)}\tcontrast {Rendering.Gpu.GpuTextTuning.Contrast.ToString(CultureInfo.InvariantCulture)}\tbias {Rendering.Gpu.GpuTextTuning.BiasScale.ToString(CultureInfo.InvariantCulture)}");
        var scale = Nested.DpiOverride ?? dpi;
        report.AppendLine($"canvas_pixels\t{Math.Ceiling(Nested.ActualWidth * scale.DpiScaleX)}x{Math.Ceiling(Nested.ActualHeight * scale.DpiScaleY)}\tcanvas_dips\t{Nested.ActualWidth:0}x{Nested.ActualHeight:0}\tbench_scale\t{BenchNumber("--bench-scale")}\tbench_layout\t{BenchNumber("--bench-layout")}");
        foreach (var line in Rendering.Gpu.GpuBootstrap.Events)
        {
            report.AppendLine($"gpu_event\t{line.Trim()}");
        }
    }

    private async Task RunNestedSnapshotsAsync(string folder)
    {
        TrackTreeChanges();
        Directory.CreateDirectory(folder);
        await Task.Delay(300);
        await WaitForRendererAsync();

        // Beside the pictures, what drew each one: a set of GPU pictures with
        // a CPU one among them says so instead of passing for a baseline.
        var renderers = new StringBuilder("scene\tcells\tnames\treason\n");

        SnapshotScene[] scenes =
        [
            new("01-this-pc", null, 1, 1),
            new("02-drive-c", @"C:\", 0.92, 1),
            new("03-windows", @"C:\Windows", 0.92, 1),
            new("04-system32", @"C:\Windows\System32", 0.92, 1),
            new("05-system32-files", @"C:\Windows\System32", 0.92, 6),
            new("06-fonts", @"C:\Windows\Fonts", 0.92, 1),
            new("07-program-files", @"C:\Program Files", 0.92, 1),
            new("08-winsxs", @"C:\Windows\WinSxS", 0.92, 1),
            new("09-winsxs-deep", @"C:\Windows\WinSxS", 0.92, 40),
            new("10-etc", @"C:\Windows\System32\drivers\etc", 0.6, 1),
            new("11-filter-exe", @"C:\Windows\System32", 0.92, 1, "*.exe"),
            new("12-filter-this-pc", null, 1, 1, "config"),
            new("13-sort-date", @"C:\Windows", 0.92, 1, Sort: new ItemSort(SortColumn.Modified, true)),
            new("14-sort-size-files", @"C:\Windows\System32", 0.92, 6, Sort: new ItemSort(SortColumn.Size, true)),
            new("15-sort-type-files", @"C:\Windows\System32", 0.92, 6, Sort: new ItemSort(SortColumn.Type, false)),
            new("16-own-order-windows", @"C:\Windows", 0.92, 1, Sort: new ItemSort(SortColumn.Modified, true), Own: true),
            new("17-across-system32-files", @"C:\Windows\System32", 0.92, 6, Across: true),
        ];

        // The same views with the strip over the canvas, headers and all: the
        // pictures above stay the canvas alone, to compare pixel for pixel.
        var withStrip = Path.Combine(folder, "with-strip");
        Directory.CreateDirectory(withStrip);
        var orders = _viewModel.Orders;
        foreach (var (name, path, fill, zoom, filter, sort, own, across) in scenes)
        {
            Nested.SetFilter(filter);
            orders.Flow = across ? LayoutOrder.AcrossThenDown : LayoutOrder.DownThenAcross;
            orders.UseEverywhere(own ? ItemSort.Default : sort ?? ItemSort.Default);
            if (own && path is not null && sort is { } folderSort)
            {
                orders.SetFolder(ViewAllPath.Normalize(path), folderSort);
            }

            if (path is null)
            {
                Nested.FitAll(animated: false);
            }
            else if (await _nestedTree.RevealAsync(path) is { } target && ViewAllPath.Equals(target.FullPath, path))
            {
                Nested.FlyTo(target, fill, animated: false);
            }
            else
            {
                continue;
            }

            if (zoom != 1)
            {
                // Into the lower part of the cell, where the files are.
                Nested.ZoomAt(new Point(Nested.ActualWidth * 0.3, Nested.ActualHeight * 0.8), zoom);
            }

            await SettleAsync();
            Save(Nested, Path.Combine(folder, name + ".png"));
            renderers.Append(name).Append('\t').Append(RendererState()).Append('\n');
            Save(NestedHost, Path.Combine(withStrip, name + ".png"));
        }

        File.WriteAllText(Path.Combine(folder, "renderer.tsv"), renderers.ToString());

        // Written now: the run exits before the window is ever idle enough
        // to write it by itself.
        orders.UseEverywhere(ItemSort.Default);
        orders.Flow = LayoutOrder.DownThenAcross;
        await _viewModel.SaveNowAsync();
    }

    /// <summary>
    /// One view the snapshots render: where, how far in, and with what filter
    /// and order - every folder's, or with <paramref name="Own"/> the folder's
    /// own alone - read down first unless <paramref name="Across"/>.
    /// </summary>
    private sealed record SnapshotScene(
        string Name,
        string? Path,
        double Fill,
        double Zoom,
        string Filter = "",
        ItemSort? Sort = null,
        bool Own = false,
        bool Across = false);

    private void Save(FrameworkElement element, string path)
    {
        // Every folder placed for the order first: a picture must never catch
        // the background pass half way.
        _nestedTree.FlushSortWork();
        element.UpdateLayout();
        Nested.RenderNow();

        // At the scale the canvas draws at: with --bench-scale that is the
        // stand-in for a 150 % monitor, and the picture is kept at its pixels.
        var dpi = Nested.DpiOverride ?? VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        // Through a brush, so the element's own offset in the window - the
        // canvas's under the strip - is not part of the picture.
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---- the probes --------------------------------------------------------------

    private BenchProbes? _benchProbes;

    /// <summary>
    /// What the dispatcher did for everybody else while a route ran - the
    /// part of a freeze a frame's own timings cannot show.
    ///
    /// <para>Two probes: an Input-priority operation every 10 ms, which is
    /// what a wheel notch or a key waits behind, and a Background one every
    /// 50 ms, which is what the window's timers and debounces wait behind.
    /// Each records how long it waited from being posted to running.  Only
    /// one of each is ever waiting: a dispatcher held for half a second gives
    /// one wait of half a second, not fifty.  They are posted from a thread
    /// of their own on a high-resolution timer, so the wait measured is the
    /// dispatcher's and not the system clock's 15.6 ms granularity.</para>
    ///
    /// <para>Every operation the dispatcher runs is counted by where it came
    /// from - the method posted, or for an await the async method it resumes
    /// - and at what priority, per phase: "0 per read, icon or watch event"
    /// is read off these counts.</para>
    /// </summary>
    private sealed class BenchProbes : IDisposable
    {
        public const int InputPeriodMilliseconds = 10;
        public const int BackgroundPeriodMilliseconds = 50;

        /// <summary>The phase waits are put under while no phase runs.</summary>
        public const string BetweenPhases = "between";

        /// <summary>The probes' columns for a route that ran without them.</summary>
        public static readonly string NoColumns = string.Join('\t', Enumerable.Repeat("-", 8));

        private static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ArgsField = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TickField = typeof(DispatcherTimer).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Regex AsyncMethod = new(@"([\w.]+)\+<(\w+)>d__", RegexOptions.Compiled);
        private static readonly Regex LambdaMethod = new(@"^<(\w+)>", RegexOptions.Compiled);

        private readonly Dispatcher _dispatcher;
        private readonly long _origin = Stopwatch.GetTimestamp();
        private readonly Thread _thread;
        private readonly Action _onInput;
        private readonly Action _onBackground;
        private readonly List<(string Phase, double At, string Priority, double Wait)> _samples = [];
        private readonly Dictionary<(string Phase, DispatcherPriority Priority, string Source), int> _operations = [];
        private readonly Dictionary<Delegate, string> _names = [];
        private volatile string _phase = "start";
        private volatile bool _stopping;
        private bool _hooked;
        private int _inputWaiting;
        private int _backgroundWaiting;
        private long _inputPosted;
        private long _backgroundPosted;
        private string _inputPhase = "start";
        private string _backgroundPhase = "start";

        public BenchProbes(Dispatcher dispatcher, bool probing)
        {
            _dispatcher = dispatcher;
            _onInput = OnInputProbe;
            _onBackground = OnBackgroundProbe;
            _dispatcher.Hooks.OperationStarted += OnOperationStarted;
            _hooked = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "bench probes", Priority = ThreadPriority.AboveNormal };
            if (probing)
            {
                _thread.Start();
            }
        }

        /// <summary>The phase what happens from now on counts to.  Set on the UI thread, read by the probes' thread.</summary>
        public string Phase
        {
            get => _phase;
            set => _phase = value;
        }

        public int SampleCount => _samples.Count;

        public long OperationCount => _operations.Values.Sum(count => (long)count);

        public void Stop()
        {
            _stopping = true;
            if (_hooked)
            {
                _hooked = false;
                _dispatcher.Hooks.OperationStarted -= OnOperationStarted;
            }

            if (_thread.IsAlive)
            {
                _thread.Join(200);
            }
        }

        public void Dispose() => Stop();

        /// <summary>The probes' columns of the summary (<see cref="BenchPhaseHeader"/>) for one phase.</summary>
        public string Columns(string phase)
        {
            var input = new List<double>();
            var background = new List<double>();
            foreach (var sample in _samples)
            {
                if (sample.Phase == phase)
                {
                    (sample.Priority == "input" ? input : background).Add(sample.Wait);
                }
            }

            input.Sort();
            var operations = 0L;
            foreach (var (key, count) in _operations)
            {
                if (key.Phase == phase)
                {
                    operations += count;
                }
            }

            var inv = CultureInfo.InvariantCulture;
            return string.Join('\t',
                input.Count.ToString(inv),
                Percentile(input, 0.5).ToString("0.00", inv),
                Percentile(input, 0.95).ToString("0.00", inv),
                (input.Count == 0 ? 0 : input[^1]).ToString("0.00", inv),
                input.Count(wait => wait > 100).ToString(inv),
                background.Count.ToString(inv),
                (background.Count == 0 ? 0 : background.Max()).ToString("0.00", inv),
                operations.ToString(inv));
        }

        /// <summary>Every wait: its phase, when it ran (ms from the start of the probes), which probe, and how long it waited.</summary>
        public string SamplesTable()
        {
            var table = new StringBuilder("phase\tat_ms\tprobe\twait_ms\n", 64 * (_samples.Count + 1));
            foreach (var (phase, at, priority, wait) in _samples)
            {
                table.Append(CultureInfo.InvariantCulture, $"{phase}\t{at:0.0}\t{priority}\t{wait:0.00}\n");
            }

            return table.ToString();
        }

        /// <summary>Every operation the dispatcher ran, counted by phase, priority and where it came from, most frequent first.</summary>
        public string OperationsTable()
        {
            var table = new StringBuilder("phase\tpriority\tsource\tcount\n");
            foreach (var ((phase, priority, source), count) in _operations.OrderBy(entry => entry.Key.Phase, StringComparer.Ordinal).ThenByDescending(entry => entry.Value))
            {
                table.Append(CultureInfo.InvariantCulture, $"{phase}\t{priority}\t{source}\t{count}\n");
            }

            return table.ToString();
        }

        private static double Percentile(List<double> sorted, double p) =>
            sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1))];

        private double Now => Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

        /// <summary>The probes' thread: a tick every 10 ms, an Input probe on each and a Background probe on every fifth.</summary>
        private void Run()
        {
            using var timer = new BenchTimer();
            var period = Stopwatch.Frequency * InputPeriodMilliseconds / 1000;
            var next = Stopwatch.GetTimestamp();
            var tick = 0;
            while (!_stopping)
            {
                next += period;
                var now = Stopwatch.GetTimestamp();
                if (next > now)
                {
                    timer.Wait((next - now) * 1000.0 / Stopwatch.Frequency);
                }
                else
                {
                    // Behind - the machine was busy: start again from now
                    // rather than post a burst to catch up.
                    next = now;
                }

                if (_stopping)
                {
                    break;
                }

                if (Interlocked.Exchange(ref _inputWaiting, 1) == 0)
                {
                    _inputPhase = _phase;
                    Volatile.Write(ref _inputPosted, Stopwatch.GetTimestamp());
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, _onInput);
                }

                if (++tick % (BackgroundPeriodMilliseconds / InputPeriodMilliseconds) == 0 && Interlocked.Exchange(ref _backgroundWaiting, 1) == 0)
                {
                    _backgroundPhase = _phase;
                    Volatile.Write(ref _backgroundPosted, Stopwatch.GetTimestamp());
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, _onBackground);
                }
            }
        }

        private void OnInputProbe()
        {
            var wait = Stopwatch.GetElapsedTime(Volatile.Read(ref _inputPosted)).TotalMilliseconds;
            _samples.Add((_inputPhase, Now, "input", wait));
            Volatile.Write(ref _inputWaiting, 0);
        }

        private void OnBackgroundProbe()
        {
            var wait = Stopwatch.GetElapsedTime(Volatile.Read(ref _backgroundPosted)).TotalMilliseconds;
            _samples.Add((_backgroundPhase, Now, "background", wait));
            Volatile.Write(ref _backgroundWaiting, 0);
        }

        /// <summary>On the UI thread, as each operation starts: counted under its source, the probes' own left out.</summary>
        private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
        {
            var operation = e.Operation;
            var method = MethodField?.GetValue(operation) as Delegate;
            if (method is not null && ReferenceEquals(method.Target, this))
            {
                return;
            }

            var key = (_phase, operation.Priority, SourceOf(method, ArgsField?.GetValue(operation)));
            CollectionsMarshal.GetValueRefOrAddDefault(_operations, key, out _)++;
        }

        /// <summary>
        /// Where an operation came from: the method posted, and what it runs
        /// in turn - the continuation of an await, which names the async
        /// method it resumes, or a timer's Tick handlers.
        /// </summary>
        private string SourceOf(Delegate? method, object? args)
        {
            var name = method is null ? "?" : NameOf(method);
            if (args is Delegate inner)
            {
                return name + " > " + NameOf(inner);
            }

            if ((method?.Target as DispatcherTimer ?? args as DispatcherTimer) is { } timer && TickField?.GetValue(timer) is Delegate tick)
            {
                return name + " > " + string.Join(" + ", tick.GetInvocationList().Select(NameOf));
            }

            return name;
        }

        /// <summary>A delegate's method as "Type.Method", lambdas and async state machines by the method they are written in.  Cached: the same few delegates come round again and again.</summary>
        private string NameOf(Delegate method)
        {
            if (_names.TryGetValue(method, out var known))
            {
                return known;
            }

            string name;
            if (method.Target is { } target && AsyncMethod.Match(target.GetType().FullName ?? string.Empty) is { Success: true } match)
            {
                var owner = match.Groups[1].Value;
                name = "async " + owner[(owner.LastIndexOf('.') + 1)..] + "." + match.Groups[2].Value;
            }
            else
            {
                var type = method.Method.DeclaringType;
                while (type is { DeclaringType: { } outer } && type.Name.StartsWith('<'))
                {
                    type = outer;
                }

                var lambda = LambdaMethod.Match(method.Method.Name);
                name = (type?.Name ?? "?") + "." + (lambda.Success ? lambda.Groups[1].Value + " (lambda)" : method.Method.Name);
            }

            // Delegates made afresh for every post - a lambda closing over
            // something - are never met again; only a bounded number are kept.
            if (_names.Count < 4096)
            {
                _names[method] = name;
            }

            return name;
        }
    }

    /// <summary>
    /// A wait of a fraction of a millisecond or more, to the millisecond or
    /// better: a high-resolution waitable timer, where Windows has one, rather
    /// than a sleep, which the system clock rounds up to 15.6 ms unless some
    /// program has asked for better for the whole machine.
    /// </summary>
    private sealed class BenchTimer : IDisposable
    {
        private const uint HighResolution = 0x2;
        private const uint AllAccess = 0x1F0003;
        private readonly IntPtr _handle = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, AllAccess);

        public void Wait(double milliseconds)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            if (_handle == IntPtr.Zero)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Round(milliseconds)));
                return;
            }

            // Relative, in units of 100 ns.
            var due = -(long)(milliseconds * 10_000);
            if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_handle, uint.MaxValue);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

    // ---- slow reads ----------------------------------------------------------------

    /// <summary>What <c>--bench-read-delay-ms</c> did, for the report.</summary>
    private string _benchReadDelay = "0";

    /// <summary>
    /// <c>--bench-read-delay-ms N</c>: every read of a folder takes N ms longer,
    /// the way a slow disk or a busy share does - so a route shows whether
    /// input still gets its turn when reads trickle in rather than pour.
    /// The tree's reader is wrapped in place: it is made with the window, and
    /// the bench has no say in how.  The delay is a timed wait on the reading
    /// thread, not a sleep the system clock would stretch to 15.6 ms.
    /// </summary>
    private void ApplyBenchReadDelay()
    {
        if (SwitchValue("--bench-read-delay-ms") is not { } text
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay)
            || delay <= 0)
        {
            return;
        }

        foreach (var field in typeof(NestedTree).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(Func<string, CancellationToken, NestedListing>)
                && field.GetValue(_nestedTree) is Func<string, CancellationToken, NestedListing> reader)
            {
                field.SetValue(_nestedTree, (Func<string, CancellationToken, NestedListing>)((path, token) =>
                {
                    using (var timer = new BenchTimer())
                    {
                        timer.Wait(delay);
                    }

                    return reader(path, token);
                }));
                _benchReadDelay = delay.ToString(CultureInfo.InvariantCulture);
                return;
            }
        }

        _benchReadDelay = $"{delay} not applied: the tree has no reader of the expected kind to wrap";
    }

    // ---- the generated tree ------------------------------------------------------

    /// <summary>
    /// The tree <c>--bench-fixture &lt;dir&gt;</c> generates, so a route's
    /// numbers do not depend on what happens to be on the machine's disks:
    ///
    /// <list type="bullet">
    /// <item><see cref="Folders"/>: 2,000 folders of 5 files each - about
    /// 2,000 reads when zoomed into, and the 2,000 loaded folders the live
    /// route needs;</item>
    /// <item><see cref="Files"/>: one folder of 1,800 files across 40
    /// extensions, 250 of them .exe, .lnk, .ico or without an extension - the
    /// kinds whose icon is asked for file by file;</item>
    /// <item><see cref="Watched"/>: <c>live\W</c>, which the live route puts
    /// on screen and writes into, reset to its two files each time;</item>
    /// <item><see cref="Noise"/>: <c>noise\N</c>, which the live route floods
    /// with changes while nothing shows it, emptied each time.</item>
    /// </list>
    ///
    /// Names, sizes and dates are the same on every run.  It is written once;
    /// a marker file says it is complete and of this version.
    /// </summary>
    private sealed record BenchFixture(string Root, string Folders, string Files, string Watched, string Noise);

    private const string BenchFixtureVersion = "UltraExplorer bench fixture 1";

    /// <summary>The fixture <c>--bench-fixture</c> names, written if need be on a worker thread; null without the switch.</summary>
    private static async Task<BenchFixture?> EnsureBenchFixtureAsync() =>
        SwitchValue("--bench-fixture") is { } root
            ? await Task.Run(() => EnsureBenchFixture(Path.GetFullPath(root)))
            : null;

    private static BenchFixture EnsureBenchFixture(string root)
    {
        var fixture = new BenchFixture(
            root,
            Path.Combine(root, "folders"),
            Path.Combine(root, "files"),
            Path.Combine(root, "live", "W"),
            Path.Combine(root, "noise", "N"));
        var marker = Path.Combine(root, "fixture.txt");
        if (!File.Exists(marker) || File.ReadAllText(marker) != BenchFixtureVersion)
        {
            WriteBenchFixture(fixture);
            File.WriteAllText(marker, BenchFixtureVersion);
        }

        // The live parts start the same way every time.
        Directory.CreateDirectory(fixture.Watched);
        foreach (var entry in new DirectoryInfo(fixture.Watched).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else if (entry.Name is not ("w0.txt" or "w1.txt"))
            {
                entry.Delete();
            }
        }

        WriteFixtureFile(Path.Combine(fixture.Watched, "w0.txt"), 120, 0);
        WriteFixtureFile(Path.Combine(fixture.Watched, "w1.txt"), 340, 1);
        if (Directory.Exists(fixture.Noise))
        {
            Directory.Delete(fixture.Noise, recursive: true);
        }

        Directory.CreateDirectory(fixture.Noise);
        return fixture;
    }

    private static readonly string[] BenchFolderFiles = ["notes.txt", "Program.cs", "photo.png", "settings.json", "README.md"];

    /// <summary>Forty extensions: the four whose icons are per file, then thirty-six whose icons are per kind.</summary>
    private static readonly string[] BenchPerPathExtensions = ["exe", "lnk", "ico", ""];

    private static readonly string[] BenchKindExtensions =
    [
        "txt", "cs", "png", "jpg", "json", "md", "xml", "html", "css", "js", "ts", "py",
        "dll", "log", "csv", "zip", "pdf", "docx", "xlsx", "mp3", "wav", "mp4", "gif", "svg",
        "ini", "cfg", "yml", "bat", "ps1", "sql", "db", "bin", "dat", "tmp", "bak", "h"
    ];

    private static void WriteBenchFixture(BenchFixture fixture)
    {
        for (var folder = 0; folder < 2_000; folder++)
        {
            var path = Path.Combine(fixture.Folders, $"f{folder:D4}");
            Directory.CreateDirectory(path);
            for (var file = 0; file < BenchFolderFiles.Length; file++)
            {
                WriteFixtureFile(Path.Combine(path, BenchFolderFiles[file]), (folder * 7 + file * 13) % 4096 + 1, folder * 5 + file);
            }
        }

        Directory.CreateDirectory(fixture.Files);
        var perPath = 0;
        for (var index = 0; index < 1_800; index++)
        {
            string extension;
            if (index % 7 == 0 && perPath < 250)
            {
                extension = BenchPerPathExtensions[perPath++ % BenchPerPathExtensions.Length];
            }
            else
            {
                extension = BenchKindExtensions[index % BenchKindExtensions.Length];
            }

            var name = extension.Length == 0 ? $"item{index:D4}" : $"item{index:D4}.{extension}";
            WriteFixtureFile(Path.Combine(fixture.Files, name), index * 7919 % 200_000 + 1, 20_000 + index);
        }
    }

    /// <summary>A file of <paramref name="length"/> bytes, dated a fixed number of minutes into 2024 so an order by date is the same on every run.</summary>
    private static void WriteFixtureFile(string path, int length, int minutes)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)('a' + index % 26);
        }

        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes));
    }

    /// <summary>A folder read again for the bench, whatever the disk says: a failure is the folder's to show, not the bench's.</summary>
    private async Task RefreshForBenchAsync(NestedFolder folder)
    {
        try
        {
            await _nestedTree.RefreshAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The default route's part over the generated tree, when there is one:
    /// into the 2,000 folders at rest while they are read, a dozen of them
    /// read again one by one, and into the folder of 1,800 files at tiles
    /// tall enough for their names while the icons come in - the rests timed
    /// until the canvas's loop lets go.
    /// </summary>
    private async Task FlyBenchFixtureAsync(BenchFixture fixture, List<PhaseStats> phases)
    {
        if (await _nestedTree.RevealAsync(fixture.Folders) is { } folders && ViewAllPath.Equals(folders.FullPath, fixture.Folders))
        {
            Nested.FlyTo(folders, 0.92, animated: false);
            phases.Add(await RestPhaseAsync("fixture-folders-rest"));

            // Then, at rest, a dozen of those folders read again one after
            // another, 50 ms apart: what a change on disk costs a view that
            // shows it - the cells of one folder, not the picture.
            var children = folders.Children;
            var refreshed = Enumerable.Range(0, 12).Select(index => children[(index * 157 + 40) % children.Count]).ToList();
            var clock = new Stopwatch();
            var started = 0;
            phases.Add(await PhaseCoreAsync("fixture-refresh", int.MaxValue, frame =>
            {
                clock.Start();
                var due = Math.Min(refreshed.Count, (int)(clock.Elapsed.TotalMilliseconds / 50) + 1);
                while (started < due)
                {
                    _ = RefreshForBenchAsync(refreshed[started++]);
                }
            }, force: false, finished: () => started >= refreshed.Count && clock.Elapsed.TotalMilliseconds > refreshed.Count * 50 + 300, timeoutMilliseconds: 5_000));
        }

        if (await _nestedTree.RevealAsync(fixture.Files) is { } files && ViewAllPath.Equals(files.FullPath, fixture.Files))
        {
            Nested.FlyTo(files, 0.92, animated: false);
            await SettleAsync();

            // Tiles 14 DIPs tall, their names drawn: into the middle of the files.
            if (Nested.ScreenRectOf(files) is { } cell && files.FileGrid is { IsEmpty: false } grid)
            {
                var zoom = 14 / (grid.TileHeight * cell.Width);
                var at = new Point(cell.X + (grid.Left + grid.StepX * grid.Columns / 2) * cell.Width, cell.Y + (grid.Top + grid.StepY * grid.Rows / 2) * cell.Width);
                Nested.ZoomAt(at, zoom);
            }

            phases.Add(await RestPhaseAsync("fixture-files-rest"));
        }
    }
}
