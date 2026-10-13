using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ExplorerTabChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(ExplorerTabChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(ExplorerTabChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Explorer tab checks require isolated state and test-window mode.");
        RunOnSta("retained explorer tabs", ExplorerTabsOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ExplorerTabsOnStaAsync()
    {
        Section("explorer tabs: independent retained contexts and file drag destinations");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var app = Application.Current!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var owned = Path.Combine(AppPaths.StateDirectory, "explorer-tabs-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(owned, "left");
        var right = Path.Combine(owned, "right");
        var third = Path.Combine(owned, "third");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        Directory.CreateDirectory(third);
        var selected = Path.Combine(left, "selected.txt");
        var copied = Path.Combine(left, "copy.txt");
        var moved = Path.Combine(left, "move.txt");
        await File.WriteAllTextAsync(selected, "retained selection");
        await File.WriteAllTextAsync(copied, "copy between tabs");
        await File.WriteAllTextAsync(moved, "move between tabs");
        var hoverImage = Path.Combine(left, "hover.png");
        HoverSaveImage(HoverImage(120, 80, Colors.Coral), hoverImage);
        MainWindow? host = null;
        try
        {
            var initial = TabRequest(left);
            host = new MainWindow { Width = 1300, Height = 800, ShowActivated = false };
            var first = host.EnableFolderTabs(initial);
            TabShell(first).SuppressShellWrites = true;
            host.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using (ActivationGuard.GuardWindowsCreated()) host.Show();
            Check("the initial tab completes startup below one cloaked, nonactivating host", await TabWait(() => first.IsFolderWindowReady));
            if (!first.IsFolderWindowReady) return;
            Check("the first requested folder opens in its own tab context", await first.ApplyFolderInvocationAsync(initial));
            var second = host.NewExplorerTab(right);
            var last = host.NewExplorerTab(third);
            TabShell(second).SuppressShellWrites = true;
            TabShell(last).SuppressShellWrites = true;
            Check("tabs added to a loaded window initialize and finish their own folder requests",
                await TabWait(() => second.IsFolderWindowReady && last.IsFolderWindowReady
                    && !second.IsFolderInvocationPending && !last.IsFolderInvocationPending));
            Check("three requests create three independent contexts below one native window",
                host.TabCount == 3 && host.TabContexts.All(context => !context.IsVisible
                    && new WindowInteropHelper(context).Handle == IntPtr.Zero
                    && ReferenceEquals(context.ShellWindow, host))
                && new WindowInteropHelper(host).Handle != IntPtr.Zero
                && ViewAllPath.Equals(first.CurrentFolderPath, left)
                && ViewAllPath.Equals(second.CurrentFolderPath, right)
                && ViewAllPath.Equals(last.CurrentFolderPath, third));
            var firstShell = TabShell(first);
            var secondShell = TabShell(second);
            Check("navigation, folder lists, searches, selections and disk changes belong to each tab",
                !ReferenceEquals(firstShell, secondShell)
                && !ReferenceEquals(firstShell.Tree, secondShell.Tree)
                && !ReferenceEquals(firstShell.Tree.FolderList, secondShell.Tree.FolderList)
                && !ReferenceEquals(firstShell.Address, secondShell.Address)
                && !ReferenceEquals(firstShell.Search, secondShell.Search)
                && !ReferenceEquals(firstShell.History, secondShell.History)
                && !ReferenceEquals(firstShell.Changes, secondShell.Changes));

            host.SelectTabContext(first, focus: false);
            firstShell.Tree.Selection.ReplaceSingle(selected, false, 18, SelectionSource.Canvas);
            first.FirstPane.SyncSelection();
            firstShell.History.Record(left);
            firstShell.History.Record(Path.Combine(left, "history"));
            TabSearchText(firstShell.Search, "first tab query");
            firstShell.Tree.FolderList.IsVisible = true;
            await SplitSettleAsync(first);
            var camera = first.FirstPane.Canvas.CaptureCamera();
            var selection = firstShell.Tree.Selection.Items.ToArray();
            var focus = firstShell.Tree.Selection.Focus;
            var anchor = firstShell.Tree.Selection.Anchor;
            var history = firstShell.History.Recent().ToArray();
            host.SelectTabContext(second, focus: false);
            TabSearchText(secondShell.Search, "second tab query");
            secondShell.IsSplit = true;
            secondShell.SplitRatio = 0.35;
            await SettingsSettle();
            host.SelectTabContext(first, focus: false);
            Check("switching preserves the original camera, selection, caret, anchor and history",
                first.FirstPane.Canvas.CaptureCamera() == camera
                && firstShell.Tree.Selection.Items.SequenceEqual(selection)
                && firstShell.Tree.Selection.Focus == focus && firstShell.Tree.Selection.Anchor == anchor
                && firstShell.History.Recent().SequenceEqual(history));
            Check("searches, list visibility and split panes keep independent presentation state",
                firstShell.Search.Text == "first tab query" && secondShell.Search.Text == "second tab query"
                && firstShell.Search.IsOpen && secondShell.Search.IsOpen
                && firstShell.Tree.FolderList.IsVisible && !firstShell.IsSplit
                && secondShell.IsSplit && second.SecondPane is not null && secondShell.SplitRatio == 0.35
                && first.ContextRoot.Visibility == Visibility.Visible && second.ContextRoot.Visibility != Visibility.Visible);
            Check("each retained view has its own binding context but the same actual presentation source",
                ReferenceEquals(first.ContextRoot.DataContext, firstShell)
                && ReferenceEquals(second.ContextRoot.DataContext, secondShell)
                && ReferenceEquals(((BindingProxy)first.ContextRoot.FindResource("Shell")).Data, firstShell)
                && ReferenceEquals(((BindingProxy)second.ContextRoot.FindResource("Shell")).Data, secondShell)
                && ReferenceEquals(PresentationSource.FromVisual(first.ContextRoot), PresentationSource.FromVisual(host))
                && ReferenceEquals(PresentationSource.FromVisual(second.ContextRoot), PresentationSource.FromVisual(host)));
            firstShell.Search.Close();
            secondShell.Search.Close();
            secondShell.IsSplit = false;
            TabHandoffOwnershipChecks(host, first, second, last);
            await TabKeyboardAndReorderAsync(host, first, second, last);
            await TabSaveScreenshotAsync(host);

            var headers = (Panel)host.FindName("TabHeaderPanel");
            var secondHeader = (Border)headers.Children[1];
            var data = new DataObject(DataFormats.FileDrop, new[] { copied });
            var overHeader = SplitDragArgs(DragDrop.DragOverEvent, data, DragDropKeyStates.ControlKey, secondHeader, new Point(20, 15));
            secondHeader.RaiseEvent(overHeader);
            await Task.Delay(120);
            Check("a brief file hover queues switching without consuming or dropping the file",
                ReferenceEquals(host.ActiveTabContext, first) && overHeader.Handled && overHeader.Effects == DragDropEffects.None);
            secondHeader.RaiseEvent(SplitDragArgs(DragDrop.DragLeaveEvent, data, DragDropKeyStates.None, secondHeader, new Point(20, 15)));
            await Task.Delay(500);
            Check("leaving a tab before the hover delay cancels switching", ReferenceEquals(host.ActiveTabContext, first));
            secondHeader.RaiseEvent(SplitDragArgs(DragDrop.DragOverEvent, data, DragDropKeyStates.ControlKey, secondHeader, new Point(20, 15)));
            Check("a sustained file hover switches the full context after the delay", await TabWait(() => ReferenceEquals(host.ActiveTabContext, second), 1500));
            var sourceHost = (INestedPaneHost)first;
            sourceHost.NestedDragPaths = [copied];
            var copiedDrop = await TabDropAsync(second, right, data, DragDropKeyStates.ControlKey);
            sourceHost.NestedDragPaths = null;
            sourceHost.ClearDropTargets();
            Check("the normal destination drop copies intact bytes and preserves the source",
                copiedDrop == DragDropEffects.Copy && await TabWait(() => File.Exists(Path.Combine(right, "copy.txt")))
                && await File.ReadAllTextAsync(Path.Combine(right, "copy.txt")) == "copy between tabs" && File.Exists(copied));
            sourceHost.NestedDragPaths = [moved];
            var movedDrop = await TabDropAsync(second, right, new DataObject(DataFormats.FileDrop, new[] { moved }), DragDropKeyStates.ShiftKey);
            sourceHost.NestedDragPaths = null;
            sourceHost.ClearDropTargets();
            Check("the normal destination drop moves intact bytes and removes the source",
                movedDrop == DragDropEffects.None && await TabWait(() => File.Exists(Path.Combine(right, "move.txt")) && !File.Exists(moved))
                && await File.ReadAllTextAsync(Path.Combine(right, "move.txt")) == "move between tabs");
            host.SelectTabContext(first, focus: false);
            sourceHost.NestedDragPaths = [selected];
            var sameFolder = await TabDropOverAsync(first, left, new DataObject(DataFormats.FileDrop, new[] { selected }), DragDropKeyStates.None);
            sourceHost.NestedDragPaths = null;
            Check("the shared internal drag source still refuses its own folder", sameFolder == DragDropEffects.None);

            await TabNativeMenuOwnerAsync(first, selected, host);
            await TabHoverWindowEventsAsync(host, first, hoverImage);
            await TabVolumeMediaReleaseAsync(host, first, second, left);
            Check("closing a middle tab disposes only its own model", await host.CloseTabContextAsync(second)
                && secondShell.IsDisposed && host.TabCount == 2 && !firstShell.IsDisposed
                && ReferenceEquals(host.ActiveTabContext, first));
            await TabPendingDropCloseAsync(host, last, third, left);
            Check("closing the remaining host closes and disposes every retained tab",
                (!host.IsVisible || await TabCloseHostAsync(host)) && firstShell.IsDisposed && TabShell(last).IsDisposed && host.TabCount == 0);
            Check("no native or unshown context windows remain after closing the host", app.Windows.OfType<MainWindow>().All(window => !ReferenceEquals(window, host)
                && !ReferenceEquals(window, first) && !ReferenceEquals(window, second) && !ReferenceEquals(window, last)));
        }
        finally
        {
            if (host is { IsVisible: true }) await TabCloseHostAsync(host);
            await SettingsSettle();
            TryDelete(owned);
        }
    }

    private static MainViewModel TabShell(MainWindow context) => (MainViewModel)context.DataContext;
    private static FolderInvocation TabRequest(string folder)
    {
        if (!FolderCommandLine.TryOpenFolder(folder, out var request, out var error)) throw new InvalidOperationException(error);
        return request with { DestinationId = Guid.NewGuid() };
    }

    private static void TabSearchText(SearchViewModel search, string text)
    {
        search.Text = text;
        // Exercise retained query/panel state without launching a disk-wide
        // search outside the fixture. Search engine behavior has its own tests.
        ((DispatcherTimer)typeof(SearchViewModel).GetField("_typing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!).Stop();
    }

    private static async Task<bool> TabWait(Func<bool> condition, int milliseconds = 15000)
        => await LiveWait(condition, milliseconds) >= 0;

    private static async Task<DragEventArgs> TabCanvasDragAsync(MainWindow context, string target, IDataObject data, DragDropKeyStates keys, RoutedEvent routed)
    {
        var pane = context.ActivePane;
        await pane.FlyToAsync(target, gentle: false, animated: false);
        var folder = pane.Tree.Find(target) ?? throw new InvalidOperationException("The owned tab destination is missing.");
        await pane.Tree.LoadAsync(folder);
        pane.Canvas.UpdateLayout();
        pane.Canvas.RenderNow();
        var cell = pane.Canvas.ScreenRectOf(folder) ?? throw new InvalidOperationException("The owned tab destination has no screen rectangle.");
        var point = pane.Canvas.TranslatePoint(new Point(cell.X + cell.Width / 2, cell.Y + Math.Min(12, cell.Height / 2)), context.ContextRoot);
        var args = SplitDragArgs(routed, data, keys, context.ContextRoot, point);
        pane.Canvas.RaiseEvent(args);
        return args;
    }

    private static async Task<DragDropEffects> TabDropOverAsync(MainWindow context, string target, IDataObject data, DragDropKeyStates keys)
        => (await TabCanvasDragAsync(context, target, data, keys, DragDrop.DragOverEvent)).Effects;

    private static async Task<DragDropEffects> TabDropAsync(MainWindow context, string target, IDataObject data, DragDropKeyStates keys)
    {
        var over = await TabDropOverAsync(context, target, data, keys);
        Check("the selected tab canvas offers the requested file operation", over == (keys == DragDropKeyStates.ControlKey ? DragDropEffects.Copy : DragDropEffects.Move));
        return (await TabCanvasDragAsync(context, target, data, keys, DragDrop.DropEvent)).Effects;
    }

    private static Task TabNativeMenuOwnerAsync(MainWindow context, string selected, MainWindow host)
    {
        var build = typeof(MainWindow).GetMethod("BuildShellMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var menu = (ShellContextMenu?)build.Invoke(context, [false, new[] { selected }, false, null]);
        Check("a native menu built from an unshown tab uses the visible host HWND",
            menu is not null && (IntPtr)typeof(ShellContextMenu).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(menu)! == new WindowInteropHelper(host).Handle
            && new WindowInteropHelper(context).Handle == IntPtr.Zero);
        return Task.CompletedTask;
    }

    private static async Task TabPendingDropCloseAsync(MainWindow host, MainWindow context, string target, string sourceFolder)
    {
        host.SelectTabContext(context, focus: false);
        var sourceFile = Path.Combine(sourceFolder, "pending.bin");
        var destination = Path.Combine(target, "pending.bin");
        await File.WriteAllTextAsync(sourceFile, "async tab drop bytes");
        var (source, sourceThread, proxy) = await ExternalAsyncSourceAsync(sourceFile, destination, "kept-async");
        source.ExpiringPath = sourceFile;
        var priorExists = NativeShellService.OperationItemExists;
        using var permitCopy = new ManualResetEventSlim();
        NativeShellService.OperationItemExists = path =>
        {
            if (ViewAllPath.Equals(path, sourceFile) && !permitCopy.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The owned pending tab copy was not released.");
            return priorExists(path);
        };
        try
        {
            var effect = await TabDropAsync(context, target, new DataObject(proxy), DragDropKeyStates.ControlKey);
            var closing = host.CloseTabContextAsync(context);
            await Task.Delay(80);
            Check("closing a tab waits for an accepted async drop while retaining its source bytes",
                effect == DragDropEffects.Copy && source.Starts == 1 && source.Ends == 0
                && !closing.IsCompleted && !TabShell(context).IsDisposed && File.Exists(sourceFile));
            host.Close();
            var count = host.TabCount;
            var active = host.ActiveTabContext;
            var acceptedSafely = false;
            try { acceptedSafely = ReferenceEquals(host.NewExplorerTab(target), active) && host.TabCount == count; }
            catch (InvalidOperationException) { /* The assertion reports a closing-host command failure. */ }
            Check("opening another tab while the host awaits its drop closes safely without a new context or exception", acceptedSafely);
            permitCopy.Set();
            Check("the async source is released once copied and the tab closes safely",
                await closing.WaitAsync(TimeSpan.FromSeconds(15)) && TabShell(context).IsDisposed
                && await TabWait(() => source.Ends == 1) && source.DestinationAtEnd && !File.Exists(sourceFile)
                && await File.ReadAllTextAsync(destination) == "async tab drop bytes");
            Check("a host waiting on the same pending drop closes all contexts after the source is released",
                await TabWait(() => host.TabCount == 0 && !host.IsVisible));
        }
        finally
        {
            permitCopy.Set();
            NativeShellService.OperationItemExists = priorExists;
            Dispatcher.FromThread(sourceThread)?.InvokeShutdown();
        }
    }

    private static async Task<bool> TabCloseHostAsync(MainWindow host)
    {
        var closed = false;
        host.Closed += (_, _) => closed = true;
        host.Close();
        return await TabWait(() => closed);
    }

    private static async Task TabSaveScreenshotAsync(MainWindow host)
    {
        await SettingsSettle();
        host.UpdateLayout();
        foreach (var pane in host.ActiveTabContext.Panes) pane.Canvas.RenderNow();
        var root = (FrameworkElement)host.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var destination = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TAB_SCREENSHOT")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "explorer-tabs", "tab-host.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var stream = File.Create(destination);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        Console.WriteLine("        tab host screenshot: " + Path.GetFullPath(destination));
    }

    private static async Task TabVolumeMediaReleaseAsync(MainWindow host, MainWindow first, MainWindow inactive, string folder)
    {
        var notifications = (VolumeNotifications)OwnerCloseField(typeof(MainWindow), "_volumeNotifications").GetValue(first)!;
        var root = TabShell(first).Changes.RootFor(folder) ?? throw new InvalidOperationException("The owned context has no volume watch.");
        Check("the owned context registers its volume watch on the shared host", await TabWait(() => notifications.NotificationFor(root) != IntPtr.Zero));
        var notification = notifications.NotificationFor(root);
        if (notification == IntPtr.Zero) return;
        var standalone = new MainWindow();
        new WindowInteropHelper(standalone).EnsureHandle();
        var sameVolume = root.Key.TrimEnd('\\', '/') + "\\owned-media.mp4";
        var otherVolume = ViewAllPath.Equals(root.Key.TrimEnd('\\', '/'), "Z:") ? @"Y:\other-media.mp4" : @"Z:\other-media.mp4";
        QuickPreviewWindow Preview(Window owner, string path, out NativePreviewHost native)
        {
            var preview = new QuickPreviewWindow { Owner = owner, ShowActivated = false };
            native = new NativePreviewHost();
            OwnerCloseField(typeof(QuickPreviewWindow), "_native").SetValue(preview, native);
            OwnerCloseField(typeof(QuickPreviewWindow), "_path").SetValue(preview, path);
            return preview;
        }
        var firstPreview = Preview(host, sameVolume, out var firstNative);
        var inactivePreview = Preview(host, sameVolume, out var inactiveNative);
        var standalonePreview = Preview(standalone, sameVolume, out var standaloneNative);
        var otherPreview = Preview(standalone, otherVolume, out var otherNative);
        OwnerCloseField(typeof(MainWindow), "_quickPreview").SetValue(first, firstPreview);
        OwnerCloseField(typeof(MainWindow), "_quickPreview").SetValue(inactive, inactivePreview);
        OwnerCloseField(typeof(MainWindow), "_quickPreview").SetValue(standalone, standalonePreview);
        try
        {
            Check("volume fixtures contain only unshown preview hosts with no player process", !firstPreview.IsVisible && !inactivePreview.IsVisible
                && !standalonePreview.IsVisible && !otherPreview.IsVisible && !standalone.IsVisible);
            using var message = new HandleMessage(notification);
            typeof(MainWindow).GetMethod("HandleWindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host,
                [new WindowInteropHelper(host).Handle, 0x0219, (IntPtr)0x8001, message.Pointer, false]);
            bool Disposed(NativePreviewHost native) => (bool)OwnerCloseField(typeof(NativePreviewHost), "_disposed").GetValue(native)!;
            Check("a query about one tab's watch releases media from that volume in inactive tabs and standalone windows",
                Disposed(firstNative) && Disposed(inactiveNative) && Disposed(standaloneNative));
            Check("the same notification leaves previews of other volumes alive", !Disposed(otherNative)
                && ReferenceEquals(OwnerCloseField(typeof(QuickPreviewWindow), "_native").GetValue(otherPreview), otherNative));
            // Restore only this process's watch after its synthetic query.
            typeof(MainWindow).GetMethod("HandleWindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host,
                [new WindowInteropHelper(host).Handle, 0x0219, (IntPtr)0x8002, message.Pointer, false]);
        }
        finally
        {
            firstPreview.CompleteOwnerClose();
            inactivePreview.CompleteOwnerClose();
            standalonePreview.CompleteOwnerClose();
            otherPreview.CompleteOwnerClose();
            OwnerCloseField(typeof(MainWindow), "_quickPreview").SetValue(first, null);
            OwnerCloseField(typeof(MainWindow), "_quickPreview").SetValue(inactive, null);
            standalone.CloseFromCaller();
            await SettingsSettle();
        }
    }

    private static async Task TabHoverWindowEventsAsync(MainWindow host, MainWindow context, string file)
    {
        // Copy completion precedes the directory refresh on purpose. Wait for
        // those actual filesystem/camera updates before beginning a hover that
        // they are correctly required to dismiss.
        long lastChange = Environment.TickCount64;
        void CameraChanged() => lastChange = Environment.TickCount64;
        void PathRefreshed(string _) => lastChange = Environment.TickCount64;
        void TreeChanged(object? _, EventArgs __) => lastChange = Environment.TickCount64;
        context.FirstPane.Canvas.CameraChanged += CameraChanged;
        context.FirstPane.Tree.Changed += TreeChanged;
        TabShell(context).Tree.PathRefreshed += PathRefreshed;
        try
        {
            await TabWait(() => Environment.TickCount64 - lastChange >= 700
                && context.Panes.All(pane => pane.FlightsUnderWay == 0)
                && !TabShell(context).Tree.FolderList.IsLoading);
        }
        finally
        {
            context.FirstPane.Canvas.CameraChanged -= CameraChanged;
            context.FirstPane.Tree.Changed -= TreeChanged;
            TabShell(context).Tree.PathRefreshed -= PathRefreshed;
        }
        context.SetHoverPreviewTarget(file, new Point(400, 300), context.FirstPane.Canvas);
        Check("the retained tab loads its own content hover", await TabWait(() => context.HoverPreviewsForChecks.Result is not null));
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [EventArgs.Empty]);
        Check("host deactivation dismisses the embedded tab's hover", context.HoverPreviewsForChecks.Path is null
            && context.PreviewCardForChecks.Visibility == Visibility.Collapsed);
        context.SetHoverPreviewTarget(file, new Point(400, 300), context.FirstPane.Canvas);
        Check("the embedded hover can resume after host deactivation", await TabWait(() => context.HoverPreviewsForChecks.Result is not null));
        var width = host.Width;
        host.Width = width + 24;
        await SettingsSettle();
        Check("physical host resizing dismisses the embedded hover", context.HoverPreviewsForChecks.Path is null
            && context.PreviewCardForChecks.Visibility == Visibility.Collapsed);
        host.Width = width;
        await SettingsSettle();
    }

    private static async Task TabKeyboardAndReorderAsync(MainWindow host, MainWindow first, MainWindow second, MainWindow last)
    {
        var method = typeof(MainWindow).GetMethod("TryTabKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Key(Key key, ModifierKeys modifiers) => (bool)method.Invoke(host, [key, modifiers])!;
        Check("Ctrl+Tab switches to the next complete context", Key(System.Windows.Input.Key.Tab, ModifierKeys.Control)
            && ReferenceEquals(host.ActiveTabContext, second) && !second.IsUntouchedHandoff && !second.IsUnusedFolderRequest);
        Check("Ctrl+Shift+Tab switches back without rebuilding the original context", Key(System.Windows.Input.Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)
            && ReferenceEquals(host.ActiveTabContext, first));
        Check("Ctrl+9 selects the last tab and Ctrl+1 returns to the first", Key(System.Windows.Input.Key.D9, ModifierKeys.Control)
            && ReferenceEquals(host.ActiveTabContext, last) && !last.IsUntouchedHandoff && !last.IsUnusedFolderRequest
            && Key(System.Windows.Input.Key.D1, ModifierKeys.Control)
            && ReferenceEquals(host.ActiveTabContext, first));
        var panel = (Panel)host.FindName("TabHeaderPanel");
        var firstHeader = (Border)panel.Children[0];
        var secondHeader = (Border)panel.Children[1];
        var lastHeader = (Border)panel.Children[2];
        var format = (string)typeof(MainWindow).GetField("TabDragFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var data = new DataObject();
        data.SetData(format, last, autoConvert: false);
        var over = SplitDragArgs(DragDrop.DragOverEvent, data, DragDropKeyStates.None, firstHeader, new Point(30, 15));
        firstHeader.RaiseEvent(over);
        var drop = SplitDragArgs(DragDrop.DropEvent, data, DragDropKeyStates.None, firstHeader, new Point(30, 15));
        firstHeader.RaiseEvent(drop);
        Check("dragging a tab reorders headers and contexts while preserving the active selection",
            over.Effects == DragDropEffects.Move && drop.Effects == DragDropEffects.Move
            && host.TabContexts.SequenceEqual([last, first, second]) && ReferenceEquals(panel.Children[0], lastHeader)
            && ReferenceEquals(host.ActiveTabContext, first));
        secondHeader.RaiseEvent(SplitDragArgs(DragDrop.DropEvent, data, DragDropKeyStates.None, secondHeader, new Point(30, 15)));
        Check("a reordered tab can be returned to its original position without changing context identity",
            host.TabContexts.SequenceEqual([first, second, last]) && ReferenceEquals(panel.Children[2], lastHeader));

        Check("Ctrl+T creates a fresh context", Key(System.Windows.Input.Key.T, ModifierKeys.Control) && host.TabCount == 4);
        var unopened = host.ActiveTabContext;
        // Close in the same dispatcher turn, before Loaded can read the
        // default profile folder. Only the fixture's isolated state is used.
        Check("Ctrl+W immediately closes the newly opened tab", Key(System.Windows.Input.Key.W, ModifierKeys.Control));
        Check("closing before Loaded disposes the context and releases its waiting folder request",
            await TabWait(() => host.TabCount == 3 && TabShell(unopened).IsDisposed && !unopened.IsFolderInvocationPending)
            && new WindowInteropHelper(unopened).Handle == IntPtr.Zero);
        host.SelectTabContext(first, focus: false);
    }

    private static void TabHandoffOwnershipChecks(MainWindow host, MainWindow first, MainWindow second, MainWindow last)
    {
        void Prepare(MainWindow context) => context.PrepareFolderInvocation(TabRequest(context.CurrentFolderPath)
            with { OriginIsShell = true, DestinationId = context.FolderDestinationId });
        Prepare(first);
        Prepare(second);
        Prepare(last);
        host.SelectTabContext(first, focus: false);
        host.QueueTabHover(second);
        host.CompleteTabHover();
        Check("programmatic selection and drag hover preserve an untouched Shell handoff",
            first.IsUntouchedHandoff && first.IsUnusedFolderRequest && second.IsUntouchedHandoff && second.IsUnusedFolderRequest);
        var panel = (Panel)host.FindName("TabHeaderPanel");
        var firstHeader = (Border)panel.Children[0];
        ((Button)((Grid)firstHeader.Child).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("explicit tab header selection makes the Shell handoff the user's context",
            ReferenceEquals(host.ActiveTabContext, first) && !first.IsUntouchedHandoff && !first.IsUnusedFolderRequest);
        var paths = new[] { Path.Combine(first.CurrentFolderPath, "selected.txt") };
        var data = new DataObject(DataFormats.FileDrop, paths);
        var drop = SplitDragArgs(DragDrop.PreviewDropEvent, data, DragDropKeyStates.ControlKey, second.ContextRoot, new Point(400, 300));
        drop.Handled = true;
        second.ContextRoot.RaiseEvent(drop);
        Check("even an already handled file drop protects its destination handoff without consuming the payload",
            !second.IsUntouchedHandoff && !second.IsUnusedFolderRequest
            && data.GetData(DataFormats.FileDrop) is string[] kept && kept.SequenceEqual(paths));
        var source = HwndSource.FromHwnd(new WindowInteropHelper(host).Handle)!;
        last.ContextRoot.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.L)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Handled = true });
        Check("keyboard input already handled by the shared host still protects the chosen tab's handoff",
            !last.IsUntouchedHandoff && !last.IsUnusedFolderRequest);
        Prepare(second);
        Prepare(last);
    }
}
