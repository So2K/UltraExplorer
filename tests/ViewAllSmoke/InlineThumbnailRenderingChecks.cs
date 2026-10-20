using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string InlineThumbnailRenderingChild = "ULTRAEXPLORER_INLINE_THUMBNAIL_RENDERING_CHILD";
    private const string InlineThumbnailCaptureDirectory = "INLINE_THUMBNAIL_CAPTURE_DIR";
    private const int InlineFixtureWidth = 1400;
    private const int InlineFixtureHeight = 900;
    private const int InlineFixtureImageCount = 9;

    /// <summary>
    /// Actual generated PNGs, the production thumbnail reader and the actual
    /// composited canvas. Everything lives in an isolated child. The optional
    /// window is cloaked before Show and refuses activation from its creation;
    /// screenshots render that canvas, never the user's desktop.
    /// </summary>
    private static async Task InlineThumbnailRenderingChecks()
    {
        if (Environment.GetEnvironmentVariable(InlineThumbnailRenderingChild) == "1")
        {
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
                throw new InvalidOperationException("Inline thumbnail checks require isolated state and nonactivating test-window mode.");
            RunOnSta("inline thumbnail rendering", InlineThumbnailRenderingOnStaAsync);
            return;
        }

        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerInlineThumbnails-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned, "temp"));
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(nameof(InlineThumbnailRenderingChecks));
            start.Environment[InlineThumbnailRenderingChild] = "1";
            start.Environment[AppPaths.StateDirectoryVariable] = Path.Combine(owned, "state");
            start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
            start.Environment["TEMP"] = Path.Combine(owned, "temp");
            start.Environment["TMP"] = Path.Combine(owned, "temp");
            child = Process.Start(start)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var finished = false;
            while (await child.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line.EndsWith(" checks passed", StringComparison.Ordinal)) finished = true;
                else if (line.Length > 0) Console.WriteLine(line);
            }
            await child.WaitForExitAsync(limit.Token);
            Check("inline thumbnail rendering reaches its isolated-process end", finished && child.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            Check("inline thumbnail rendering finishes within five minutes", false);
        }
        finally { child?.Dispose(); TryDelete(owned); }
    }

    private static async Task InlineThumbnailRenderingOnStaAsync()
    {
        Section("inline thumbnails: actual file pixels at strong zoom, without hover");
        var directory = Path.Combine(AppPaths.StateDirectory, "inline-rendering-fixture");
        Directory.CreateDirectory(directory);
        string[] names = ["01 alpine landscape.png", "02 ocean sunset.png", "03 geometric study.png",
            "04 portrait garden.png", "05 golden dunes.png", "06 night skyline.png",
            "07 red planet.png", "08 forest lake.png", "09 blue mosaic.png"];
        for (var index = 0; index < names.Length; index++) InlineWritePicture(Path.Combine(directory, names[index]), index);
        var unsupported = Path.Combine(directory, "10 unsupported.inline-unknown");
        await File.WriteAllTextAsync(unsupported, "Owned fixture with no content thumbnail provider.");

        using var service = new FileThumbnailService(new FileThumbnailOptions { WorkerCount = 2 });
        var requests = new ConcurrentQueue<string>();
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ThumbnailResult?> Fetch(string path, CancellationToken token)
        {
            requests.Enqueue(path);
            await allow.Task.WaitAsync(token);
            // A deterministic unsupported fixture; real PNGs take the real
            // production metadata, decoder and frozen bitmap worker path.
            return path == unsupported ? null : await service.GetAsync(path, token);
        }
        using var tree = new NestedTree { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(directory, "Visual references", NestedFolderKind.Drive)]);
        var folder = tree.Find(directory)!;
        await tree.LoadAsync(folder);
        using var canvasScope = new InlineCanvasScope(new NestedCanvas
        { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) });
        var canvas = canvasScope.Canvas;
        InlineArrange(canvas);
        canvas.SetInlineThumbnailSource(Fetch);
        canvas.FlyTo(folder, 0.92, animated: false);
        InlineSetTileHeight(canvas, tree, folder, names[0], 50);
        await InlinePumpAsync(canvas, 180);
        Check("ordinary small cards do not start any thumbnail reads", requests.IsEmpty && !canvas.InlineThumbnailModeForTests);
        InlineCapture(canvas, "01-overview-icons.png");

        InlineSetTileHeight(canvas, tree, folder, names[0], 88);
        var framedCards = folder.Files.Select(file => FocusFileRect(canvas, tree, Path.Combine(directory, file.Name))).ToArray();
        Check("the close-up fixture fully frames all ten cards and the folder heading",
            framedCards.Length == 10 && framedCards.All(card => card is { } rect
                && new Rect(0, 0, InlineFixtureWidth, InlineFixtureHeight).Contains(rect))
            && canvas.ScreenRectOf(folder) is { Top: >= 0 });
        var selected = Path.Combine(directory, names[2]);
        canvas.SetSelection([selected], selected);
        canvas.RenderNow();
        var beforeCamera = canvas.CaptureCamera();
        var beforeCards = folder.Files.Select(file => FocusFileRect(canvas, tree, Path.Combine(directory, file.Name))).ToArray();
        var beforeSelection = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
        var pending = canvas.InlineThumbnailStatesForTests();
        Check("large visible cards reserve preview slots before their pixels arrive", pending.Count > 0 && pending.All(state => !state.Ready));
        await InlineWaitAsync(() => !requests.IsEmpty, canvas);
        allow.TrySetResult();
        var ready = await InlineWaitAsync(() => canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) == InlineFixtureImageCount, canvas);
        var states = canvas.InlineThumbnailStatesForTests();
        Check("real generated PNG content appears in at least six visible cards without a hover", ready && service.Diagnostics.Extractions >= 6);
        Check("thumbnail completion preserves camera, card geometry and the exact selected paths",
            beforeCamera == canvas.CaptureCamera() && beforeSelection.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path))
            && beforeCards.SequenceEqual(folder.Files.Select(file => FocusFileRect(canvas, tree, Path.Combine(directory, file.Name)))));
        Check("arriving content keeps the reserved image slots in the same positions",
            pending.Where(state => state.Slot.IntersectsWith(new Rect(0, 0, InlineFixtureWidth, InlineFixtureHeight)))
                .All(old => states.Any(now => now.Path == old.Path && now.Slot == old.Slot)));
        Check("unsupported content preserves its fallback card instead of painting fabricated content",
            !requests.Contains(unsupported) && states.All(state => state.Path != unsupported)
            && canvas.RecordLabelCallsForTests().Texts.Any(text => text.Text == Path.GetFileName(unsupported)));

        var picture = InlineCapture(canvas, "02-close-up-content-cpu.png");
        var cpuPixels = await InlineCompareSourcePatchesAsync(canvas, picture, service);
        Check("the source reference patches are flat interiors away from resized shape and checker edges", cpuPixels.SourcePatchesFlat);
        Check($"the actual composited CPU picture contains all source images ({cpuPixels.Compared} interior patches)",
            cpuPixels.Compared == InlineFixtureImageCount && cpuPixels.Match);
        Check("images retain their aspect ratio and remain inside their own reserved card slots",
            states.Where(state => state.Ready).All(state => state.Slot.Contains(state.Image)
                && state.Image.Width > 0 && state.Image.Height > 0));
        var portrait = states.FirstOrDefault(state => Path.GetFileName(state.Path) == names[3]);
        Check("a portrait image is letterboxed vertically rather than stretched into a landscape slot",
            portrait.Ready && Math.Abs(portrait.Image.Width / portrait.Image.Height - 280.0 / 440) < 0.001);

        var warmRequests = requests.Count;
        var warmExtractions = service.Diagnostics.Extractions;
        for (var index = 0; index < 8; index++) canvas.RenderNow();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var warmClock = Stopwatch.StartNew();
        for (var index = 0; index < 20; index++) canvas.RenderLabelsForTests(inMotion: false);
        warmClock.Stop();
        var warmBytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 20;
        await Task.Delay(180);
        Check($"warm label frames reuse content without another provider request ({warmBytes:N0} UI bytes/frame, {warmClock.Elapsed.TotalMilliseconds / 20:F2} ms/frame)",
            requests.Count == warmRequests && service.Diagnostics.Extractions == warmExtractions && warmBytes < 262_144);

        canvas.ShownLayers &= ~CanvasLayer.Icons;
        await InlinePumpAsync(canvas, 150);
        Check("turning off the Icons layer also hides inline content without reading another file",
            canvas.InlineThumbnailStatesForTests().Count == 0 && requests.Count == warmRequests);
        canvas.ShownLayers |= CanvasLayer.Icons;
        await InlineWaitAsync(() => canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) >= 6, canvas);

        InlineSetTileHeight(canvas, tree, folder, names[0], 70);
        await InlinePumpAsync(canvas, 150);
        Check("a small zoom back through the hysteresis band keeps the existing previews", canvas.InlineThumbnailModeForTests && canvas.InlineThumbnailStatesForTests().Any(state => state.Ready));
        InlineSetTileHeight(canvas, tree, folder, names[0], 59);
        await InlinePumpAsync(canvas, 150);
        Check("zooming back to compact cards removes inline content", !canvas.InlineThumbnailModeForTests && canvas.InlineThumbnailStatesForTests().Count == 0);

        InlineSetTileHeight(canvas, tree, folder, names[0], 88);
        await InlineWaitAsync(() => canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) >= 6, canvas);
        await InlineThumbnailDenseViewportChecksAsync(directory, service);
        await InlineThumbnailGpuAndLifetimeChecksAsync(canvas, tree, folder, names[0], requests, service);
    }

    private static async Task InlineThumbnailDenseViewportChecksAsync(string directory, FileThumbnailService service)
    {
        Section("inline thumbnails: viewport demand stays bounded in a 10,000-file folder");
        var root = Path.Combine(directory, "synthetic-dense");
        var files = Enumerable.Range(0, 10_000).Select(index => new NestedFile($"picture-{index:D5}.png", false, 1)).ToArray();
        using var tree = new NestedTree((_, _) => new NestedListing([], files.Length, 0, false) { Files = files }) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(root, "Dense fixture", NestedFolderKind.Drive)]);
        var folder = tree.Find(root)!;
        await tree.LoadAsync(folder);
        var source = await service.GetAsync(Path.Combine(directory, "01 alpine landscape.png")) ?? throw new InvalidOperationException("Fixture PNG was not decoded.");
        using var scope = new InlineCanvasScope(new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) });
        var canvas = scope.Canvas;
        InlineArrange(canvas);
        var calls = new ConcurrentQueue<string>();
        canvas.SetInlineThumbnailSource((path, _) => { calls.Enqueue(path); return Task.FromResult<ThumbnailResult?>(source); });
        canvas.FlyTo(folder, 0.92, animated: false);
        InlineSetTileHeight(canvas, tree, folder, files[0].Name, 88);
        var movingClock = Stopwatch.StartNew();
        do
        {
            canvas.RenderLabelsForTests(inMotion: true);
            await Task.Delay(15);
        } while (movingClock.ElapsedMilliseconds < 200);
        Check("enlarged cards request no file content while the camera frames are moving", calls.IsEmpty);
        await InlinePumpAsync(canvas, 350);
        var demanded = calls.Distinct(StringComparer.Ordinal).ToArray();
        var viewport = new Rect(0, 0, InlineFixtureWidth, InlineFixtureHeight);
        Check($"a 10,000-file listing requests only visible content, bounded to 24 paths ({demanded.Length} requested)",
            demanded.Length is > 0 and <= 24 && demanded.All(path => FocusFileRect(canvas, tree, path) is { } card && card.IntersectsWith(viewport)));
        var count = calls.Count;
        for (var index = 0; index < 8; index++) canvas.RenderNow();
        await Task.Delay(160);
        Check("repainting the same dense viewport does not requeue its warm content", calls.Count == count);
        canvas.Pan(new Vector(-450, -150));
        canvas.RenderNow();
        var moved = canvas.InlineThumbnailStatesForTests();
        Check("panning keeps every recorded thumbnail attached to its own currently visible card",
            moved.Count > 0 && moved.All(state => FocusFileRect(canvas, tree, state.Path) is { } card
                && card.IntersectsWith(viewport) && card.Contains(state.Slot)));
    }

    private static async Task InlineThumbnailGpuAndLifetimeChecksAsync(NestedCanvas canvas, NestedTree tree,
        NestedFolder folder, string firstName, ConcurrentQueue<string> requests, FileThumbnailService service)
    {
        Section("inline thumbnails: actual GPU composition and retained versus closed canvas lifetime");
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var panel = new Grid();
        panel.Children.Add(canvas);
        var window = new Window
        {
            Content = panel, Width = InlineFixtureWidth, Height = InlineFixtureHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Title = "UltraExplorer isolated inline thumbnail capture"
        };
        var oldPreference = GpuBootstrap.Preference;
        nint handle = 0;
        try
        {
            if (!GpuBootstrap.IsStarted)
            {
                GpuLabelAtlases.RegisterWarmUp();
                NestedGpuRenderer.RegisterWarmUp();
            }
            GpuLabelAtlases.StartWarmUp();
            using (ActivationGuard.GuardWindowsCreated())
            {
                handle = new WindowInteropHelper(window).EnsureHandle();
                if (!DialogNative.CloakOwn(handle, true)) throw new InvalidOperationException("Owned test window could not be cloaked before Show.");
                window.Show();
            }
            Check("the owned capture window is cloaked and cannot take foreground activation",
                DialogNative.IsCloaked(handle) && ActivationGuard.IsGuarded(handle) && InlineThumbnailForegroundWindow() != handle);
            window.UpdateLayout();
            InlineSetTileHeight(canvas, tree, folder, firstName, 88);
            var gpu = await InlineWaitAsync(() => canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu, canvas, 20_000);
            await InlineWaitAsync(() => canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) == InlineFixtureImageCount, canvas);
            var rendered = InlineCapture(canvas, gpu ? "03-close-up-content-gpu.png" : "03-close-up-content-window-cpu-fallback.png");
            Check("the actual window composition retains six or more inline content images",
                canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) >= 6 && rendered.PixelWidth == InlineFixtureWidth);
            var windowPixels = await InlineCompareSourcePatchesAsync(canvas, rendered, service);
            Check("the window's source reference patches remain flat interiors", windowPixels.SourcePatchesFlat);
            Check($"the actual window picture contains every source image over its scene ({windowPixels.Compared} interior patches)",
                windowPixels.Compared == InlineFixtureImageCount && windowPixels.Match);
            Console.WriteLine($"  note  actual renderer: sceneGPU={canvas.IsSceneOnGpu}, labelsGPU={canvas.AreLabelsOnGpu}; {canvas.RendererReason}");
            if (gpu) Check("hardware GPU scene and GPU labels coexist with the composited inline content", canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu);

            var camera = canvas.CaptureCamera();
            var selected = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
            var warm = requests.Count;
            canvas.Visibility = Visibility.Hidden;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check("a hidden retained canvas clears displayed previews", canvas.InlineThumbnailStatesForTests().Count == 0);
            canvas.Visibility = Visibility.Visible;
            window.UpdateLayout();
            await InlineWaitAsync(() => canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) >= 6, canvas);
            Check("a retained canvas returns with its warm content, camera and selection",
                canvas.InlineThumbnailStatesForTests().Count(state => state.Ready) >= 6
                && requests.Count == warm && camera == canvas.CaptureCamera() && selected.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path)));
            canvas.Dispose();
            await Dispatcher.Yield(DispatcherPriority.Background);
            canvas.RenderNow();
            Check("permanent closure clears the inline recording and late render calls cannot revive it",
                canvas.InlineThumbnailStatesForTests().Count == 0 && canvas.Tree is null);
            Check("permanent closure immediately releases its bounded presentation cache without a GC",
                canvas.InlineThumbnailDiagnosticsForTests.CacheBytes == 0 && canvas.InlineThumbnailDiagnosticsForTests.CacheEntries == 0);
            Check("the capture never activated its hidden window", InlineThumbnailForegroundWindow() != handle && DialogNative.IsCloaked(handle));
        }
        finally
        {
            GpuBootstrap.UseSavedPreference(oldPreference);
            window.Close();
            panel.Children.Clear();
        }
    }

    private static void InlineArrange(NestedCanvas canvas)
    {
        canvas.Measure(new Size(InlineFixtureWidth, InlineFixtureHeight));
        canvas.Arrange(new Rect(0, 0, InlineFixtureWidth, InlineFixtureHeight));
        canvas.UpdateLayout();
        canvas.RenderNow();
    }

    private static void InlineSetTileHeight(NestedCanvas canvas, NestedTree tree, NestedFolder folder, string name, double height)
    {
        canvas.RenderNow();
        var path = Path.Combine(folder.FullPath, name);
        var card = FocusFileRect(canvas, tree, path) ?? throw new InvalidOperationException("Fixture card has no screen rectangle.");
        canvas.ZoomAt(new Point(InlineFixtureWidth / 2.0, InlineFixtureHeight / 2.0), height / card.Height);
        canvas.RenderNow();
        if (folder.Files.Count <= 16)
        {
            // Zooming about the viewport centre may push the first column's
            // images offscreen. Frame the small visual fixture explicitly:
            // all ten cards fit, and the folder's title band remains visible.
            // This is the canvas API only, never pointer/desktop interaction.
            var enlarged = FocusFileRect(canvas, tree, path) ?? throw new InvalidOperationException("Zoomed fixture card has no screen rectangle.");
            canvas.Pan(new Vector(60 - enlarged.X, 170 - enlarged.Y));
            canvas.RenderNow();
        }
    }

    private static async Task InlinePumpAsync(NestedCanvas canvas, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do { canvas.RenderNow(); await Task.Delay(15); } while (clock.ElapsedMilliseconds < milliseconds);
    }

    private static async Task<bool> InlineWaitAsync(Func<bool> ready, NestedCanvas canvas, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            canvas.RenderNow();
            if (ready()) return true;
            await Task.Delay(15);
        } while (clock.ElapsedMilliseconds < milliseconds);
        return false;
    }

    private static RenderTargetBitmap InlineCapture(NestedCanvas canvas, string name)
    {
        canvas.RenderNow();
        var picture = new RenderTargetBitmap(InlineFixtureWidth, InlineFixtureHeight, 96, 96, PixelFormats.Pbgra32);
        picture.Render(canvas);
        picture.Freeze();
        if (Environment.GetEnvironmentVariable(InlineThumbnailCaptureDirectory) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using (var output = File.Create(Path.Combine(directory, name)))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                encoder.Save(output);
            }
            File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
            {
                actualSceneOnGpu = canvas.IsSceneOnGpu, actualLabelsOnGpu = canvas.AreLabelsOnGpu,
                rendererReason = canvas.RendererReason, width = picture.PixelWidth, height = picture.PixelHeight,
                previews = canvas.InlineThumbnailStatesForTests().Select(state => new
                { fileName = Path.GetFileName(state.Path), slot = state.Slot.ToString(), image = state.Image.ToString(), state.Ready })
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  note  isolated canvas capture: {Path.Combine(directory, name)}");
        }
        return picture;
    }

    private static async Task<(int Compared, bool Match, bool SourcePatchesFlat)> InlineCompareSourcePatchesAsync(
        NestedCanvas canvas, BitmapSource picture, FileThumbnailService service)
    {
        var compared = 0;
        var match = true;
        var flat = true;
        var selected = canvas.SelectionState.Items().Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var selectionBrush = (SolidColorBrush)typeof(NestedCanvas).GetField("SelectedTileBrush", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var selection = selectionBrush.Color;
        var selectionRgb = 0xFF000000u | (uint)selection.R << 16 | (uint)selection.G << 8 | selection.B;
        foreach (var state in canvas.InlineThumbnailStatesForTests().Where(state => state.Ready))
        {
            var decoded = await service.GetAsync(state.Path);
            if (decoded is null) { match = false; continue; }
            var name = Path.GetFileName(state.Path);
            // Geometric/checker centres sit exactly on cell boundaries. A
            // one-time 256px downsample followed by high-quality card scaling
            // legitimately blends those pixels. These fixture-defined points
            // lie inside a solid checker cell or clear sky/background instead.
            // Compare the original decoded file, not the controller's bitmap.
            var checker = name.StartsWith("03 ", StringComparison.Ordinal) || name.StartsWith("09 ", StringComparison.Ordinal);
            var u = checker ? 1.0 / 16 : 0.16;
            var v = checker ? 1.0 / 12 : 0.17;
            var sourceX = (int)Math.Round((decoded.Image.PixelWidth - 1) * u);
            var sourceY = (int)Math.Round((decoded.Image.PixelHeight - 1) * v);
            var sourceColor = Pixel(decoded.Image, sourceX, sourceY);
            // The existing selection is intentionally a translucent Decor
            // fill above the entire card, including its content image. Use
            // that actual brush in the reference composition rather than
            // incorrectly requiring an unselected source colour underneath it.
            var expected = selected.Contains(state.Path) ? Blend(sourceColor, selectionRgb, selection.A) : sourceColor;
            var x = (int)Math.Floor(state.Image.X + state.Image.Width * u);
            var y = (int)Math.Floor(state.Image.Y + state.Image.Height * v);
            if (x < 1 || y < 1 || x + 1 >= picture.PixelWidth || y + 1 >= picture.PixelHeight)
            {
                match = false;
                Console.WriteLine($"  note  source patch outside owned capture: {name}, ({x},{y})");
                continue;
            }
            var fileMatch = true;
            var fileFlat = true;
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
            {
                fileFlat &= SameWithin(Pixel(decoded.Image, sourceX + dx, sourceY + dy), sourceColor, 0);
                fileMatch &= SameWithin(Pixel(picture, x + dx, y + dy), expected, 12);
            }
            if (!fileMatch || !fileFlat)
                Console.WriteLine($"  note  source patch {name}: source {sourceColor:X8}, composited {expected:X8}, actual {Pixel(picture, x, y):X8}, selected={selected.Contains(state.Path)}, sourceFlat={fileFlat}, patchMatch={fileMatch}");
            match &= fileMatch;
            flat &= fileFlat;
            compared++;
        }
        return (compared, match, flat);
    }

    private static void InlineWritePicture(string path, int index)
    {
        var width = index == 3 ? 280 : 480;
        var height = index == 3 ? 440 : 300;
        var palettes = new[]
        {
            (Color.FromRgb(115, 185, 220), Color.FromRgb(50, 103, 85)),
            (Color.FromRgb(244, 148, 82), Color.FromRgb(29, 112, 153)),
            (Color.FromRgb(100, 77, 151), Color.FromRgb(229, 193, 87)),
            (Color.FromRgb(187, 215, 170), Color.FromRgb(88, 139, 78)),
            (Color.FromRgb(245, 216, 158), Color.FromRgb(185, 116, 53)),
            (Color.FromRgb(35, 43, 83), Color.FromRgb(88, 104, 153)),
            (Color.FromRgb(55, 30, 49), Color.FromRgb(193, 82, 54)),
            (Color.FromRgb(105, 172, 168), Color.FromRgb(32, 98, 77)),
            (Color.FromRgb(65, 125, 184), Color.FromRgb(135, 213, 218))
        };
        var (sky, subject) = palettes[index];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(sky), null, new Rect(0, 0, width, height));
            if (index == 2 || index == 8)
            {
                for (var y = 0; y < 6; y++) for (var x = 0; x < 8; x++)
                    if ((x + y + index) % 3 != 0) dc.DrawRoundedRectangle(new SolidColorBrush(subject), null,
                        new Rect(x * width / 8.0 + 3, y * height / 6.0 + 3, width / 8.0 - 6, height / 6.0 - 6), 7, 7);
            }
            else if (index == 3)
            {
                dc.DrawRectangle(new SolidColorBrush(subject), null, new Rect(width * 0.47, height * 0.3, width * 0.06, height * 0.7));
                for (var petal = 0; petal < 7; petal++)
                {
                    var angle = petal * Math.PI * 2 / 7;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(219, 102, 126)), null,
                        new Point(width * 0.5 + Math.Cos(angle) * 53, height * 0.32 + Math.Sin(angle) * 53), 37, 37);
                }
                dc.DrawEllipse(Brushes.Gold, null, new Point(width * 0.5, height * 0.32), 29, 29);
            }
            else if (index == 5)
            {
                for (var x = 0; x < 10; x++) dc.DrawRectangle(new SolidColorBrush(subject), null,
                    new Rect(x * 52, 160 - (x * 31 % 95), 43, 200));
                dc.DrawEllipse(Brushes.LightGoldenrodYellow, null, new Point(370, 65), 25, 25);
            }
            else if (index == 6)
            {
                dc.DrawEllipse(new SolidColorBrush(subject), null, new Point(width * 0.5, height * 0.5), 107, 107);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(151, 61, 48)), null, new Point(width * 0.59, height * 0.39), 28, 20);
            }
            else
            {
                dc.DrawEllipse(Brushes.LightGoldenrodYellow, null, new Point(width * 0.77, height * 0.24), 30, 30);
                var geometry = new StreamGeometry();
                using (var pen = geometry.Open())
                {
                    pen.BeginFigure(new Point(0, height), true, true);
                    pen.PolyLineTo([new Point(width * 0.29, height * 0.28), new Point(width * 0.51, height * 0.69),
                        new Point(width * 0.74, height * 0.37), new Point(width, height)], true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(subject), null, geometry);
                if (index is 1 or 7) dc.DrawRectangle(new SolidColorBrush(subject), null, new Rect(0, height * 0.64, width, height * 0.36));
            }
        }
        var picture = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        picture.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class InlineCanvasScope(NestedCanvas canvas) : IDisposable
    {
        public NestedCanvas Canvas { get; } = canvas;
        public void Dispose() => Canvas.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint InlineThumbnailForegroundWindow();
}
