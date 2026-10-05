using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>The actual WPF/broker fixture runs on its own never-switched desktop
/// with a fresh state directory. It never launches or modifies Windows Explorer.</summary>
internal static class FolderRouterFixture
{
    private static string _result = string.Empty;
    private static int _failures;

    internal static int Run(string[] args)
    {
        var at = Array.IndexOf(args, "--folder-router-fixture");
        if (at < 0 || at + 1 >= args.Length) return 2;
        var root = Path.GetFullPath(args[at + 1]);
        _result = Path.Combine(root, "results.txt");
        Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        // Use the exact product theme without product OnStartup, which would
        // create an additional window and run the integration controller.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Nodify;component/Themes/Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/PickerControls.xaml", UriKind.Relative) });
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await ChecksAsync(root); }
            catch (Exception ex) { Check("fixture completed without an exception: " + ex, false); }
            finally
            {
                foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) window.Close();
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return _failures == 0 ? 0 : 1;
    }


    private static void Check(string name, bool passed)
    {
        if (!passed) _failures++;
        File.AppendAllText(_result, (passed ? "ok " : "FAIL ") + name + Environment.NewLine);
    }
    private static void Mode(bool enabled)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        // No settings controller/startup/association writes in this fixture.
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = enabled }));
    }
    private static async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", ExplorerLaunchRouter.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000); return pipe;
    }
    private static async Task<FolderRouteReceipt> RouteAsync(FolderInvocation invocation, Guid? request = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await ExplorerLaunchRouter.SendAsync(new(request ?? Guid.NewGuid(), invocation, true), deadline.Token);
    }

    private static async Task ChecksAsync(string root)
    {
        Mode(false);
        var basic = Path.Combine(root, "ordinary folder Ж"); Directory.CreateDirectory(basic);
        await new WorkspaceStore().SaveAsync(new WorkspaceState { IsSplit = true, SidebarWidth = 267 });
        FolderCommandLine.TryOpenFolder(basic, out var open, out _);
        var first = new MainWindow { ShowActivated = false };
        ExplorerLaunchRouter.Attach(first); first.Show();
        Check("a real normal window finishes its folder navigation", await first.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(20)));
        Check("the normal tile window has no picker footer", !first.IsPickerMode && first.PickerFooter.Visibility != Visibility.Visible);
        Check("manual UltraExplorer commands work with integration off", ExplorerLaunchRouter.ReceiptMatches(await RouteAsync(open), open));

        var navigationPane = first.ActivePane;
        var missing = new FolderInvocation(FolderInvocationKind.OpenFolder, Path.Combine(root, "removed while requested"), []);
        Check("a vanished folder request is refused", !await first.ApplyFolderInvocationAsync(missing));
        Check("failed navigation releases the normal overview's startup loading gate", navigationPane.Canvas.LoadUnfocusedRoots);
        var alternate = Path.Combine(root, "newer ordinary folder"); Directory.CreateDirectory(alternate);
        FolderCommandLine.TryOpenFolder(alternate, out var alternative, out _);
        using (var canceled = new CancellationTokenSource())
        {
            var late = first.ApplyFolderInvocationAsync(alternative, canceled.Token);
            canceled.Cancel();
            Check("a broker cancellation ends the pending navigation intent", !await late && first.CurrentFolderPath == basic);
            Check("cancellation restores the overview load gate", navigationPane.Canvas.LoadUnfocusedRoots);
            Check("an already canceled request leaves the live folder untouched", !await first.ApplyFolderInvocationAsync(alternative, canceled.Token)
                && first.CurrentFolderPath == basic && navigationPane.Canvas.LoadUnfocusedRoots);
        }
        var oldRequest = first.ApplyFolderInvocationAsync(alternative);
        var newRequest = first.ApplyFolderInvocationAsync(open);
        Check("a superseded folder invocation cannot overwrite the new one", !await oldRequest && await newRequest
            && first.CurrentFolderPath == basic && first.ActivePane.Canvas.LoadUnfocusedRoots);

        var sibling = first.Panes.First(pane => !ReferenceEquals(pane, navigationPane));
        var switched = first.ApplyFolderInvocationAsync(alternative);
        ((INestedPaneHost)first).ActivatePane(sibling);
        Check("a pane switch invalidates a folder request still in flight", !await switched);
        Check("an inactive pane's failed request releases only its own loading gate", navigationPane.Canvas.LoadUnfocusedRoots
            && sibling.Canvas.LoadUnfocusedRoots);
        ((INestedPaneHost)first).ActivatePane(navigationPane);
        Check("navigation resumes after a canceled pane request", await first.ApplyFolderInvocationAsync(open));

        ((INestedPaneHost)first).ActivatePane(sibling);
        var detached = first.ApplyFolderInvocationAsync(alternative);
        ((MainViewModel)first.DataContext).IsSplit = false;
        Check("detaching the requested pane cancels its stale navigation", !await detached && !first.Panes.Contains(sibling));
        Check("detached-pane cleanup releases its own gate without changing the active pane", sibling.Canvas.LoadUnfocusedRoots
            && first.ActivePane.Canvas.LoadUnfocusedRoots);
        ((MainViewModel)first.DataContext).IsSplit = true;
        Check("a normal pane can reopen the requested folder after detach", await first.ApplyFolderInvocationAsync(open));

        Check("startup forwarding runs on an actual STA DispatcherSynchronizationContext",
            Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && SynchronizationContext.Current is DispatcherSynchronizationContext);
        var forwarding = Stopwatch.StartNew();
        Check("the synchronous startup bridge forwards without blocking its WPF continuation context",
            ExplorerLaunchRouter.TryForward(open) && forwarding.Elapsed < TimeSpan.FromSeconds(3));

        await using (var malformed = await ConnectAsync()) await ExplorerLaunchRouter.WritePacketAsync<object?>(malformed, null, CancellationToken.None);
        await using (var malformed = await ConnectAsync())
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, ExplorerLaunchRouter.MaximumPacket + 1);
            await malformed.WriteAsync(header);
        }
        var nullInvocation = await ExplorerLaunchRouter.SendAsync(new(Guid.NewGuid(), null!, true), CancellationToken.None);
        Check("a null invocation is refused", !nullInvocation.Accepted && !nullInvocation.Ready);
        Check("malformed and oversized packets leave the resident channel usable", ExplorerLaunchRouter.ReceiptMatches(await RouteAsync(open), open));

        var before = Application.Current.Windows.OfType<MainWindow>().Count();
        var abandoned = open with { DestinationId = Guid.NewGuid() };
        await using (var pipe = await ConnectAsync())
        {
            await ExplorerLaunchRouter.WritePacketAsync(pipe, new FolderRouteRequest(Guid.NewGuid(), abandoned, true), CancellationToken.None);
            var offer = await ExplorerLaunchRouter.ReadPacketAsync<FolderRouteReceipt>(pipe, CancellationToken.None);
            Check("the offer precedes destination creation", offer.Accepted && Application.Current.Windows.OfType<MainWindow>().Count() == before);
            // Deliberately disconnect without committing.
        }
        await Task.Delay(150);
        Check("an abandoned offer never creates a late duplicate window", Application.Current.Windows.OfType<MainWindow>().Count() == before);
        var off = await RouteAsync(open with { OriginIsShell = true, DestinationId = Guid.NewGuid() });
        Check("shell-origin requests are canceled with integration off", !off.Accepted && Application.Current.Windows.OfType<MainWindow>().Count() == before);

        Mode(true);
        await using (var pipe = await ConnectAsync())
        {
            var pending = open with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
            await ExplorerLaunchRouter.WritePacketAsync(pipe, new FolderRouteRequest(Guid.NewGuid(), pending, true), CancellationToken.None);
            var offer = await ExplorerLaunchRouter.ReadPacketAsync<FolderRouteReceipt>(pipe, CancellationToken.None);
            Check("an enabled shell request can be offered", offer.Accepted);
            Mode(false); pipe.WriteByte(1);
        }
        await Task.Delay(150);
        Check("turning off between offer and commit preserves every existing window", Application.Current.Windows.OfType<MainWindow>().Count() == before);

        var originalShell = (MainViewModel)first.DataContext;
        originalShell.Marks.SetNote(basic, "main window marker");
        await originalShell.SaveNowAsync();
        // Freeze only the fixture's original autosave after its sentinel is
        // saved. Camera/background work in that window must not be confused
        // with writes made by the adopted windows under test.
        originalShell.SuppressShellWrites = true;
        originalShell.Tree.SuppressWrites = true;
        await Task.Delay(100);
        var globalFiles = new[] { AppPaths.State("workspace.json"), AppPaths.State("view-all.workspace.json"), AppPaths.State("folder-marks.json") };
        var globalBytes = globalFiles.ToDictionary(path => path, File.ReadAllBytes);
        Check("the original normal window keeps the user's two-pane preference", originalShell.IsSplit && first.Panes.Count == 2);

        var receipts = new List<(FolderInvocation Invocation, FolderRouteReceipt Receipt, Guid Request)>();
        for (var index = 0; index < 7; index++)
        {
            var folder = Path.Combine(root, "source " + index); Directory.CreateDirectory(folder);
            var a = Path.Combine(folder, "file one Ж.txt"); var b = Path.Combine(folder, "file two.txt");
            File.WriteAllText(a, "one"); File.WriteAllText(b, "two");
            FolderCommandLine.TryReveal([a, b], out var invocation, out _);
            invocation = invocation with { DestinationId = Guid.NewGuid() };
            var request = Guid.NewGuid(); var receipt = await RouteAsync(invocation, request);
            Check("source frame " + index + " has its exact ready folder and complete selection", ExplorerLaunchRouter.ReceiptMatches(receipt, invocation));
            var window = Application.Current.Windows.OfType<MainWindow>().Single(item => item.FolderDestinationId == invocation.DestinationId);
            var shell = (MainViewModel)window.DataContext;
            var workspace = ExplorerLaunchRouter.FolderWorkspacePath(invocation.DestinationId);
            Check("source frame " + index + " has one normal pane and the user's own workspace and marks",
                !shell.IsSplit && window.Panes.Count == 1 && shell.SidebarWidth == 267
                && shell.Marks.StatePath == AppPaths.State("folder-marks.json")
                && shell.Marks.Get(basic).Note == "main window marker");
            Check("source frame " + index + " is a real normal HWND with its own selected tiles",
                new WindowInteropHelper(window).Handle.ToInt64() == receipt.Window && !window.IsPickerMode && window.Nested.SelectedCount == 2
                && window.CurrentFolderPath == folder && window.CurrentFolderSelection.SequenceEqual([a, b]));
            var bounds = window.Nested.ScreenRectOf(window.ActivePane.Tree.Find(folder)!);
            Check("source frame " + index + " visibly frames its folder at a readable local zoom",
                window.Nested.CaptureCamera() is { AnchorPath: var anchor, Width: >= 0.4 and <= 1.5 }
                && anchor == folder && bounds is { Width: > 200, Height: > 100 }
                && bounds.Value.IntersectsWith(new Rect(0, 0, window.Nested.ActualWidth, window.Nested.ActualHeight))
                && ((UltraExplorer.ViewModels.MainViewModel)window.DataContext).Address.CurrentPath == folder
                && window.Nested.ZoomText.Length < 12 && !window.Nested.ZoomText.Contains('M') && !window.Nested.ZoomText.Contains('K'));
            var loadedFolder = window.ActivePane.Tree.Find(folder)!;
            var tileRects = loadedFolder.Files.Select((file, tile) =>
            {
                var origin = loadedFolder.FileGrid.Origin(tile);
                return new Rect(bounds!.Value.X + origin.X * bounds.Value.Width, bounds.Value.Y + origin.Y * bounds.Value.Width,
                    loadedFolder.FileGrid.TileWidth * bounds.Value.Width, loadedFolder.FileGrid.TileHeight * bounds.Value.Width);
            }).ToArray();
            Check("source frame " + index + " shows both selected file tiles on screen",
                tileRects.Length == 2 && tileRects.All(rect => rect.Width > 100 && rect.Height > 20
                    && new Rect(0, 0, window.Nested.ActualWidth, window.Nested.ActualHeight).Contains(rect)));
            if (index == 0)
            {
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(root, "folder-router-ui.png")); png.Save(output);
                File.WriteAllText(Path.Combine(root, "folder-router-ui.json"), JsonSerializer.Serialize(new
                {
                    Folder = window.CurrentFolderPath, Address = ((UltraExplorer.ViewModels.MainViewModel)window.DataContext).Address.CurrentPath,
                    Camera = window.Nested.CaptureCamera(), Zoom = window.Nested.ZoomText, FolderBounds = bounds,
                    Selection = window.CurrentFolderSelection, SelectedTileRects = tileRects
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            receipts.Add((invocation, receipt, request));
        }
        Check("seven source frames retain seven distinct destinations", receipts.Select(item => item.Receipt.Window).Distinct().Count() == 7
            && Application.Current.Windows.OfType<MainWindow>().Count() == before + 7);
        foreach (var item in receipts)
            Check("earlier frame " + item.Receipt.DestinationId + " still retains its original context",
                await ExplorerLaunchRouter.ValidateReadyAsync(item.Receipt, item.Invocation, CancellationToken.None));

        var original = receipts[0];
        var moved = Application.Current.Windows.OfType<MainWindow>().Single(item => item.FolderDestinationId == original.Invocation.DestinationId);
        await moved.ApplyFolderInvocationAsync(open);
        var repeated = await RouteAsync(original.Invocation, original.Request);
        Check("replaying an accepted id never navigates a changed destination back", !repeated.Ready && moved.CurrentFolderPath == basic);
        Check("fresh verification refuses an earlier ready receipt after navigation",
            !await ExplorerLaunchRouter.ValidateReadyAsync(original.Receipt, original.Invocation, CancellationToken.None));
        ((MainViewModel)moved.DataContext).Marks.SetNote(basic, "edited in an adopted window");
        foreach (var item in receipts)
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single(candidate => candidate.FolderDestinationId == item.Invocation.DestinationId);
            await ((MainViewModel)window.DataContext).SaveNowAsync();
            var workspace = ExplorerLaunchRouter.FolderWorkspacePath(item.Invocation.DestinationId);
            Check("source frame " + item.Invocation.DestinationId + " keeps only its own view in a file of its own",
                File.Exists(workspace + ".tree.json") && !File.Exists(workspace) && !File.Exists(workspace + ".marks.json"));
            window.Close();
        }
        using (var closed = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (Application.Current.Windows.OfType<MainWindow>().Any(window => receipts.Any(item => item.Invocation.DestinationId == window.FolderDestinationId)))
                await Task.Delay(20, closed.Token);
        }
        var sharedMarks = new FolderMarkService();
        await sharedMarks.LoadAsync();
        Check("closing all adopted windows preserves the main window's camera and split",
            File.ReadAllBytes(globalFiles[1]).SequenceEqual(globalBytes[globalFiles[1]])
            && await new WorkspaceStore().LoadAsync() is { IsSplit: true, SidebarWidth: 267 }
            && originalShell.IsSplit && first.Panes.Count == 2);
        Check("and the note edited in an adopted window is kept where every window reads it",
            sharedMarks.Get(basic).Note == "edited in an adopted window");
        Check("the fixture state is isolated from the real user workspace", AppPaths.StateDirectory == Path.Combine(root, "state"));
        File.WriteAllText(Path.Combine(root, "destinations.json"), JsonSerializer.Serialize(receipts.Select(item => item.Receipt), new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static partial class Program
{
    private static async Task FolderRouterChecks()
    {
        Section("owned UltraExplorer windows and resident folder IPC on an inactive desktop");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFolderRouter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerFolderRouter-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var arguments = NativeShellService.BuildCommandLine([executable, "--folder-router-fixture", root]);
        FixtureProcess process = default;
        try
        {
            if (!CreateProcess(executable, new System.Text.StringBuilder(arguments), 0, 0, false, 0x08000000,
                0, Path.GetDirectoryName(executable), ref startup, out process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
            foreach (var line in File.ReadAllLines(Path.Combine(root, "results.txt")))
                Check(line.StartsWith("ok ") ? line[3..] : line.StartsWith("FAIL ") ? line[5..] : line, line.StartsWith("ok "));
            Check("the owned GUI fixture exits successfully", child.ExitCode == 0);
            var pictures = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(pictures);
            if (File.Exists(Path.Combine(root, "folder-router-ui.png")))
                File.Copy(Path.Combine(root, "folder-router-ui.png"), Path.Combine(pictures, "folder-router-ui.png"), true);
            if (File.Exists(Path.Combine(root, "folder-router-ui.json")))
                File.Copy(Path.Combine(root, "folder-router-ui.json"), Path.Combine(pictures, "folder-router-ui.json"), true);
        }
        finally
        {
            if (process.Handle != 0 && GetExitCodeProcess(process.Handle, out var code) && code == 259)
            { TerminateProcess(process.Handle, 1); WaitForSingleObject(process.Handle, 1000); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Handle != 0) CloseHandle(process.Handle);
            CloseDesktop(desktop);
            TryDelete(root);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FixtureStartup
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags; public ushort Show, ReservedCount;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureProcess { public nint Handle, Thread; public uint Id, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, System.Text.StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit,
        uint flags, nint environment, string? directory, ref FixtureStartup startup, out FixtureProcess process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
