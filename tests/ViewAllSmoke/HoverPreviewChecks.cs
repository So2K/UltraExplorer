using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    // No Application is created here: the existing Settings suite owns the one
    // process-wide App and calls HoverPreviewWindowChecks on its own dispatcher.
    private static Task HoverPreviewChecks() => OnDispatcher(HoverPreviewChecksAsync);

    private static async Task HoverPreviewChecksAsync()
    {
        Section("hover preview: dwell, stale responses and lifetime");
        Check("the production hover dwell is 220 ms", HoverPreviewController.Dwell == TimeSpan.FromMilliseconds(220));
        var dispatcher = Dispatcher.CurrentDispatcher;
        var calls = 0;
        using (var controller = new HoverPreviewController(dispatcher, (_, _) =>
               { calls++; return Task.FromResult<ThumbnailResult?>(null); }))
        {
            controller.Hover("rapid-a.png");
            await Task.Delay(70);
            controller.Hover("rapid-b.png");
            await Task.Delay(70);
            controller.Clear();
            await Task.Delay(250);
            Check("passing over files and leaving before dwell performs no extraction", calls == 0
                && controller.Path is null && controller.Result is null && !controller.IsLoading);
        }

        var a = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new List<string>();
        var tokens = new List<CancellationToken>();
        var changes = 0;
        var wrongThread = false;
        var imageA = HoverImage(80, 40, Colors.Crimson);
        var imageB = HoverImage(40, 80, Colors.CornflowerBlue);
        using (var controller = new HoverPreviewController(dispatcher, (path, token) =>
               { asked.Add(path); tokens.Add(token); return path == "a.png" ? a.Task : b.Task; }, TimeSpan.FromMilliseconds(45)))
        {
            controller.Changed += () => { changes++; wrongThread |= !dispatcher.CheckAccess(); };
            controller.Hover("a.png");
            Check("a settled target starts exactly its load", await HoverWait(() => asked.Count == 1)
                && asked[0] == "a.png" && controller.IsLoading);
            controller.Hover("A.PNG");
            await Task.Delay(80);
            Check("moving within the same Windows path does not restart extraction", asked.Count == 1 && !tokens[0].IsCancellationRequested);
            controller.Hover("b.png");
            Check("moving to another file cancels the old waiter", tokens[0].IsCancellationRequested
                && controller.Path == "b.png" && controller.Result is null);
            Check("the newest target starts independently", await HoverWait(() => asked.Count == 2)
                && asked[1] == "b.png");
            b.SetResult(new ThumbnailResult(imageB, 40, 80));
            Check("the current target receives its exact image", await HoverWait(() => controller.Result is not null)
                && ReferenceEquals(controller.Result?.Image, imageB) && !controller.IsLoading);
            var beforeStale = changes;
            a.SetResult(new ThumbnailResult(imageA, 80, 40));
            await Task.Delay(80);
            Check("an uncancellable old provider cannot replace the newest image or issue another change", changes == beforeStale
                && controller.Path == "b.png" && ReferenceEquals(controller.Result?.Image, imageB));
            Check("all card updates stay on the owning dispatcher", !wrongThread);
            controller.Clear();
            Check("clear removes every visible preview field immediately", controller.Path is null
                && controller.Result is null && !controller.IsLoading && tokens[1].IsCancellationRequested);
        }

        var failedLoads = 0;
        using (var controller = new HoverPreviewController(dispatcher, (_, _) =>
               { failedLoads++; throw new IOException("owned failing reader"); }, TimeSpan.FromMilliseconds(25)))
        {
            controller.Hover("unreadable.png");
            Check("a reader failure dismisses loading without escaping the dispatcher", await HoverWait(() => failedLoads == 1 && !controller.IsLoading)
                && controller.Result is null);
        }

        var blocked = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken blockedToken = default;
        var timeoutCalls = 0;
        using (var controller = new HoverPreviewController(dispatcher, (_, token) =>
               { timeoutCalls++; blockedToken = token; return blocked.Task; }, TimeSpan.FromMilliseconds(20)))
        {
            controller.Hover("never-returns.png");
            Check("a blocked reader reaches loading", await HoverWait(() => controller.IsLoading && timeoutCalls == 1));
            var since = Stopwatch.StartNew();
            Check("a blocked provider has a bounded loading lifetime", await HoverWait(() => !controller.IsLoading, 4200)
                && since.Elapsed < TimeSpan.FromSeconds(4.2) && blockedToken.IsCancellationRequested && controller.Result is null);
            blocked.SetResult(new ThumbnailResult(imageA));
            await Task.Delay(50);
            Check("a provider finishing after timeout cannot show a late preview", controller.Result is null && !controller.IsLoading);
        }

        var late = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lateToken = default;
        var disposed = new HoverPreviewController(dispatcher, (_, token) => { lateToken = token; return late.Task; }, TimeSpan.FromMilliseconds(20));
        var lateChanges = 0;
        disposed.Changed += () => lateChanges++;
        disposed.Hover("closing.png");
        Check("the lifetime fixture has a live extraction", await HoverWait(() => disposed.IsLoading));
        disposed.Dispose();
        var afterDispose = lateChanges;
        late.SetResult(new ThumbnailResult(imageA));
        disposed.Hover("cannot-reopen.png");
        await Task.Delay(80);
        Check("disposal cancels outstanding work and prevents callbacks or reopening", lateToken.IsCancellationRequested
            && lateChanges == afterDispose && disposed.Path is null && disposed.Result is null && !disposed.IsLoading);
        disposed.Dispose();

        Section("hover preview: passive card and edge placement");
        var card = new HoverPreviewCard();
        Check("the card is passive from construction", !card.Focusable && !card.IsHitTestVisible && card.Visibility == Visibility.Collapsed);
        foreach (var image in new[] { imageA, imageB })
        {
            card.Show("long-name-for-portrait-or-landscape-image.png", new ThumbnailResult(image, image.PixelWidth, image.PixelHeight), 1200, 700);
            var shown = HoverVisuals<Image>(card).Single();
            Check($"a {image.PixelWidth}x{image.PixelHeight} image keeps its aspect and source", shown.Stretch == Stretch.Uniform
                && shown.StretchDirection == StretchDirection.DownOnly && ReferenceEquals(shown.Source, image));
            Check("the card names the exact hovered file for accessibility", AutomationProperties.GetName(card)
                == "Preview of long-name-for-portrait-or-landscape-image.png");
            foreach (var pointer in new[] { new Point(0, 0), new Point(1199, 0), new Point(0, 699), new Point(1199, 699) })
            {
                var placed = HoverPreviewCard.Place(pointer, card.DesiredSize, new Size(1200, 700));
                Check($"the card is contained at viewport edge {pointer}", placed.X >= 8 && placed.Y >= 8
                    && placed.X + card.DesiredSize.Width <= 1192.01 && placed.Y + card.DesiredSize.Height <= 692.01);
            }
        }
        card.Hide();
        Check("hiding the card releases its image immediately", card.Visibility == Visibility.Collapsed && HoverVisuals<Image>(card).Single().Source is null);
        card.Show("loading.png", null, 1200, 700);
        Check("a loading card never carries a previous file image", HoverVisuals<Image>(card).Single().Source is null
            && HoverVisuals<TextBlock>(card).Any(text => text.Text == "Loading preview…" && text.Visibility == Visibility.Visible));
    }

    private static async Task HoverPreviewWindowChecks()
    {
        Section("hover preview: real WPF window, exact hovered multiselection and disable");
        var fixture = Path.Combine(Path.GetTempPath(), "UltraExplorerHoverWindow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var first = Path.Combine(fixture, "a-landscape.png");
        var second = Path.Combine(fixture, "b-portrait.png");
        HoverSaveImage(HoverImage(240, 120, Colors.Coral), first);
        HoverSaveImage(HoverImage(100, 240, Colors.CornflowerBlue), second);
        MainWindow? window = null;
        MainViewModel? model = null;
        var app = Application.Current;
        var priorShutdown = app.ShutdownMode;
        var priorMain = app.MainWindow;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            window = new MainWindow(null, Path.Combine(fixture, "workspace.json"))
            { Width = 1200, Height = 700, WindowState = WindowState.Normal };
            model = (MainViewModel)window.DataContext;
            model.SuppressShellWrites = true;
            window.ActivePane.Tree.IsReadingOnDemand = false;
            Check("the hover window opens only its owned fixture", FolderCommandLine.TryParse(["--open-folder", fixture], out var invocation, out _));
            window.PrepareFolderInvocation(invocation);
            window.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using (ActivationGuard.GuardWindowsCreated()) window.Show();
            Check("the real hover window finishes startup before hover assertions", await HoverWait(() => window.IsFolderWindowReady, 6000));
            // A selection normally follows startup and navigation. Performing
            // it on an uninitialized, unshown window started that navigation
            // concurrently with the hover and correctly dismissed the card.
            await model.Tree.SelectPathAsync(first);
            model.Tree.FolderList.IsVisible = true;
            await model.Tree.FolderList.ReloadAsync();
            Check("the focused file and its list settle before multiselection", await HoverWait(() => model.Tree.ActiveNode?.FullPath == first
                && !model.Tree.FolderList.IsLoading && model.Tree.FolderList.Items.Count == 2, 6000));
            model.ShowHoverPreviews = true;
            var content = (Grid)window.Content;
            HoverLayout(content);
            model.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(first, false, new FileInfo(first).Length), new SelectionItem(second, false, new FileInfo(second).Length)],
                Anchor = first, Focus = first, Source = SelectionSource.Canvas
            });
            var version = model.Tree.Selection.Version;
            var focus = Keyboard.FocusedElement;
            window.SetHoverPreviewTarget(second, new Point(1160, 670));
            Check("hovering one of several selected files shows that exact second file", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null)
                && ViewAllPath.Equals(window.HoverPreviewsForChecks.Path ?? string.Empty, second)
                && window.HoverPreviewsForChecks.Result is { OriginalWidth: 100, OriginalHeight: 240 });
            HoverLayout(content);
            var card = window.PreviewCardForChecks;
            Check("the window overlay cannot capture a click, keyboard focus or change selection", card.Visibility == Visibility.Visible
                && !card.Focusable && !card.IsHitTestVisible && card.Parent is Canvas { IsHitTestVisible: false }
                && model.Tree.Selection.Version == version && model.Tree.Selection.Count == 2
                && model.Tree.Selection.Contains(first) && model.Tree.Selection.Contains(second) && Keyboard.FocusedElement == focus);
            var x = Canvas.GetLeft(card);
            var y = Canvas.GetTop(card);
            Check("the real window card is contained at its bottom-right edge", x >= 8 && y >= 8
                && x + card.ActualWidth <= content.ActualWidth - 8 + 0.01 && y + card.ActualHeight <= content.ActualHeight - 8 + 0.01);
            HoverSaveShot(content, "window-owned-portrait-simulated-hover.png");

            model.ShowHoverPreviews = false;
            Check("disabling previews hides the card synchronously and releases the target", card.Visibility == Visibility.Collapsed
                && window.HoverPreviewsForChecks.Path is null && !window.HoverPreviewsForChecks.IsLoading);
            window.SetHoverPreviewTarget(first, new Point(200, 200));
            await Task.Delay(300);
            Check("a disabled preference prevents even an explicit target from loading", window.HoverPreviewsForChecks.Path is null
                && window.HoverPreviewsForChecks.Result is null && card.Visibility == Visibility.Collapsed);
            model.ShowHoverPreviews = true;
            window.SetHoverPreviewTarget(first, new Point(500, 200));
            Check("re-enabling previews displays a different file without opening it", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null)
                && window.HoverPreviewsForChecks.Result is { OriginalWidth: 240, OriginalHeight: 120 });
            HoverLayout(content);
            HoverSaveShot(content, "window-owned-landscape-simulated-hover.png");

            // Read-only user-provided file; opt-in only. The application service
            // loads the real PNG, but the hover target here is injected, so the
            // screenshot is explicitly labelled simulated hover.
            if (Environment.GetEnvironmentVariable("HOVER_REAL_PREVIEW") is { Length: > 0 } real && File.Exists(real))
            {
                window.SetHoverPreviewTarget(real, new Point(550, 200));
                Check("the requested real PNG decodes in the actual card without opening a viewer", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null));
                HoverLayout(content);
                HoverSaveShot(content, "window-real-png-simulated-hover.png");
            }

            window.SetHoverPreviewTarget(null, default);
            Check("leaving the target hides and releases its bitmap", card.Visibility == Visibility.Collapsed
                && HoverVisuals<Image>(card).Single().Source is null);
            Check("all preview activity preserves the original two-item selection", model.Tree.Selection.Version == version
                && model.Tree.Selection.Count == 2 && model.Tree.Selection.Focus == first);

            // Both split panes can draw the same file. Moving to the other
            // pane must move its subscriptions and suppression while keeping
            // the decoded image; a later camera/tree event belongs to its
            // actual current pane, not whichever showed the path first.
            using (var treeA = new NestedTree { IsReadingOnDemand = false })
            using (var treeB = new NestedTree { IsReadingOnDemand = false })
            using (var replacementTree = new NestedTree { IsReadingOnDemand = false })
            {
                var canvasA = new NestedCanvas { Tree = treeA };
                var canvasB = new NestedCanvas { Tree = treeB };
                foreach (var canvas in new[] { canvasA, canvasB })
                {
                    canvas.Measure(new Size(600, 400));
                    canvas.Arrange(new Rect(0, 0, 600, 400));
                }
                window.SetHoverPreviewTarget(second, new Point(250, 200), canvasA);
                Check("the first pane suppresses its text tip while showing a content preview", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null)
                    && canvasA.SuppressFileHoverTip && !canvasB.SuppressFileHoverTip);
                var sharedImage = window.HoverPreviewsForChecks.Result;
                var beforeTransferX = Canvas.GetLeft(card);
                window.SetHoverPreviewTarget(second, new Point(700, 200), canvasB);
                Check("the same file moves to its hovered pane without restarting extraction", ReferenceEquals(sharedImage, window.HoverPreviewsForChecks.Result)
                    && !canvasA.SuppressFileHoverTip && canvasB.SuppressFileHoverTip
                    && Canvas.GetLeft(card) != beforeTransferX);
                treeA.SetRoots([]);
                Check("a tree update in the old pane cannot dismiss the current pane preview", ReferenceEquals(sharedImage, window.HoverPreviewsForChecks.Result)
                    && window.HoverPreviewsForChecks.Path == second && canvasB.SuppressFileHoverTip);
                canvasB.FitAll(animated: false);
                Check("moving the currently hovered pane camera dismisses its preview", window.HoverPreviewsForChecks.Path is null
                    && !canvasB.SuppressFileHoverTip && card.Visibility == Visibility.Collapsed);

                window.SetHoverPreviewTarget(second, new Point(700, 200), canvasB);
                Check("the tree-replacement fixture reaches a live card", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null));
                canvasB.Tree = replacementTree;
                window.SetHoverPreviewTarget(null, default);
                window.SetHoverPreviewTarget(second, new Point(250, 200), canvasA);
                Check("the new pane obtains its preview after leaving a replaced tree", await HoverWait(() => window.HoverPreviewsForChecks.Result is not null));
                var afterReplacement = window.HoverPreviewsForChecks.Result;
                treeB.SetRoots([]);
                Check("the captured old tree is unsubscribed even when the canvas now has a different tree", ReferenceEquals(afterReplacement, window.HoverPreviewsForChecks.Result)
                    && window.HoverPreviewsForChecks.Path == second && canvasA.SuppressFileHoverTip);
                window.SetHoverPreviewTarget(null, default);
                canvasA.Tree = null;
                canvasB.Tree = null;
            }

            // Unlike SetHoverPreviewTarget, this sends the actual routed WPF
            // preview-move event from a real generated folder-list container.
            model.Layout = CanvasLayout.Tree;
            model.Tree.FolderList.IsVisible = true;
            await model.Tree.FolderList.NavigateAsync(fixture);
            await model.Tree.FolderList.ReloadAsync();
            HoverLayout(content);
            var row = model.Tree.FolderList.Items.FirstOrDefault(item => ViewAllPath.Equals(item.FullPath, second));
            var list = window.FolderListItems;
            list.ScrollIntoView(row);
            HoverLayout(content);
            var container = row is null ? null : list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            Check("the list hover fixture has a real generated file row", container is not null);
            if (container is not null)
            {
                container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                Check("a routed move from the folder-list row targets its own file", ViewAllPath.Equals(window.HoverPreviewsForChecks.Path ?? string.Empty, second));
                window.SetHoverPreviewTarget(null, default);
            }
        }
        finally
        {
            if (window is not null)
            {
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                await HoverWait(() => closed, 2500);
            }
            model?.Dispose();
            app.MainWindow = priorMain;
            app.ShutdownMode = priorShutdown;
            TryDelete(fixture);
        }
    }

    private static async Task<bool> HoverWait(Func<bool> ready, int milliseconds = 2500)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!ready() && Environment.TickCount64 < until) await Task.Delay(10);
        return ready();
    }

    private static BitmapSource HoverImage(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            var stripe = (x / Math.Max(1, width / 6) + y / Math.Max(1, height / 6)) % 2 == 0;
            pixels[at] = stripe ? color.B : (byte)(color.B / 3);
            pixels[at + 1] = stripe ? color.G : (byte)(color.G / 3);
            pixels[at + 2] = stripe ? color.R : (byte)(color.R / 3);
            pixels[at + 3] = 255;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private static IEnumerable<T> HoverVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in HoverVisuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void HoverLayout(FrameworkElement content)
    {
        content.Measure(new Size(1200, 700));
        content.Arrange(new Rect(0, 0, 1200, 700));
        content.UpdateLayout();
    }

    private static void HoverSaveImage(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void HoverSaveShot(FrameworkElement content, string name)
    {
        if (Environment.GetEnvironmentVariable("HOVER_PREVIEW_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var shot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shot.Render(content);
        HoverSaveImage(shot, Path.Combine(folder, name));
    }
}
