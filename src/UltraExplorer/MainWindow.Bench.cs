using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
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
/// the numbers and exits.</item>
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
            // The canvas's part first, on its own, then the rest of what a
            // click does - the tree, the list, the headers - which finds the
            // canvas's tree already in the new order.
            var watch = Stopwatch.StartNew();
            window._nestedTree.SetSort(sort);
            WorstCanvasMilliseconds = Math.Max(WorstCanvasMilliseconds, watch.Elapsed.TotalMilliseconds);
            window._viewModel.Sort = sort;
            watch.Stop();
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
            return;
        }

        var chosen = monitors.FindIndex(monitor => !monitor.IsPrimary);
        if (int.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIAGNOSTICS_MONITOR"), out var asked)
            && asked >= 0 && asked < monitors.Count)
        {
            chosen = asked;
        }

        // Never the active window: not when it opens, not when it restores
        // itself from maximised, not if clicked.  The run needs no keyboard.
        if (neverActivate)
        {
            SetWindowLongPtr(handle, ExtendedStyleIndex, GetWindowLongPtr(handle, ExtendedStyleIndex) | NoActivateStyle);
        }

        var (target, work, _) = monitors[Math.Max(0, chosen)];
        var scale = GetDpiForMonitor(target, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var width = Math.Min((int)Math.Round(1600 * scale), work.Right - work.Left);
        var height = Math.Min((int)Math.Round(1000 * scale), work.Bottom - work.Top);
        SetWindowPos(handle, IntPtr.Zero, work.Left + 8, work.Top + 8, width, height, SwpNoZOrder | SwpNoActivate);
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
        Width = 1600;
        Height = 1000;
        _viewModel.Layout = CanvasLayout.Nested;
        _viewModel.Tree.FolderList.IsVisible = false;

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (bench is not null)
                {
                    await RunNestedBenchAsync(bench);
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
                Application.Current.Shutdown();
            }
        }, DispatcherPriority.ApplicationIdle);
        return true;
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

    private void TrackTreeChanges() => _nestedTree.Changed += (_, _) => _treeChangedRecently = true;

    private sealed class PhaseStats(string name)
    {
        public string Name { get; } = name;
        public List<double> Render { get; } = [];
        public List<double> Interval { get; } = [];
        public List<int> Cells { get; } = [];
        public List<double> Labels { get; } = [];
        public List<double> Layouts { get; } = [];

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
                (Layouts.Count == 0 ? 0 : Layouts.Max()).ToString("0", inv));
        }
    }

    /// <summary>Runs <paramref name="step"/> once per frame for <paramref name="frames"/> frames, timing each.</summary>
    private Task<PhaseStats> PhaseAsync(string name, int frames, Action<int> step, bool force = true)
    {
        // One step per real frame.  WPF raises Rendering more than once per
        // frame when it is asked to, so frames are told apart by their
        // RenderingTime; the gap between two real frames is what a user sees
        // as smooth (16 ms) or as a hitch (anything much longer).
        var stats = new PhaseStats(name);
        var done = new TaskCompletionSource<PhaseStats>();
        var frame = 0;
        var lastTime = TimeSpan.MinValue;
        var lastStamp = 0L;
        var lastCount = Nested.RenderCount;
        void OnFrame(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == lastTime)
            {
                return;
            }

            lastTime = time;
            var now = Stopwatch.GetTimestamp();
            if (lastStamp != 0)
            {
                stats.Interval.Add(Stopwatch.GetElapsedTime(lastStamp, now).TotalMilliseconds);
            }

            lastStamp = now;
            if (Nested.RenderCount != lastCount)
            {
                stats.Render.Add(Nested.LastRenderMilliseconds);
                stats.Cells.Add(Nested.DrawnCellCount);
                stats.Labels.Add(Nested.LastLabelsMilliseconds);
                stats.Layouts.Add(Nested.NewTextLayouts);
                lastCount = Nested.RenderCount;
            }

            if (frame >= frames)
            {
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
        await Task.Delay(300);
        var report = new StringBuilder();
        report.AppendLine("phase\tframes\tmean_ms\tp50_ms\tp95_ms\tmax_ms\tinterval_ms\tinterval_p95\tcells\thitches_20ms\tworst_gap_ms\tlabels_ms\tlabels_max\tnew_text\tnew_text_max");

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

        foreach (var phase in phases)
        {
            report.AppendLine(phase.Row());
        }

        report.AppendLine();
        report.AppendLine($"sort_click_max_ms\t{sortSwitch.WorstMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}");
        report.AppendLine($"sort_canvas_max_ms\t{sortSwitch.WorstCanvasMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}");
        report.AppendLine($"sort_placed_in_background\t{(sortSwitch.Clicks > 0 && sortSwitch.Deferred == sortSwitch.Clicks ? "yes" : "no")}");

        // Selecting a folder no one has selected before: the time until the
        // address bar, the list and the status bar all have it.
        report.AppendLine();
        report.AppendLine("select\tms");
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
        File.WriteAllText(output, report.ToString());
    }

    private async Task RunNestedSnapshotsAsync(string folder)
    {
        TrackTreeChanges();
        Directory.CreateDirectory(folder);
        await Task.Delay(300);

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
        ];

        // The same views with the strip over the canvas, headers and all: the
        // pictures above stay the canvas alone, to compare pixel for pixel.
        var withStrip = Path.Combine(folder, "with-strip");
        Directory.CreateDirectory(withStrip);
        foreach (var (name, path, fill, zoom, filter, sort) in scenes)
        {
            Nested.SetFilter(filter);
            _viewModel.Sort = sort ?? ItemSort.Default;
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
            Save(NestedHost, Path.Combine(withStrip, name + ".png"));
        }

        // Written now: the run exits before the window is ever idle enough
        // to write it by itself.
        _viewModel.Sort = ItemSort.Default;
        await _viewModel.SaveNowAsync();
    }

    /// <summary>One view the snapshots render: where, how far in, and with what filter and order.</summary>
    private sealed record SnapshotScene(
        string Name,
        string? Path,
        double Fill,
        double Zoom,
        string Filter = "",
        ItemSort? Sort = null);

    private void Save(FrameworkElement element, string path)
    {
        // Every folder placed for the order first: a picture must never catch
        // the background pass half way.
        _nestedTree.FlushSortWork();
        element.UpdateLayout();
        Nested.RenderNow();
        var dpi = VisualTreeHelper.GetDpi(element);
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
}
