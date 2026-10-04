using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace ViewAllSmoke;

/// <summary>
/// J001: a drop from another program held that program - the desktop, an
/// Explorer window, a browser - inside its DoDragDrop until the whole copy and
/// the folder re-reads after it had finished.  The real tree and nested Drop
/// handlers are given the Shell's own data object for a file, and one that
/// lives in another apartment and can wait (IDataObjectAsyncCapability), while
/// the copy is slowed on its own thread.  A drop from outside the temporary
/// folder must return before the copy ends; one from the temporary folder,
/// where archive managers extract, is still held, but only until the copy
/// ends.  Everything runs in an owned child on an inactive desktop.
/// </summary>
internal static partial class Program
{
    private const string ExternalDropChildVariable = "ULTRAEXPLORER_EXTERNAL_DROP_CHILD";
    private const int ExternalDropCopyDelayMs = 2000;

    private static async Task ExternalDropReleaseChecks()
    {
        if (Environment.GetEnvironmentVariable(ExternalDropChildVariable) is { Length: > 0 } childRoot)
        {
            var output = new StreamWriter(Path.Combine(childRoot, "results.txt"), false, Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(output);
            RunOnSta("external drop release", () => ExternalDropOnStaAsync(childRoot));
            return;
        }

        Section("external drops: the source is let go of (J001)");
        // Beside the temporary folder, not in it: these stand for files a
        // source keeps, as the desktop's or an Explorer window's are.
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(Path.GetDirectoryName(temp)!, "UltraExplorerExternalDrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerExternalDrop-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var roleBefore = Environment.GetEnvironmentVariable(ExternalDropChildVariable);
        var stateBefore = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var testBefore = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
        FixtureProcess process = default;
        try
        {
            Environment.SetEnvironmentVariable(ExternalDropChildVariable, root);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
            var command = NativeShellService.BuildCommandLine([executable, "--only", nameof(ExternalDropReleaseChecks)]);
            if (!CreateProcess(executable, new StringBuilder(command), 0, 0, false, 0x08000000,
                0, Path.GetDirectoryName(executable), ref startup, out process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Environment.SetEnvironmentVariable(ExternalDropChildVariable, roleBefore);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, stateBefore);
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testBefore);
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));
            foreach (var line in File.ReadAllLines(Path.Combine(root, "results.txt")))
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line.Length > 0) Console.WriteLine(line);
            }
            Check("the owned external drop child completes successfully", child.ExitCode == 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExternalDropChildVariable, roleBefore);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, stateBefore);
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testBefore);
            if (process.Handle != 0 && GetExitCodeProcess(process.Handle, out var code) && code == 259)
            { TerminateProcess(process.Handle, 1); WaitForSingleObject(process.Handle, 1000); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Handle != 0) CloseHandle(process.Handle);
            CloseDesktop(desktop);
            ArchiveDeleteOwned(root, root);
        }
    }

    private static async Task ExternalDropOnStaAsync(string root)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        Check("external drop fixture uses its own state and test-window mode, with its kept files outside the temporary folder",
            Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && ViewAllPath.Equals(AppPaths.StateDirectory, Path.Combine(root, "state"))
            && !root.StartsWith(temp, StringComparison.OrdinalIgnoreCase));
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var temporaryRoot = Path.Combine(temp, "UltraExplorerExternalDrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var nested in new[] { true, false })
                foreach (var scenario in new[] { "kept", "kept-async", "temporary", "temporary-async" })
                    await ExternalDropRouteAsync(scenario.StartsWith("temporary", StringComparison.Ordinal) ? temporaryRoot : root, nested, scenario);
        }
        finally
        {
            ArchiveDeleteOwned(temporaryRoot, temporaryRoot);
        }
    }

    private static async Task ExternalDropRouteAsync(string root, bool nested, string scenario)
    {
        var label = (nested ? "nested canvas" : "tree editor") + " / " + scenario;
        Section("external drop release: " + label);
        var owned = Path.Combine(root, nested ? "nested" : "tree", scenario);
        var target = Path.Combine(owned, "destination");
        var sourceFile = Path.Combine(owned, "source", "payload.bin");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        var payload = Encoding.UTF8.GetBytes("external drop payload for " + label);
        File.WriteAllBytes(sourceFile, payload);
        var destination = Path.Combine(target, "payload.bin");
        var workspace = Path.Combine(owned, "workspace.json");
        await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", CanvasLayout = nested ? "Nested" : "Tree" });
        File.WriteAllText(workspace + ".marks.json", "{}");

        var async = scenario.EndsWith("-async", StringComparison.Ordinal);
        var held = scenario.StartsWith("temporary", StringComparison.Ordinal);
        ExternalAsyncSource? asyncSource = null;
        Thread? sourceThread = null;
        object offered;
        if (async)
        {
            (asyncSource, sourceThread, offered) = await ExternalAsyncSourceAsync(sourceFile, destination);
            Check(label + ": the source that can wait reaches the window as another apartment's COM object", Marshal.IsComObject(offered));
        }
        else
        {
            offered = ExternalShellDataObject(sourceFile);
        }
        var data = new DataObject(offered);

        var main = new MainWindow(null, workspace);
        Application.Current.MainWindow = main;
        var shell = (MainViewModel)main.DataContext;
        var operationExistsBefore = NativeShellService.OperationItemExists;
        var copyEnded = false;
        try
        {
            new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
            await shell.InitializeAsync(target);
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(owned, "Owned external drop fixture", NestedFolderKind.Drive)]);
            shell.Layout = nested ? CanvasLayout.Nested : CanvasLayout.Tree;
            shell.Tree.PreferLightReveal = nested;
            if (!nested) await main.TreeEntryForChecks;
            var node = await shell.Tree.RevealPathAsync(target, focus: false);
            Check(label + ": the destination node is present", node is not null && ViewAllPath.Equals(node.FullPath, target));
            if (node is null) return;
            if (nested)
            {
                await main.FirstPane.FlyToAsync(target, gentle: false, animated: false);
                await main.FirstPane.Tree.LoadAsync(main.FirstPane.Tree.Find(target)!);
            }
            else
            {
                node.Location = new Point(30000, 30000);
                main.Editor.ViewportZoom = 1;
                main.Editor.ViewportLocation = new Point(node.Bounds.X - 100, node.Bounds.Y - 100);
            }
            LayOutWindow(main, 1400, 900);
            await SettingsSettle();
            var host = SettingsHosts[main];
            FrameworkElement dropView = nested ? main.FirstPane.Canvas : main.Editor;
            Point point;
            if (nested)
            {
                main.FirstPane.Canvas.RenderNow();
                var cell = main.FirstPane.Canvas.ScreenRectOf(main.FirstPane.Tree.Find(target)!);
                Check(label + ": its destination tile is on the canvas", cell is not null);
                if (cell is null) return;
                point = main.FirstPane.Canvas.TranslatePoint(new Point(cell.Value.X + cell.Value.Width / 2,
                    cell.Value.Y + Math.Min(12, cell.Value.Height / 2)), host);
            }
            else
            {
                node.Location += new Vector(1, 1);
                shell.Tree.SpatialIndex.AddOrUpdate(node);
                var graphPoint = new Point(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
                var localPoint = new Point((graphPoint.X - main.Editor.ViewportLocation.X) * main.Editor.ViewportZoom,
                    (graphPoint.Y - main.Editor.ViewportLocation.Y) * main.Editor.ViewportZoom);
                point = main.Editor.TranslatePoint(localPoint, host);
                var probe = ArchiveDragArgs(DragDrop.PreviewDragOverEvent, data, host, point);
                var actualTarget = shell.Tree.FindNearestDropTarget(main.Editor.GetLocationInsideEditor(probe), 96 / Math.Max(main.Editor.ViewportZoom, 0.05));
                Check(label + ": the editor hit test resolves the destination", actualTarget is not null && ViewAllPath.Equals(actualTarget.FullPath, target));
                if (actualTarget is null || !ViewAllPath.Equals(actualTarget.FullPath, target)) return;
            }
            DragEventArgs Raise(RoutedEvent routed)
            {
                var args = ArchiveDragArgs(routed, data, host, point);
                dropView.RaiseEvent(args);
                return args;
            }
            var over = Raise(nested ? DragDrop.DragOverEvent : DragDrop.PreviewDragOverEvent);
            Check(label + ": the source's file is offered Copy", over.Handled && over.Effects == DragDropEffects.Copy);

            // The copy itself is slow: the Shell's operation waits on its own
            // thread before it starts, as a big file or a slow disk makes it.
            var fixtureSource = Path.GetDirectoryName(sourceFile)! + Path.DirectorySeparatorChar;
            NativeShellService.OperationItemExists = path =>
            {
                if (path.StartsWith(fixtureSource, StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(ExternalDropCopyDelayMs);
                    Volatile.Write(ref copyEnded, true);
                }
                return operationExistsBefore(path);
            };

            var closed = false;
            main.Closed += (_, _) => closed = true;
            var clock = Stopwatch.StartNew();
            var dropped = Raise(nested ? DragDrop.DropEvent : DragDrop.PreviewDropEvent);
            var dropMs = clock.Elapsed.TotalMilliseconds;
            var endedAtReturn = Volatile.Read(ref copyEnded);
            var copiedAtReturn = ArchiveBytesEqual(destination, payload);
            var busyAtReturn = shell.Toast.IsBusy;
            var startsAtReturn = asyncSource?.Starts ?? 0;
            var endsAtReturn = asyncSource?.Ends ?? 0;
            Console.WriteLine($"        {label}: Drop returned after {dropMs:F0} ms (copy delay {ExternalDropCopyDelayMs} ms), copyEnded={endedAtReturn}, copied={copiedAtReturn}, toastBusy={busyAtReturn}, effect={dropped.Effects}, starts={startsAtReturn}, ends={endsAtReturn}");
            Check(label + ": the drop is reported as a copy", dropped.Handled && dropped.Effects == DragDropEffects.Copy);
            if (held)
            {
                Check(label + ": a source whose files lie in the temporary folder is held until they are copied",
                    endedAtReturn && copiedAtReturn && dropMs >= ExternalDropCopyDelayMs);
                Check(label + ": ... and let go of before the folders are read again", busyAtReturn);
                Check(label + ": ... without being told the copy goes on after Drop", startsAtReturn == 0);
            }
            else
            {
                Check(label + ": a source whose files are kept is let go of before the copy ends",
                    !endedAtReturn && !copiedAtReturn && dropMs < ExternalDropCopyDelayMs);
                if (async)
                    Check(label + ": ... and a source that can wait is told the copy goes on, and not yet that it has ended",
                        startsAtReturn == 1 && endsAtReturn == 0);

                // Close waits for the copy that goes on after Drop.
                main.Close();
                Check(label + ": closing the window waits for the copy still running", !closed);
            }

            await ArchiveWaitAsync(() => !shell.Toast.IsBusy && shell.Toast.Message.Length > 0
                && (asyncSource is null || asyncSource.Ends > 0) && (held || closed), 15000);
            Console.WriteLine($"        {label}: final toast={shell.Toast.Message}");
            Check(label + ": the file arrives whole", ArchiveBytesEqual(destination, payload));
            Check(label + ": the copy is reported done", shell.Toast.Message.StartsWith("Copied 1 item", StringComparison.Ordinal));
            if (!held) Check(label + ": the window closes once the copy has ended", closed);
            if (asyncSource is not null && !held)
                Check(label + ": the source that can wait is told once, after the file arrived, that the copy succeeded as a copy",
                    asyncSource.Starts == 1 && asyncSource.Ends == 1 && asyncSource.EndResult == 0
                    && asyncSource.EndEffects == (uint)DragDropEffects.Copy && asyncSource.DestinationAtEnd);
            if (asyncSource is not null && held)
                Check(label + ": a held source that can wait is never told the copy goes on", asyncSource.Starts == 0 && asyncSource.Ends == 0);
            Check(label + ": the source file is unchanged", ArchiveBytesEqual(sourceFile, payload));
            Check(label + ": the owned window was never shown", !main.IsVisible);
        }
        finally
        {
            NativeShellService.OperationItemExists = operationExistsBefore;
            shell.Tree.BeginNavigation();
            main.CloseFromCaller();
            await SettingsSettle();
            if (sourceThread is not null) Dispatcher.FromThread(sourceThread)?.InvokeShutdown();
        }
    }

    /// <summary>The Shell's own data object for <paramref name="file"/>, as Explorer offers one for a drag.</summary>
    private static object ExternalShellDataObject(string file)
    {
        var itemId = typeof(IExternalDropShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file, 0, ref itemId, out var item));
        var handler = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4"); // BHID_DataObject
        var dataId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
        Marshal.ThrowExceptionForHR(((IExternalDropShellItem)item).BindToHandler(0, ref handler, ref dataId, out var data));
        return data;
    }

    /// <summary>
    /// A source that can wait, living on a thread of its own as another
    /// program's would, and the proxy the window is handed for it.
    /// </summary>
    private static async Task<(ExternalAsyncSource Source, Thread Thread, object Proxy)> ExternalAsyncSourceAsync(string file, string destination)
    {
        var ready = new TaskCompletionSource<(ExternalAsyncSource, nint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var source = new ExternalAsyncSource((System.Runtime.InteropServices.ComTypes.IDataObject)ExternalShellDataObject(file), destination);
                var dataId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
                Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref dataId, source, out var stream));
                ready.SetResult((source, stream));
            }
            catch (Exception error)
            {
                ready.SetException(error);
                return;
            }
            Dispatcher.Run();
        }) { IsBackground = true, Name = "external drop source" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var (made, marshalled) = await ready.Task;
        var proxyId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
        Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshalled, ref proxyId, out var proxy));
        return (made, thread, proxy);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object item);

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] object unknown, out nint stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(nint stream, ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExternalDropShellItem
{
    [PreserveSig]
    int BindToHandler(nint bindContext, ref Guid handler, ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}

/// <summary>IDataObjectAsyncCapability, as a drag source implements it.</summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExternalDropAsyncCapability
{
    [PreserveSig] int SetAsyncMode(int doAsync);
    [PreserveSig] int GetAsyncMode(out int isAsync);
    [PreserveSig] int StartOperation(nint bindContext);
    [PreserveSig] int InOperation(out int inOperation);
    [PreserveSig] int EndOperation(int result, nint bindContext, uint effects);
}

/// <summary>
/// The Shell's data object for a file, offered by a source that has turned
/// asynchronous mode on and records what the drop target tells it.  It hides
/// the free-threaded marshaller, so another apartment reaches it only through
/// a real COM proxy, as a drop target in another process does.
/// </summary>
[ComVisible(true)]
public sealed class ExternalAsyncSource(System.Runtime.InteropServices.ComTypes.IDataObject inner, string destination)
    : System.Runtime.InteropServices.ComTypes.IDataObject, IExternalDropAsyncCapability, ICustomQueryInterface
{
    private static readonly Guid MarshalId = new("00000003-0000-0000-C000-000000000046");
    private static readonly Guid AgileId = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    public int Starts;
    public int Ends;
    public int EndResult = 1;
    public uint EndEffects;
    public bool DestinationAtEnd;

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        return iid == MarshalId || iid == AgileId ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
    }

    public int SetAsyncMode(int doAsync) => 0;
    public int GetAsyncMode(out int isAsync) { isAsync = 1; return 0; }
    public int StartOperation(nint bindContext) { Interlocked.Increment(ref Starts); return 0; }
    public int InOperation(out int inOperation) { inOperation = Starts > Ends ? 1 : 0; return 0; }
    public int EndOperation(int result, nint bindContext, uint effects)
    {
        EndResult = result;
        EndEffects = effects;
        DestinationAtEnd = File.Exists(destination);
        Interlocked.Increment(ref Ends);
        return 0;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium) => inner.GetData(ref format, out medium);
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
    public int QueryGetData(ref FORMATETC format) => inner.QueryGetData(ref format);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => inner.GetCanonicalFormatEtc(ref formatIn, out formatOut);
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => inner.SetData(ref formatIn, ref medium, release);
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection) => inner.DAdvise(ref format, advf, adviseSink, out connection);
    public void DUnadvise(int connection) => inner.DUnadvise(connection);
    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => inner.EnumDAdvise(out enumAdvise);
}
