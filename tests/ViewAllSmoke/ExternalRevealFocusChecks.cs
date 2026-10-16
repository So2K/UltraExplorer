using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string ExternalRevealFocusChild = "ULTRAEXPLORER_EXTERNAL_REVEAL_FOCUS_CHILD";

    private static async Task ExternalRevealFocusChecks()
    {
        if (Environment.GetEnvironmentVariable(ExternalRevealFocusChild) == "1")
        {
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
                throw new InvalidOperationException("External reveal checks require isolated state and nonactivating test-window mode.");
            RunOnSta("external reveal: readable selected file", ExternalRevealFocusOnStaAsync);
            return;
        }

        // Also isolate a direct --only invocation; never inherit the installed
        // program's state or let these real windows acquire desktop focus.
        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerExternalRevealFocus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned, "temp"));
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(nameof(ExternalRevealFocusChecks));
            start.Environment[ExternalRevealFocusChild] = "1";
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
            Check("external reveal checks reach their normal isolated-process end", finished && child.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            Check("external reveal checks finish within five minutes", false);
        }
        finally { child?.Dispose(); TryDelete(owned); }
    }

    private static async Task ExternalRevealFocusOnStaAsync()
    {
        Section("external reveal: the exact file is selected and readable in a dense folder");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var currentApplication = Application.Current ?? throw new InvalidOperationException("The isolated WPF application was not created.");
        currentApplication.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var owned = Path.Combine(AppPaths.StateDirectory, "owned-reveal-fixture");
        var drive = Path.Combine(owned, "drive");
        var dense = Path.Combine(drive, "Downloads fixture");
        var other = Path.Combine(drive, "other folder");
        Directory.CreateDirectory(dense);
        Directory.CreateDirectory(other);
        for (var index = 0; index < 1500; index++)
            File.WriteAllText(Path.Combine(dense, $"entry-{index:D4}.txt"), "generated reveal fixture " + index);
        for (var index = 0; index < 6; index++) Directory.CreateDirectory(Path.Combine(dense, "folder-" + index));
        var selected = Path.Combine(dense, "entry-0777 downloaded archive Ж.zip");
        var neighbour = Path.Combine(dense, "entry-0777.txt");
        File.WriteAllText(selected, "generated archive-name fixture, never opened");
        var otherFile = Path.Combine(other, "later request.txt");
        File.WriteAllText(otherFile, "later request");
        var browsableZip = Path.Combine(dense, "entry-0820 valid archive.zip");
        using (var archive = ZipFile.Open(browsableZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("inside/generated-note.txt").Open()))
            writer.Write("A valid generated archive used only by this reveal fixture.");
        if (!FolderCommandLine.TryReveal([selected], out var reveal, out _))
            throw new InvalidOperationException("The generated reveal invocation was invalid.");

        var main = new MainWindow(null, Path.Combine(owned, "window.workspace.json"))
        { Width = 1400, Height = 900, ShowActivated = false };
        var shell = (MainViewModel)main.DataContext;
        shell.SuppressShellWrites = true;
        shell.Layout = CanvasLayout.Nested;
        shell.BrowseArchives = false;
        main.PrepareFolderInvocation(reveal);
        main.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
        using var activation = ActivationGuard.GuardWindowsCreated();
        try
        {
            main.Show();
            await ExternalRevealWaitAsync(() => main.IsFolderWindowReady, "the cloaked window finishes normal startup");
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "Owned reveal drive", NestedFolderKind.Drive)]);
            var pane = main.ActivePane;
            pane.Canvas.DpiOverride = new DpiScale(1, 1);
            Check("the cold reveal opens successfully through the normal public window API",
                await main.ApplyFolderInvocationAsync(reveal).WaitAsync(TimeSpan.FromSeconds(45)));
            await ExternalRevealSettleAsync(pane.Canvas);
            Check("the cold reveal selects its exact file in both shared and rendered selections",
                ExternalRevealExactSelection(main, [selected]) && shell.Tree.Selection.Focus == selected
                && shell.Tree.FolderList.FolderPath == dense && shell.Address.CurrentPath == dense);
            Check("the dense fixture really contains over 1500 files rather than a sparse substitute",
                pane.Tree.Find(dense) is { IsLoaded: true } loaded && loaded.AllFiles.Length >= 1500);
            ExternalRevealCheckGeometry(pane, selected, "cold");
            var cold = ExternalRevealSave(pane.Canvas, "cold-reveal.png");
            var coldRect = FocusFileRect(pane.Canvas, pane.Tree, selected);
            Check("the actual rendered cold target has its cyan selection outline",
                coldRect is { } highlighted && ExternalRevealCyanBorder(cold, highlighted) >= 30);
            Check("the actual rendered cold target contains readable light filename glyphs",
                coldRect is { } labelled && ExternalRevealTextPixels(cold, labelled) >= 20);
            ExternalRevealSave(main, "cold-reveal-window.png");

            pane.Canvas.FitAll(animated: false);
            pane.Canvas.Pan(new Vector(-430, 210));
            Check("revealing again from a hand-panned overview completes in the existing window",
                await main.ApplyFolderInvocationAsync(reveal).WaitAsync(TimeSpan.FromSeconds(45)));
            await ExternalRevealSettleAsync(pane.Canvas);
            ExternalRevealCheckGeometry(pane, selected, "warm");
            Check("the warm reveal retains exactly its requested selected file", ExternalRevealExactSelection(main, [selected]));
            ExternalRevealSave(pane.Canvas, "warm-reveal.png");

            FolderCommandLine.TryReveal([selected, neighbour], out var multiple, out _);
            Check("a multi-file reveal completes without reducing its selection to the camera target",
                await main.ApplyFolderInvocationAsync(multiple).WaitAsync(TimeSpan.FromSeconds(45))
                && ExternalRevealExactSelection(main, [selected, neighbour]) && shell.Tree.Selection.Focus == selected);
            await ExternalRevealSettleAsync(pane.Canvas);
            ExternalRevealCheckGeometry(pane, selected, "multi-file primary");
            var multiImage = ExternalRevealSave(pane.Canvas, "multi-reveal.png");
            Check("the first and neighbouring selected cards both have actual cyan outlines",
                FocusFileRect(pane.Canvas, pane.Tree, selected) is { } firstRect
                && FocusFileRect(pane.Canvas, pane.Tree, neighbour) is { } nextRect
                && ExternalRevealCyanBorder(multiImage, firstRect) >= 30
                && ExternalRevealCyanBorder(multiImage, nextRect) >= 15);

            FolderCommandLine.TryOpenFolder(dense, out var open, out _);
            Check("plain folder opening after a reveal clears the previous file selection",
                await main.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(45))
                && shell.Tree.Selection.Count == 0 && pane.Canvas.SelectedCount == 0 && main.CurrentFolderPath == dense);
            await ExternalRevealSettleAsync(pane.Canvas);
            Check("plain folder opening frames the whole folder instead of retaining the file zoom",
                pane.Tree.Find(dense) is { } folder && pane.Canvas.ScreenRectOf(folder) is { } folderRect
                && Math.Max(folderRect.Width / pane.Canvas.ActualWidth, folderRect.Height / pane.Canvas.ActualHeight) is >= 0.8 and <= 1.2
                && FocusFileRect(pane.Canvas, pane.Tree, selected) is { Height: < 30 });
            ExternalRevealSave(pane.Canvas, "plain-folder.png");

            await ExternalRevealLayerChecksAsync(main, shell, pane, reveal, selected);
            await ExternalRevealLatestChecksAsync(main, shell, selected, otherFile);
            await ExternalRevealArchiveChecksAsync(main, shell, pane, browsableZip);

            main.Width = 860;
            main.Height = 650;
            main.UpdateLayout();
            Check("a reveal still completes in a narrow window", await main.ApplyFolderInvocationAsync(reveal).WaitAsync(TimeSpan.FromSeconds(45)));
            await ExternalRevealSettleAsync(pane.Canvas);
            ExternalRevealCheckGeometry(pane, selected, "narrow window");
            ExternalRevealSave(main, "narrow-reveal-window.png");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            main.Closed += (_, _) => closed.TrySetResult();
            main.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            shell.Dispose();
        }
    }

    private static bool ExternalRevealExactSelection(MainWindow main, string[] paths)
    {
        var shared = ((MainViewModel)main.DataContext).Tree.Selection.Paths;
        var rendered = main.ActivePane.Canvas.SelectionState.Items().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return shared.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) && rendered.SetEquals(paths);
    }

    private static void ExternalRevealCheckGeometry(NestedPane pane, string selected, string stage)
    {
        var canvas = pane.Canvas;
        var rect = FocusFileRect(canvas, pane.Tree, selected);
        var readableWidth = Math.Min(260, canvas.ActualWidth * 0.6);
        Check($"the {stage} target is a readable card with surrounding folder context ({rect})",
            rect is { } card && card.Width >= readableWidth && card.Width <= 500 && card.Height >= 40
            && card.Height <= 100 && card.Width <= canvas.ActualWidth * 0.8 + 1);
        Check($"the {stage} target is wholly visible near the viewport centre",
            rect is { } visible && visible.Left >= 0 && visible.Top >= 0
            && visible.Right <= canvas.ActualWidth && visible.Bottom <= canvas.ActualHeight
            && Math.Abs(visible.Left + visible.Width / 2 - canvas.ActualWidth / 2) <= canvas.ActualWidth * 0.08
            && Math.Abs(visible.Top + visible.Height / 2 - canvas.ActualHeight / 2) <= canvas.ActualHeight * 0.08);
    }

    private static async Task ExternalRevealLayerChecksAsync(MainWindow main, MainViewModel shell, NestedPane pane,
        FolderInvocation reveal, string selected)
    {
        var layers = shell.Layers;
        var filter = pane.Tree.FileNameFilter;
        Func<string, bool> pngOnly = name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        try
        {
            shell.Layers = layers & ~CanvasLayer.Files;
            pane.Tree.FileNameFilter = pngOnly;
            Check("an explicit reveal makes its file visible when files and its type were hidden",
                await main.ApplyFolderInvocationAsync(reveal).WaitAsync(TimeSpan.FromSeconds(45))
                && ExternalRevealExactSelection(main, [selected]) && pane.Canvas.ShownLayers.HasFlag(CanvasLayer.Files));
            await ExternalRevealSettleAsync(pane.Canvas);
            ExternalRevealCheckGeometry(pane, selected, "locally unhidden");
            Check("explicit reveal preserves the saved layer choice and installed name filter",
                !shell.Layers.HasFlag(CanvasLayer.Files) && ReferenceEquals(pane.Tree.FileNameFilter, pngOnly));
            ExternalRevealSave(pane.Canvas, "locally-unhidden-reveal.png");
        }
        finally { pane.Tree.FileNameFilter = filter; shell.Layers = layers; }
    }

    private static async Task ExternalRevealLatestChecksAsync(MainWindow main, MainViewModel shell, string oldPath, string laterPath)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Tree.MaterializeBarrierForChecks = async path =>
        {
            if (!ViewAllPath.Equals(path, oldPath)) return;
            entered.TrySetResult();
            await release.Task;
        };
        FolderCommandLine.TryReveal([oldPath], out var oldRequest, out _);
        FolderCommandLine.TryReveal([laterPath], out var laterRequest, out _);
        var older = main.ApplyFolderInvocationAsync(oldRequest);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var later = await main.ApplyFolderInvocationAsync(laterRequest).WaitAsync(TimeSpan.FromSeconds(45));
            var laterCamera = main.ActivePane.Canvas.CaptureCamera();
            release.TrySetResult();
            Check("a delayed older reveal cannot overwrite the newer request's selection or camera",
                later && !await older.WaitAsync(TimeSpan.FromSeconds(15))
                && ExternalRevealExactSelection(main, [laterPath]) && main.ActivePane.Canvas.CaptureCamera() == laterCamera);
            await ExternalRevealSettleAsync(main.ActivePane.Canvas);
            ExternalRevealCheckGeometry(main.ActivePane, laterPath, "newest request");
        }
        finally { release.TrySetResult(); shell.Tree.MaterializeBarrierForChecks = null; }
    }

    private static async Task ExternalRevealArchiveChecksAsync(MainWindow main, MainViewModel shell, NestedPane pane, string zip)
    {
        var priorMode = shell.BrowseArchives;
        var original = File.ReadAllBytes(zip);
        try
        {
            Check("the archive reveal fixture has its real bundled archive reader", ArchiveService.IsAvailable);
            shell.BrowseArchives = true;
            pane.ApplyArchivePreference();
            var parentPath = Path.GetDirectoryName(zip)!;
            var parent = await pane.Tree.MaterializePathAsync(parentPath)
                ?? throw new InvalidOperationException("The archive fixture parent was not materialized.");
            await pane.Tree.RefreshAsync(parent).WaitAsync(TimeSpan.FromSeconds(30));
            FolderCommandLine.TryReveal([zip], out var request, out _);
            Check("a real ZIP reveal completes with archive browsing enabled",
                await main.ApplyFolderInvocationAsync(request).WaitAsync(TimeSpan.FromSeconds(45)));
            await ExternalRevealSettleAsync(pane.Canvas);
            Check("archive reveal keeps physical file kind and byte size for file commands",
                ExternalRevealExactSelection(main, [zip]) && shell.Tree.Selection.TryGetItem(zip, out var item)
                && !item.IsDirectory && item.Size == original.LongLength && shell.Tree.FolderList.FolderPath == parentPath);
            var visual = pane.Tree.Find(zip);
            Check("archive reveal frames its existing archive-folder visual",
                visual is { IsArchive: true } && pane.Canvas.Anchor is { } anchor
                && ViewAllPath.Equals(anchor.FullPath, zip) && pane.Canvas.ScreenRectOf(visual) is { } rect
                && rect.Width > 100 && rect.Height > 60
                && Math.Abs(rect.Left + rect.Width / 2 - pane.Canvas.ActualWidth / 2) < pane.Canvas.ActualWidth * 0.08
                && Math.Abs(rect.Top + rect.Height / 2 - pane.Canvas.ActualHeight / 2) < pane.Canvas.ActualHeight * 0.08);
            parent = pane.Tree.Find(parentPath) ?? parent;
            Check("archive reveal inserts no duplicate regular-file tile beside its archive visual",
                parent.Children.Count(child => ViewAllPath.Equals(child.FullPath, zip)) == 1
                && parent.AllFiles.All(file => !file.Name.Equals(Path.GetFileName(zip), StringComparison.OrdinalIgnoreCase))
                && pane.Tree.FindFileIndex(parent, Path.GetFileName(zip)) < 0);
            Check("revealing an archive leaves its actual ZIP bytes untouched", File.ReadAllBytes(zip).SequenceEqual(original));
            ExternalRevealSave(pane.Canvas, "browsable-archive-reveal.png");
        }
        finally { shell.BrowseArchives = priorMode; pane.ApplyArchivePreference(); }
    }

    private static async Task ExternalRevealWaitAsync(Func<bool> condition, string description)
    {
        var until = Environment.TickCount64 + 45_000;
        while (!condition() && Environment.TickCount64 < until) await Task.Delay(15);
        if (!condition()) throw new TimeoutException(description);
    }

    private static async Task ExternalRevealSettleAsync(NestedCanvas canvas)
    {
        canvas.UpdateLayout();
        for (var frame = 0; frame < 3; frame++)
        {
            canvas.RenderNow();
            await canvas.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static RenderTargetBitmap ExternalRevealSave(FrameworkElement visual, string filename)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "external-reveal-focus");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
        return bitmap;
    }

    private static int ExternalRevealCyanBorder(BitmapSource bitmap, Rect card)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var found = 0;
        for (var y = Math.Max(0, (int)Math.Floor(card.Top) - 3); y <= Math.Min(bitmap.PixelHeight - 1, (int)Math.Ceiling(card.Bottom) + 3); y++)
            for (var x = Math.Max(0, (int)Math.Floor(card.Left) - 3); x <= Math.Min(bitmap.PixelWidth - 1, (int)Math.Ceiling(card.Right) + 3); x++)
            {
                if (x > card.Left + 4 && x < card.Right - 4 && y > card.Top + 4 && y < card.Bottom - 4) continue;
                var at = y * stride + x * 4;
                var b = pixels[at]; var g = pixels[at + 1]; var r = pixels[at + 2];
                if (pixels[at + 3] >= 200 && b >= 225 && g >= 160 && g <= 240 && r >= 65 && r <= 135 && b - r >= 90) found++;
            }
        return found;
    }

    private static int ExternalRevealTextPixels(BitmapSource bitmap, Rect card)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var found = 0;
        for (var y = Math.Max(0, (int)card.Top + 8); y <= Math.Min(bitmap.PixelHeight - 1, (int)card.Bottom - 8); y++)
            for (var x = Math.Max(0, (int)card.Left + 45); x <= Math.Min(bitmap.PixelWidth - 1, (int)card.Right - 25); x++)
            {
                var at = y * stride + x * 4;
                var b = pixels[at]; var g = pixels[at + 1]; var r = pixels[at + 2];
                if (pixels[at + 3] >= 200 && r >= 150 && g >= 150 && b >= 150
                    && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 55) found++;
            }
        return found;
    }
}
