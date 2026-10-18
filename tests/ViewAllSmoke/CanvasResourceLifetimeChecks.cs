using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string CanvasLifetimeChild = "ULTRAEXPLORER_CANVAS_LIFETIME_CHILD";

    private static async Task CanvasResourceLifetimeChecks()
    {
        if (Environment.GetEnvironmentVariable(CanvasLifetimeChild) == "1")
        {
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
                throw new InvalidOperationException("Canvas lifetime checks require isolated state and nonactivating test windows.");
            RunOnSta("canvas resource lifetime", CanvasLifetimeOnStaAsync);
            return;
        }

        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerCanvasLifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned, "temp"));
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(nameof(CanvasResourceLifetimeChecks));
            start.Environment[CanvasLifetimeChild] = "1";
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
            Check("canvas lifetime checks reach their isolated-process end", finished && child.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            Check("canvas lifetime checks finish within five minutes", false);
        }
        finally { child?.Dispose(); TryDelete(owned); }
    }

    private static async Task CanvasLifetimeOnStaAsync()
    {
        Section("canvas lifetime: retained views stay warm; closed panes release resources without GC");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var owned = Path.Combine(AppPaths.StateDirectory, "canvas-lifetime-fixture");
        var left = Path.Combine(owned, "left");
        var right = Path.Combine(owned, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        for (var index = 0; index < 12; index++) File.WriteAllText(Path.Combine(left, $"entry-{index:D2}.txt"), "generated lifetime fixture");
        var selected = Path.Combine(left, "entry-05.txt");
        File.WriteAllText(Path.Combine(right, "other.txt"), "generated second pane fixture");
        MainWindow? host = null;
        try
        {
            host = new MainWindow { Width = 1300, Height = 800, ShowActivated = false };
            var first = host.EnableFolderTabs(TabRequest(left));
            var shell = TabShell(first);
            shell.SuppressShellWrites = true;
            shell.Layout = CanvasLayout.Nested;
            host.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using var activation = ActivationGuard.GuardWindowsCreated();
            host.Show();
            if (!await TabWait(() => first.IsFolderWindowReady)) throw new TimeoutException("The lifetime fixture did not initialize.");
            first.UseNestedDrivesForChecks([new NestedRoot(owned, "Owned lifetime fixture", NestedFolderKind.Drive)]);
            if (!FolderCommandLine.TryReveal([selected], out var reveal, out var error)) throw new InvalidOperationException(error);
            Check("the lifetime fixture opens and selects a real generated file", await first.ApplyFolderInvocationAsync(reveal));
            var pane = first.FirstPane;
            var canvas = pane.Canvas;
            canvas.DpiOverride = new DpiScale(1, 1);
            canvas.RenderNow();
            var projection = SearchMapProjection.Create("entry-05", [selected]);
            canvas.SetSearchMapProjection(projection);
            canvas.RenderNow();
            var camera = canvas.CaptureCamera();
            var selection = shell.Tree.Selection.Items.ToArray();
            var bitmap = canvas.SceneBitmap;
            var pixels = CanvasLifetimePixels(canvas);
            Check("the retained-view fixture has a real nonempty CPU scene and selected card",
                camera is not null && bitmap is not null && pixels.Distinct().Take(3).Count() >= 3
                && canvas.SelectedCount == 1 && shell.Tree.Selection.Focus == selected);
            Check("the real scene has populated its folder label references", CanvasLifetimeLabelled(canvas).Count > 0);
            using var firstFrame = CanvasLifetimeSeedFrame(canvas);

            var other = host.NewExplorerTab(right);
            TabShell(other).SuppressShellWrites = true;
            TabShell(other).Layout = CanvasLayout.Nested;
            if (!await TabWait(() => other.IsFolderWindowReady && !other.IsFolderInvocationPending))
                throw new TimeoutException("The retained tab did not initialize.");
            Check("hiding a retained tab preserves its native frame, CPU bitmap and map projection",
                first.ContextRoot.Visibility != Visibility.Visible && firstFrame.SceneRects.Capacity >= 8192
                && ReferenceEquals(CanvasLifetimeFrame(canvas), firstFrame)
                && ReferenceEquals(canvas.SceneBitmap, bitmap) && ReferenceEquals(canvas.SearchMapProjection, projection));
            host.SelectTabContext(first, focus: false);
            host.UpdateLayout();
            canvas.RenderNow();
            Check("returning to a retained tab renders the same scene with its original camera and selection",
                camera == canvas.CaptureCamera() && shell.Tree.Selection.Items.SequenceEqual(selection)
                && canvas.SelectedCount == 1 && pixels.SequenceEqual(CanvasLifetimePixels(canvas)));

            // Actual WPF unload/reload by reparenting, rather than invoking
            // the handlers directly: this must remain a reversible lifecycle.
            var parent = (Panel)VisualTreeHelper.GetParent(canvas);
            var indexInParent = parent.Children.IndexOf(canvas);
            parent.Children.Remove(canvas);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check("temporary WPF unload retains the reusable native frame and camera",
                !canvas.IsLoaded && ReferenceEquals(CanvasLifetimeFrame(canvas), firstFrame)
                && firstFrame.SceneRects.Capacity >= 8192 && camera == canvas.CaptureCamera());
            parent.Children.Insert(indexInParent, canvas);
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            canvas.RenderNow();
            Check("reloaded canvas renders the same pixels, camera and selected card",
                canvas.IsLoaded && pixels.SequenceEqual(CanvasLifetimePixels(canvas))
                && camera == canvas.CaptureCamera() && canvas.SelectedCount == 1
                && shell.Tree.Selection.Items.SequenceEqual(selection));

            shell.IsSplit = true;
            if (!await TabWait(() => first.SecondPane is { IsReady: true })) throw new TimeoutException("The split pane did not initialize.");
            var second = first.SecondPane!;
            second.Canvas.RenderNow();
            using var secondFrame = CanvasLifetimeSeedFrame(second.Canvas);
            second.Canvas.SetSearchMapProjection(projection);
            var secondDriver = second.Tree.Driver;
            var sharedDriver = second.Canvas.Changes!.Driver;
            second.Canvas.Wake(); // This dispatcher callback survives until after closure.
            shell.IsSplit = false;
            Check("closing a real split pane immediately frees all four native arrays without a collection",
                first.SecondPane is null && CanvasLifetimeFreed(secondFrame));
            Check("the closed pane releases CPU recordings, map, tree and driver subscriptions",
                CanvasLifetimeClosed(second.Canvas) && !secondDriver.IsClaimedBy(second.Canvas) && !sharedDriver.IsClaimedBy(second.Canvas));
            second.Detach();
            second.Canvas.Dispose();
            await Dispatcher.Yield(DispatcherPriority.Background);
            CanvasLifetimeLateCallbacks(second.Canvas);
            Check("repeated detach and queued wakes cannot revive a closed pane",
                CanvasLifetimeFreed(secondFrame) && CanvasLifetimeClosed(second.Canvas));
            Check("closing the other pane leaves the retained first pane's native frame alive",
                ReferenceEquals(CanvasLifetimeFrame(canvas), firstFrame) && firstFrame.SceneRects.Capacity >= 8192);

            // Closing an inactive retained tab also reaches the real window
            // lifecycle, not merely the split-pane helper.
            host.SelectTabContext(other, focus: false);
            var otherCanvas = other.FirstPane.Canvas;
            otherCanvas.RenderNow();
            using var otherFrame = CanvasLifetimeSeedFrame(otherCanvas);
            Check("closing an inactive tab completes its ordinary window close", await host.CloseTabContextAsync(first));
            Check("inactive tab close frees its arrays while the active tab stays usable",
                CanvasLifetimeFreed(firstFrame) && CanvasLifetimeClosed(canvas)
                && otherFrame.SceneRects.Capacity >= 8192 && ReferenceEquals(otherCanvas.Tree, other.FirstPane.Tree));
            otherCanvas.RenderNow();
            Check("the remaining tab still paints its real scene", CanvasLifetimePixels(otherCanvas).Distinct().Take(3).Count() >= 3);
            otherCanvas.Wake();
            Check("closing the host completes the final retained window lifecycle", await TabCloseHostAsync(host));
            CanvasLifetimeLateCallbacks(otherCanvas);
            Check("host closure releases the final pane and pending callbacks leave it closed",
                CanvasLifetimeFreed(otherFrame) && CanvasLifetimeClosed(otherCanvas));
        }
        finally
        {
            if (host is { IsVisible: true }) await TabCloseHostAsync(host);
            await SettingsSettle();
            TryDelete(owned);
        }
    }

    private static NestedGpuFrame? CanvasLifetimeFrame(NestedCanvas canvas)
        => (NestedGpuFrame?)typeof(NestedCanvas).GetField("_gpuFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);

    private static NestedGpuFrame CanvasLifetimeSeedFrame(NestedCanvas canvas)
    {
        // Real production native arrays, seeded independently of whether the
        // test machine has a GPU. Keep a strong reference past close so only
        // deterministic disposal, never finalization, can pass this check.
        if (CanvasLifetimeFrame(canvas) is not null) throw new InvalidOperationException("The CPU fixture unexpectedly owns a GPU frame.");
        var frame = new NestedGpuFrame(16, 16, 16);
        for (var index = 0; index < 4097; index++) frame.SceneRects.Add() = default;
        for (var index = 0; index < 65; index++) frame.LabelRects.Add() = default;
        for (var index = 0; index < 33; index++) frame.Icons.Add() = default;
        for (var index = 0; index < 77; index++) frame.Glyphs.Add() = default;
        typeof(NestedCanvas).GetField("_gpuFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, frame);
        return frame;
    }

    private static bool CanvasLifetimeFreed(NestedGpuFrame frame)
        => frame.SceneRects.Capacity == 0 && frame.LabelRects.Capacity == 0
            && frame.Icons.Capacity == 0 && frame.Glyphs.Capacity == 0;

    private static bool CanvasLifetimeClosed(NestedCanvas canvas)
        => CanvasLifetimeFrame(canvas) is null && canvas.SceneBitmap is null && canvas.Tree is null
            && canvas.IconArrivals is null && canvas.Changes is null && canvas.SearchMapProjection is null
            && !canvas.IsFrameHooked && CanvasLifetimeLabelled(canvas).Count == 0
            && Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(canvas)).All(index =>
                VisualTreeHelper.GetChild(canvas, index) is DrawingVisual visual
                && VisualTreeHelper.GetChildrenCount(visual) == 0 && (visual.Drawing is null || visual.Drawing.Children.Count == 0));

    private static HashSet<NestedFolder> CanvasLifetimeLabelled(NestedCanvas canvas)
        => (HashSet<NestedFolder>)typeof(NestedCanvas).GetField("_labelled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    private static void CanvasLifetimeLateCallbacks(NestedCanvas canvas)
    {
        var frames = canvas.LoopFrameCount;
        canvas.Wake();
        canvas.RefreshIcons();
        canvas.Redraw();
        canvas.RenderNow();
        canvas.RenderAsFrame();
        canvas.RunFrameForTests(TimeSpan.FromSeconds(15));
        canvas.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check("late render and Loaded callbacks perform no closed-canvas frames", canvas.LoopFrameCount == frames);
    }

    private static byte[] CanvasLifetimePixels(NestedCanvas canvas)
    {
        var bitmap = canvas.SceneBitmap ?? throw new InvalidOperationException("The live fixture has no CPU scene.");
        var width = canvas.ScenePixelWidth;
        var height = canvas.ScenePixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return pixels;
    }
}
