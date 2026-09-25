using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Starts a benchmark or snapshot run if one was asked for; true when it did.</summary>
    private bool TryStartNestedDiagnostics()
    {
        var bench = SwitchValue("--nested-bench");
        var snapshots = SwitchValue("--nested-snapshots");
        if (bench is null && snapshots is null)
        {
            return false;
        }

        // Always the primary monitor, at the same place and size: two runs on
        // monitors of different scale are different pictures and different
        // amounts of work, and could not be compared.
        WindowState = WindowState.Normal;
        Left = 60;
        Top = 60;
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

    /// <summary>Waits until nothing is being read and nothing has changed for a moment.</summary>
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
            if (_nestedTree.PendingCount > 0 || Nested.RenderCount != lastRenders && _treeChangedRecently)
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

        foreach (var phase in phases)
        {
            report.AppendLine(phase.Row());
        }

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

        var scenes = new (string Name, string? Path, double Fill, double Zoom, string Filter)[]
        {
            ("01-this-pc", null, 1, 1, ""),
            ("02-drive-c", @"C:\", 0.92, 1, ""),
            ("03-windows", @"C:\Windows", 0.92, 1, ""),
            ("04-system32", @"C:\Windows\System32", 0.92, 1, ""),
            ("05-system32-files", @"C:\Windows\System32", 0.92, 6, ""),
            ("06-fonts", @"C:\Windows\Fonts", 0.92, 1, ""),
            ("07-program-files", @"C:\Program Files", 0.92, 1, ""),
            ("08-winsxs", @"C:\Windows\WinSxS", 0.92, 1, ""),
            ("09-winsxs-deep", @"C:\Windows\WinSxS", 0.92, 40, ""),
            ("10-etc", @"C:\Windows\System32\drivers\etc", 0.6, 1, ""),
            ("11-filter-exe", @"C:\Windows\System32", 0.92, 1, "*.exe"),
            ("12-filter-this-pc", null, 1, 1, "config"),
        };

        foreach (var (name, path, fill, zoom, filter) in scenes)
        {
            Nested.SetFilter(filter);
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
            Save(Path.Combine(folder, name + ".png"));
        }
    }

    private void Save(string path)
    {
        Nested.UpdateLayout();
        Nested.RenderNow();
        var dpi = VisualTreeHelper.GetDpi(Nested);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Nested.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(Nested.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        // Through a brush, so the canvas's own offset under the filter strip
        // is not part of the picture.
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(Nested), null, new Rect(0, 0, Nested.ActualWidth, Nested.ActualHeight));
        }

        bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
