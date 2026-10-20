using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private const string FileDetailChild = "ULTRAEXPLORER_FILE_DETAIL_ZOOM_CHILD";
    private const string FileDetailCaptureDirectory = "FILE_DETAIL_CAPTURE_DIR";
    private const int FileDetailWidth = 1400, FileDetailHeight = 900;

    private static async Task FileDetailZoomChecks()
    {
        if (Environment.GetEnvironmentVariable(FileDetailChild) == "1")
        {
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
                throw new InvalidOperationException("File detail checks require isolated state and nonactivating test-window mode.");
            RunOnSta("file detail zoom", FileDetailZoomOnStaAsync);
            return;
        }
        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerFileDetail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned, "temp"));
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--only"); start.ArgumentList.Add(nameof(FileDetailZoomChecks));
            start.Environment[FileDetailChild] = "1";
            start.Environment[AppPaths.StateDirectoryVariable] = Path.Combine(owned, "state");
            start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
            start.Environment["TEMP"] = Path.Combine(owned, "temp"); start.Environment["TMP"] = Path.Combine(owned, "temp");
            child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var finished = false;
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line.EndsWith(" checks passed", StringComparison.Ordinal)) finished = true;
                else if (line.Length > 0) Console.WriteLine(line);
            }
            await child.WaitForExitAsync(timeout.Token);
            Check("file detail zoom reaches its isolated-process end", finished && child.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            Check("file detail zoom finishes within five minutes", false);
        }
        finally { child?.Dispose(); TryDelete(owned); }
    }

    private static async Task FileDetailZoomOnStaAsync()
    {
        Section("file detail: zoom intent follows the actual file and grows its content");
        var root = Path.Combine(AppPaths.StateDirectory, "file-detail-fixture");
        var images = Path.Combine(root, "references");
        var neighbor = Path.Combine(root, "neighbor");
        Directory.CreateDirectory(images);
        var picturePath = Path.Combine(images, "01 alpine landscape.png");
        var textPath = Path.Combine(images, "02 project notes.txt");
        var siblingPath = Path.Combine(images, "03 geometric study.png");
        FileDetailWriteLargePicture(picturePath);
        InlineWritePicture(siblingPath, 2);
        var notes = "PROJECT NOTES / Заметки проекта\n\nZoom changes the card, not the reading size.\n"
            + "Text stays readable at a fixed size while the file grows.\n\n"
            + string.Join('\n', Enumerable.Range(1, 36).Select(index => $"{index:D2}  Inspect content, keep the original selection and camera."));
        await File.WriteAllTextAsync(textPath, notes, new UTF8Encoding(false));
        var disk = new FakeDisk();
        foreach (var path in new[] { picturePath, textPath, siblingPath })
        {
            var info = new FileInfo(path);
            disk.AddFile(images, info.Name, info.Length, modified: info.LastWriteTimeUtc);
        }
        disk.AddFiles(neighbor, 10_000, "entry-");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(root, "Owned content detail fixture", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var folder = tree.Find(images)!;
        using var thumbnails = new FileThumbnailService();
        var requests = new ConcurrentQueue<string>();
        async Task<FocusedFileDetailResult?> Fetch(string path, CancellationToken token)
        {
            requests.Enqueue(path);
            return await FocusedFileDetailProvider.GetAsync(path, token);
        }
        using var scope = new InlineCanvasScope(new NestedCanvas
        { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) });
        var canvas = scope.Canvas;
        FileDetailArrange(canvas);
        canvas.SetInlineThumbnailSource(thumbnails.GetAsync);
        canvas.SetFocusedFileDetailSource(Fetch);
        canvas.FlyTo(folder, .92, animated: false);
        FileDetailZoomToHeight(canvas, tree, picturePath, 88);
        await FileDetailWaitAsync(() => canvas.InlineThumbnailStatesForTests().Any(state => state.Path == picturePath && state.Ready), canvas);
        Check("the ordinary file view retains its compact thumbnail and starts no detail extraction",
            canvas.FocusedFileDetailStateForTests is null && requests.IsEmpty
            && canvas.InlineThumbnailStatesForTests().Any(state => state.Path == picturePath && state.Slot.Width <= 104));
        canvas.SetSelection([picturePath], picturePath);
        canvas.RenderNow();
        var selection = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
        FileDetailCapture(canvas, "01-file-normal-thumbnail.png");

        FileDetailZoomToHeight(canvas, tree, picturePath, 240);
        var camera = canvas.CaptureCamera();
        var card = FocusFileRect(canvas, tree, picturePath)!.Value;
        var ready = await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: false }, canvas);
        var small = canvas.FocusedFileDetailStateForTests;
        Check("direct zoom goes beyond the ordinary cap into the literal file, rather than its container",
            canvas.FileDetailTargetForTests == picturePath && card.Height > 200 && ready && small?.Path == picturePath);
        Check("detail completion preserves the exact camera, file geometry, hit identity and selection",
            camera == canvas.CaptureCamera() && FocusFileRect(canvas, tree, picturePath) == card
            && canvas.HitTest(FileDetailVisiblePoint(card)) is { IsFile: true } hit && hit.Path == picturePath
            && selection.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path)));
        Check("only the targeted file expands; visible siblings keep their bounded normal thumbnails",
            canvas.InlineThumbnailStatesForTests().All(state => state.Path != picturePath && state.Slot.Width <= 104)
            && canvas.InlineThumbnailStatesForTests().Any(state => state.Path == textPath || state.Path == siblingPath));
        FileDetailCheckHeaderFont(canvas, tree, picturePath);
        FileDetailCapture(canvas, "02-file-image-detail-240-cpu.png");

        var wheelBefore = card.Height;
        // This is the canvas's model-level wheel method, not OS input.
        canvas.Pointer.Wheel(FileDetailVisiblePoint(card), 120, ModifierKeys.Control);
        canvas.RenderNow();
        var wheeled = FocusFileRect(canvas, tree, picturePath)!.Value;
        Check("Ctrl-wheel over the same actual file keeps the file target and continues zooming beyond the folder limit",
            canvas.FileDetailTargetForTests == picturePath && wheeled.Height > wheelBefore * 1.1);

        FileDetailZoomToHeight(canvas, tree, picturePath, 720);
        await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: false }, canvas);
        var large = canvas.FocusedFileDetailStateForTests;
        Check("further file zoom grows the image itself instead of retaining a 104-pixel preview",
            small is { } first && large is { } next && next.Image.Width > first.Image.Width * 2.8
            && next.Image.Height > first.Image.Height * 2.8 && next.Image.Width > 500);
        Check("image aspect ratio and fixed 13-DIP filename survive deep file zoom",
            large is { } imageState && Math.Abs(imageState.Image.Width / imageState.Image.Height - 1.6) < .001
            && imageState.HeaderFontSize == 13 && imageState.Header.Height == 30);
        FileDetailCheckHeaderFont(canvas, tree, picturePath);
        var imageCapture = FileDetailCapture(canvas, "03-file-image-detail-720-cpu.png");
        Check("the composited deep image contains its original source pixels underneath the ordinary selection tint",
            large is { } sampled && FileDetailImagePatch(canvas, sampled.Image, imageCapture, 0xFF4D93BC));
        Check("the single focused result retains high-resolution pixels within the 16-MiB budget",
            canvas.FocusedFileDetailDiagnosticsForTests.CacheBytes > 256L * 256 * 4
            && canvas.FocusedFileDetailDiagnosticsForTests.CacheBytes <= 16L * 1024 * 1024);

        var deepPoint = FileDetailVisiblePoint(FocusFileRect(canvas, tree, picturePath)!.Value);
        canvas.ZoomAt(deepPoint, 1000);
        canvas.RenderNow();
        var maximum = FocusFileRect(canvas, tree, picturePath)!.Value.Height;
        Check("file-specific magnification has a finite bounded maximum", maximum > 2048 && maximum <= 8 * FileDetailHeight + .01);
        var gradual = true;
        for (var index = 0; index < 3; index++)
        {
            var previous = FocusFileRect(canvas, tree, picturePath)!.Value.Height;
            canvas.ZoomAt(FileDetailVisiblePoint(FocusFileRect(canvas, tree, picturePath)!.Value), .9);
            canvas.RenderNow();
            var current = FocusFileRect(canvas, tree, picturePath)!.Value.Height;
            gradual &= Math.Abs(current / previous - .9) < .001 && current > 160;
        }
        Check("zooming back from a deep file remains gradual and never snaps to the ordinary folder cap", gradual);

        var neighborFolder = tree.Find(neighbor)!;
        canvas.FlyTo(neighborFolder, .92, animated: false);
        canvas.RenderNow();
        var normalHeader = canvas.ScreenRectOf(neighborFolder)!.Value;
        canvas.ZoomAt(new Point(normalHeader.Left + 30, normalHeader.Top + 20), 1000);
        canvas.RenderNow();
        Check("zooming toward a neighboring folder clears file intent and retains the ordinary dense-file limit",
            canvas.FileDetailTargetForTests is null && canvas.FocusedFileDetailStateForTests is null
            && canvas.ScreenRectOf(neighborFolder)!.Value.Width * neighborFolder.FileGrid.TileHeight <= 160.01);

        canvas.FlyTo(folder, .92, animated: false);
        FileDetailZoomToHeight(canvas, tree, textPath, 240);
        await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: true }, canvas);
        var smallText = canvas.FocusedFileDetailStateForTests;
        var smallFonts = FileDetailDocumentFonts(canvas);
        var textSmallCapture = FileDetailCapture(canvas, "04-file-text-detail-240-cpu.png");
        Check("zooming into the actual text file renders its extracted document at readable fixed text size",
            smallText is { Document: true, TextFontSize: 14, TextLineHeight: 20, HeaderFontSize: 13 }
            && smallFonts.Count > 0 && smallFonts.All(size => Math.Abs(size - 14) < .001)
            && FileDetailTextInk(textSmallCapture, smallText.Value.Content) > 150);
        FileDetailZoomToHeight(canvas, tree, textPath, 720);
        await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: true }, canvas);
        var bigText = canvas.FocusedFileDetailStateForTests;
        var fonts = FileDetailDocumentFonts(canvas);
        var textCapture = FileDetailCapture(canvas, "05-file-text-detail-720-cpu.png");
        Check("deep text zoom grows the reading region while glyphs and 20-DIP lines remain unscaled",
            smallText is { } earlier && bigText is { TextFontSize: 14, TextLineHeight: 20, HeaderFontSize: 13 } currentText
            && currentText.Content.Width > earlier.Content.Width * 2.8
            && fonts.Count > 0 && fonts.All(size => Math.Abs(size - 14) < .001)
            && FileDetailTextInk(textCapture, currentText.Content) > 1000);
        FileDetailCheckHeaderFont(canvas, tree, textPath);
        var warm = requests.Count;
        for (var index = 0; index < 12; index++) canvas.RenderLabelsForTests(inMotion: index % 2 == 1);
        await Task.Delay(150);
        Check("warm text and image redraws reuse their focused result without another decoder request", requests.Count == warm);
        Check("zoom intent stays independent of selection while inspecting another file",
            canvas.FileDetailTargetForTests == textPath && selection.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path)));

        await FileDetailGpuAndLifetimeAsync(canvas, tree, folder, picturePath, textPath);
        await FileDetailLiteralIdentityAsync(disk, images, picturePath);
    }

    private static async Task FileDetailGpuAndLifetimeAsync(NestedCanvas canvas, NestedTree tree, NestedFolder folder,
        string picturePath, string textPath)
    {
        Section("file detail: actual GPU composition, retained camera and permanent cleanup");
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!GpuBootstrap.IsStarted) { GpuLabelAtlases.RegisterWarmUp(); NestedGpuRenderer.RegisterWarmUp(); }
        GpuLabelAtlases.StartWarmUp();
        var panel = new Grid(); panel.Children.Add(canvas);
        var window = new Window
        {
            Content = panel, Width = FileDetailWidth, Height = FileDetailHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Title = "UltraExplorer isolated file detail capture"
        };
        nint handle = 0;
        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                handle = new WindowInteropHelper(window).EnsureHandle();
                if (!DialogNative.CloakOwn(handle, true)) throw new InvalidOperationException("Owned detail window could not be cloaked.");
                window.Show();
            }
            window.UpdateLayout();
            Check("the owned detail capture window is cloaked, guarded and never foreground",
                DialogNative.IsCloaked(handle) && ActivationGuard.IsGuarded(handle) && FileDetailForegroundWindow() != handle);
            var gpu = await FileDetailWaitAsync(() => canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu, canvas, 20_000);
            await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: true }, canvas);
            var text = FileDetailCapture(canvas, gpu ? "06-file-text-detail-gpu.png" : "06-file-text-detail-window-cpu-fallback.png");
            Check("the actual window composition paints readable fixed-size text over its scene",
                canvas.FocusedFileDetailStateForTests is { Document: true, TextFontSize: 14 } state
                && FileDetailTextInk(text, state.Content) > 1000);
            canvas.FlyTo(folder, .92, animated: false);
            FileDetailZoomToHeight(canvas, tree, picturePath, 720);
            await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true, Document: false }, canvas);
            var image = FileDetailCapture(canvas, gpu ? "07-file-image-detail-gpu.png" : "07-file-image-detail-window-cpu-fallback.png");
            Check("the actual window composition paints the enlarged source image over its scene",
                canvas.FocusedFileDetailStateForTests is { } imageState && FileDetailImagePatch(canvas, imageState.Image, image, 0xFF4D93BC));
            Console.WriteLine($"  note  detail renderer sceneGPU={canvas.IsSceneOnGpu}, labelsGPU={canvas.AreLabelsOnGpu}; {canvas.RendererReason}");
            if (gpu) Check("GPU cells and GPU filenames coexist with the growing high-resolution content", canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu);

            var camera = canvas.CaptureCamera();
            var selected = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
            canvas.Visibility = Visibility.Hidden;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check("hiding a retained tab drops detail results while preserving the literal camera target",
                canvas.FocusedFileDetailStateForTests is null && canvas.FocusedFileDetailDiagnosticsForTests.CacheBytes == 0
                && canvas.FileDetailTargetForTests == picturePath && canvas.CaptureCamera() == camera);
            canvas.Visibility = Visibility.Visible; window.UpdateLayout();
            await FileDetailWaitAsync(() => canvas.FocusedFileDetailStateForTests is { Ready: true }, canvas);
            Check("returning to the retained tab restores details with the same camera, target and selection",
                canvas.FocusedFileDetailStateForTests is { Ready: true } && canvas.CaptureCamera() == camera
                && canvas.FileDetailTargetForTests == picturePath
                && selected.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path)));
            canvas.Dispose();
            await Dispatcher.Yield(DispatcherPriority.Background);
            canvas.RenderNow();
            Check("permanent close frees detail pixels and target references without a collection",
                canvas.FileDetailTargetForTests is null && canvas.FocusedFileDetailStateForTests is null
                && canvas.FocusedFileDetailDiagnosticsForTests.CacheBytes == 0 && !canvas.FocusedFileDetailDiagnosticsForTests.HasDemand);
            Check("the entire detail capture left user foreground ownership untouched", FileDetailForegroundWindow() != handle);
        }
        finally { window.Close(); panel.Children.Clear(); }
    }

    private static async Task FileDetailLiteralIdentityAsync(FakeDisk disk, string images, string original)
    {
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(images, "Literal target fixture", NestedFolderKind.Drive)]);
        var folder = tree.Find(images)!;
        await tree.LoadAsync(folder);
        using var scope = new InlineCanvasScope(new NestedCanvas { Tree = tree, FramesByHandForTests = true });
        var canvas = scope.Canvas;
        FileDetailArrange(canvas); canvas.FlyTo(folder, .92, animated: false);
        FileDetailZoomToHeight(canvas, tree, original, 240);
        Check("the literal-identity fixture holds its exact mixed-case filename", canvas.FileDetailTargetForTests == original);
        disk.Folder(images).Files.RemoveAll(file => file.Name == Path.GetFileName(original));
        disk.AddFile(images, Path.GetFileName(original).ToUpperInvariant(), 1);
        await tree.RefreshAsync(folder);
        canvas.RenderNow();
        Check("refresh cannot silently retarget a removed filename to a differently cased twin", !canvas.ValidateFileDetailTarget() && canvas.FileDetailTargetForTests is null);
        canvas.Tree = null;
        Check("tree replacement leaves no prior file target", canvas.FileDetailTargetForTests is null);
    }

    private static void FileDetailArrange(NestedCanvas canvas)
    {
        canvas.Measure(new Size(FileDetailWidth, FileDetailHeight));
        canvas.Arrange(new Rect(0, 0, FileDetailWidth, FileDetailHeight));
        canvas.UpdateLayout(); canvas.RenderNow();
    }

    private static Point FileDetailVisiblePoint(Rect card)
    {
        var visible = Rect.Intersect(card, new Rect(0, 0, FileDetailWidth, FileDetailHeight));
        if (visible.IsEmpty) throw new InvalidOperationException("The target file is outside the owned fixture view.");
        return new Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
    }

    private static void FileDetailZoomToHeight(NestedCanvas canvas, NestedTree tree, string path, double height)
    {
        canvas.RenderNow();
        var before = FocusFileRect(canvas, tree, path) ?? throw new InvalidOperationException("The owned file has no card.");
        canvas.ZoomAt(FileDetailVisiblePoint(before), height / before.Height);
        canvas.RenderNow();
        var after = FocusFileRect(canvas, tree, path)!.Value;
        canvas.Pan(new Vector(60 - after.X, 140 - after.Y));
        canvas.RenderNow();
    }

    private static async Task<bool> FileDetailWaitAsync(Func<bool> ready, NestedCanvas canvas, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        do { canvas.RenderNow(); if (ready()) return true; await Task.Delay(15); } while (clock.ElapsedMilliseconds < milliseconds);
        return false;
    }

    private static void FileDetailCheckHeaderFont(NestedCanvas canvas, NestedTree tree, string path)
    {
        var folder = tree.Find(Path.GetDirectoryName(path)!)!;
        var index = tree.FindFileIndex(folder, Path.GetFileName(path));
        var card = FocusFileRect(canvas, tree, path)!.Value;
        var recorder = new FileDetailFontTarget();
        canvas.DrawFileLabelForTests(recorder, folder, index, card.Width, card.Height);
        Check("the actual filename layout requests 13-DIP unscaled type at this zoom",
            recorder.Texts.Any(text => text.Text == Path.GetFileName(path) && text.Size == 13 && !text.Scaled));
    }

    private static List<double> FileDetailDocumentFonts(NestedCanvas canvas)
    {
        var drawing = (DrawingGroup?)typeof(NestedCanvas).GetField("_focusedDocumentDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
        var sizes = new List<double>();
        void Read(Drawing value)
        {
            if (value is GlyphRunDrawing { GlyphRun: { } run }) sizes.Add(run.FontRenderingEmSize);
            if (value is DrawingGroup group) foreach (var child in group.Children) Read(child);
        }
        if (drawing is not null) Read(drawing);
        return sizes;
    }

    private static int FileDetailTextInk(BitmapSource picture, Rect content)
    {
        var clip = Rect.Intersect(content, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
        if (clip.IsEmpty) return 0;
        var left = Math.Max(0, (int)clip.Left + 8);
        var top = Math.Max(0, (int)clip.Top + 8);
        var width = Math.Min(picture.PixelWidth - left, (int)clip.Right - left - 5);
        var height = Math.Min(picture.PixelHeight - top, (int)clip.Bottom - top - 5);
        if (width <= 0 || height <= 0) return 0;
        var pixels = new uint[width * height];
        picture.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        var count = 0;
        foreach (var pixel in pixels)
            if (((pixel >> 16) & 255) + ((pixel >> 8) & 255) + (pixel & 255) > 450) count++;
        return count;
    }

    private static bool FileDetailImagePatch(NestedCanvas canvas, Rect image, BitmapSource picture, uint source)
    {
        var color = ((SolidColorBrush)typeof(NestedCanvas).GetField("SelectedTileBrush", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Color;
        var selected = canvas.SelectionState.Items().Any(item => item.Path == canvas.FileDetailTargetForTests);
        var ink = 0xFF000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
        var expected = selected ? Blend(source, ink, color.A) : source;
        var x = (int)(image.X + image.Width * .15); var y = (int)(image.Y + image.Height * .15);
        if (x < 1 || y < 1 || x + 1 >= picture.PixelWidth || y + 1 >= picture.PixelHeight) return false;
        for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
            if (!SameWithin(Pixel(picture, x + dx, y + dy), expected, 12)) return false;
        return true;
    }

    private static RenderTargetBitmap FileDetailCapture(NestedCanvas canvas, string name)
    {
        canvas.RenderNow();
        var picture = new RenderTargetBitmap(FileDetailWidth, FileDetailHeight, 96, 96, PixelFormats.Pbgra32);
        picture.Render(canvas); picture.Freeze();
        if (Environment.GetEnvironmentVariable(FileDetailCaptureDirectory) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using (var stream = File.Create(Path.Combine(directory, name)))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture)); encoder.Save(stream);
            }
            File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
            {
                canvas.IsSceneOnGpu, canvas.AreLabelsOnGpu, canvas.RendererReason,
                target = canvas.FileDetailTargetForTests,
                detail = canvas.FocusedFileDetailStateForTests?.ToString(),
                diagnostics = canvas.FocusedFileDetailDiagnosticsForTests.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  note  isolated file detail capture: {Path.Combine(directory, name)}");
        }
        return picture;
    }

    private static void FileDetailWriteLargePicture(string path)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(77, 147, 188)), null, new Rect(0, 0, 2048, 1280));
            dc.DrawEllipse(Brushes.LightGoldenrodYellow, null, new Point(1570, 250), 110, 110);
            var mountains = new StreamGeometry();
            using (var geometry = mountains.Open())
            {
                geometry.BeginFigure(new Point(0, 1280), true, true);
                geometry.PolyLineTo([new Point(550, 420), new Point(1090, 1030), new Point(1550, 570), new Point(2048, 1280)], true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(38, 99, 83)), null, mountains);
            for (var index = 0; index < 34; index++)
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(59, 128, 104)), null, new Rect(35 + index * 60, 1120, 24, 100));
        }
        var picture = new RenderTargetBitmap(2048, 1280, 96, 96, PixelFormats.Pbgra32); picture.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture));
        using var output = File.Create(path); encoder.Save(output);
    }

    private sealed class FileDetailFontTarget : LabelTarget
    {
        public List<(string Text, double Size, bool Scaled)> Texts { get; } = [];
        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
        {
            Texts.Add((text, size, scaled)); return new(text, Math.Min(maxWidth, text.Length * size * .5), size * 1.3);
        }
        public override void DrawText(in LabelText text, Point origin) { }
        public override void FillRect(Rect bounds, Color colour) { }
        public override void FillRounded(Rect bounds, double radius, Color colour) { }
        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds) => true;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint FileDetailForegroundWindow();
}
