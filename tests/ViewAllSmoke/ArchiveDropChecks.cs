using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Nodify;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The real tree/nested Drop handlers receive a delayed-rendered data object.
/// The source owns its materialized files only until Drop returns, then removes
/// its entire staging directory. Everything runs in an owned child on an
/// inactive desktop: even native copy error/progress UI cannot reach the user.
/// </summary>
internal static partial class Program
{
    private const string ArchiveDropChildVariable = "ULTRAEXPLORER_ARCHIVE_DROP_CHILD";

    private static async Task ArchiveDropChecks()
    {
        if (Environment.GetEnvironmentVariable(ArchiveDropChildVariable) is { Length: > 0 } childRoot)
        {
            var output = new StreamWriter(Path.Combine(childRoot, "results.txt"), false, Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(output);
            RunOnSta("archive source lifetime", () => ArchiveDropOnStaAsync(childRoot));
            return;
        }

        Section("archive drops: actual handlers on an inactive owned desktop");
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerArchiveDrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerArchiveDrop-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var roleBefore = Environment.GetEnvironmentVariable(ArchiveDropChildVariable);
        var stateBefore = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var testBefore = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
        FixtureProcess process = default;
        try
        {
            Environment.SetEnvironmentVariable(ArchiveDropChildVariable, root);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
            var command = NativeShellService.BuildCommandLine([executable, "--only", nameof(ArchiveDropChecks)]);
            if (!CreateProcess(executable, new StringBuilder(command), 0, 0, false, 0x08000000,
                0, Path.GetDirectoryName(executable), ref startup, out process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Environment.SetEnvironmentVariable(ArchiveDropChildVariable, roleBefore);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, stateBefore);
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testBefore);
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            foreach (var line in File.ReadAllLines(Path.Combine(root, "results.txt")))
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line.Length > 0) Console.WriteLine(line);
            }
            Check("the owned archive drop child completes successfully", child.ExitCode == 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArchiveDropChildVariable, roleBefore);
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

    private static async Task ArchiveDropOnStaAsync(string root)
    {
        Check("archive drop fixture uses its own state and test-window mode",
            Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && ViewAllPath.Equals(AppPaths.StateDirectory, Path.Combine(root, "state")));
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var payload = ArchivePayload();
        foreach (var nested in new[] { true, false })
            foreach (var scenario in new[] { "fresh-path", "stable-path", "read-error", "close-during-copy" })
                await ArchiveDropRouteAsync(root, nested, payload, scenario);
    }

    private static async Task ArchiveDropRouteAsync(string root, bool nested, byte[] payload, string scenario)
    {
        var label = (nested ? "nested canvas" : "tree editor") + " / " + scenario;
        Section("archive drop lifetime: " + label);
        var owned = Path.Combine(root, nested ? "nested" : "tree", scenario);
        var target = Path.Combine(owned, "destination");
        Directory.CreateDirectory(target);
        var workspace = Path.Combine(owned, "workspace.json");
        await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", CanvasLayout = nested ? "Nested" : "Tree" });
        File.WriteAllText(workspace + ".marks.json", "{}");
        var archive = Path.Combine(owned, "source.rar");
        var archiveBytes = Encoding.UTF8.GetBytes("owned archive canary; never transferred or changed");
        File.WriteAllBytes(archive, archiveBytes);
        var source = new ArchiveDelayedData(owned, payload, scenario != "stable-path") { ThrowOnDropRead = scenario == "read-error" };
        var data = new DataObject(source);
        var main = new MainWindow(null, workspace);
        Application.Current.MainWindow = main;
        var shell = (MainViewModel)main.DataContext;
        var existsBefore = MainViewModel.ItemExists;
        try
        {
            // Ensure no operation HWND can be associated with the user's desktop.
            new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
            await shell.InitializeAsync(target);
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(owned, "Owned archive fixture", NestedFolderKind.Drive)]);
            shell.Layout = nested ? CanvasLayout.Nested : CanvasLayout.Tree;
            shell.Tree.PreferLightReveal = nested;
            if (!nested) await main.TreeEntryForChecks;
            var node = await shell.Tree.RevealPathAsync(target, focus: false);
            Check(label + ": the actual destination node is present", node is not null && ViewAllPath.Equals(node.FullPath, target));
            if (node is null) return;
            if (nested)
            {
                await main.FirstPane.FlyToAsync(target, gentle: false, animated: false);
                await main.FirstPane.Tree.LoadAsync(main.FirstPane.Tree.Find(target)!);
            }
            else
            {
                // Put the owned empty destination well clear of every other
                // graph node. This avoids a stale intermediate auto-layout
                // index obscuring the drop target in the baseline build.
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
                // Layout realizes the node's actual size. A normal public
                // move updates its spatial entry for that final size too.
                node.Location += new Vector(1, 1);
                // The fixture owns the layout. Register its final measured
                // node so a deferred baseline layout slice cannot make this
                // source-lifetime test depend on unrelated indexing timing.
                shell.Tree.SpatialIndex.AddOrUpdate(node);
                var graphPoint = new Point(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
                var localPoint = new Point((graphPoint.X - main.Editor.ViewportLocation.X) * main.Editor.ViewportZoom,
                    (graphPoint.Y - main.Editor.ViewportLocation.Y) * main.Editor.ViewportZoom);
                point = main.Editor.TranslatePoint(localPoint, host);
                var probe = ArchiveDragArgs(DragDrop.PreviewDragOverEvent, data, host, point);
                var actualGraphPoint = main.Editor.GetLocationInsideEditor(probe);
                var actualTarget = shell.Tree.FindNearestDropTarget(actualGraphPoint, 96 / Math.Max(main.Editor.ViewportZoom, 0.05));
                Console.WriteLine($"        tree coordinates: node={node.Bounds}, requested={graphPoint}, actual={actualGraphPoint}, visible={node.IsTreeVisible}, target={actualTarget?.FullPath}");
                Check(label + ": the real editor hit test resolves the intended destination", actualTarget is not null && ViewAllPath.Equals(actualTarget.FullPath, target));
                if (actualTarget is null || !ViewAllPath.Equals(actualTarget.FullPath, target)) return;
            }
            DragEventArgs Raise(RoutedEvent routed, IDataObject? offered = null)
            {
                var args = ArchiveDragArgs(routed, offered ?? data, host, point);
                dropView.RaiseEvent(args);
                return args;
            }
            var over = Raise(nested ? DragDrop.DragOverEvent : DragDrop.PreviewDragOverEvent);
            Check(label + ": copy-only source is offered Copy", over.Handled && over.Effects == DragDropEffects.Copy);
            Check(label + ": the provider's preview/committed path scenario is configured correctly",
                ViewAllPath.Equals(source.PreviewPath, source.FreshPath) == (scenario == "stable-path"));
            source.BeginDrop();
            // Delay only the existing worker-side validation seam. The UI
            // dispatcher remains free; this makes an early callback return
            // deterministic rather than depending on filesystem scheduling.
            var validationStarted = 0;
            var uiResponsive = false;
            var repeatRejected = false;
            var closeWasDeferred = false;
            var closed = false;
            main.Closed += (_, _) => closed = true;
            var repeated = new ArchiveDelayedData(Path.Combine(owned, "repeated"), payload, true, "repeat.zip");
            var repeatedData = new DataObject(repeated);
            MainViewModel.ItemExists = path =>
            {
                if (path.StartsWith(source.StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(repeated.StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (Interlocked.Exchange(ref validationStarted, 1) == 0)
                        main.Dispatcher.BeginInvoke(() =>
                        {
                            uiResponsive = true;
                            repeated.BeginDrop();
                            var again = Raise(nested ? DragDrop.DropEvent : DragDrop.PreviewDropEvent, repeatedData);
                            repeatRejected = again.Handled && again.Effects == DragDropEffects.None
                                && !File.Exists(Path.Combine(target, "repeat.zip"));
                            repeated.DropReturned();
                            if (scenario == "close-during-copy")
                            {
                                main.Close();
                                closeWasDeferred = !closed;
                            }
                        }, DispatcherPriority.Send);
                    Thread.Sleep(100);
                }
                return existsBefore(path);
            };
            var dropped = Raise(nested ? DragDrop.DropEvent : DragDrop.PreviewDropEvent);
            var destination = Path.Combine(target, Path.GetFileName(source.FreshPath));
            var atReturn = ArchiveBytesEqual(destination, payload);
            var sourceAtReturn = ArchiveBytesEqual(source.FreshPath, payload);
            Console.WriteLine($"        {label}: at Drop return targetExact={atReturn}, sourceExact={sourceAtReturn}, previewReads={source.PreviewReads}, dropReads={source.DropReads}, effect={dropped.Effects}, dispatcherRan={uiResponsive}");
            // The source cleanup happens immediately when the real handler's
            // synchronous return releases it, just as an archive drag does.
            source.DropReturned();
            var rejectedRead = scenario == "read-error";
            Check(label + ": destination bytes reflect successful copy or rejected final data read", rejectedRead ? !File.Exists(destination) : atReturn);
            Check(label + ": Copy leaves the rendered source unchanged until its source cleans up", rejectedRead || sourceAtReturn);
            Check(label + ": source was read fresh during Drop rather than relying on preview paths", source.DropReads > 0);
            Check(label + ": a completed Copy is reported only after success; failed reads report None",
                dropped.Handled && dropped.Effects == (rejectedRead ? DragDropEffects.None : DragDropEffects.Copy) && (rejectedRead || atReturn));
            if (!rejectedRead)
            {
                Check(label + ": dispatcher callbacks execute while the drop's worker validation is pending", uiResponsive);
                Check(label + ": a reentrant drop is rejected without starting a second copy", repeatRejected && !File.Exists(Path.Combine(target, "repeat.zip")));
            }
            else
                Check(label + ": a final IDataObject COM failure produces an error toast without escaping the routed event",
                    shell.Toast.Message.Contains("owned delayed data read failed", StringComparison.Ordinal));
            Check(label + ": source cleanup removes its entire staging directory", !Directory.Exists(source.StagingRoot));
            await ArchiveWaitAsync(() => !shell.Toast.IsBusy && shell.Toast.Message.Length > 0, 5000);
            Console.WriteLine($"        {label}: final toast={shell.Toast.Message}");
            Check(label + ": final destination remains correct after cleanup and dispatcher continuations",
                rejectedRead ? !File.Exists(destination) : ArchiveBytesEqual(destination, payload));
            if (scenario == "close-during-copy")
            {
                await ArchiveWaitAsync(() => closed, 5000);
                Check(label + ": normal close waits for the active transfer, then closes the window", closeWasDeferred && closed && atReturn);
            }
            Check(label + ": the archive itself is unchanged", ArchiveBytesEqual(archive, archiveBytes));
            Check(label + ": the owned window was never shown", !main.IsVisible);
        }
        finally
        {
            MainViewModel.ItemExists = existsBefore;
            shell.Tree.BeginNavigation();
            main.CloseFromCaller();
            await SettingsSettle();
        }
    }

    private static DragEventArgs ArchiveDragArgs(RoutedEvent routed, IDataObject data, DependencyObject relativeTo, Point point)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)
            ?? throw new MissingMethodException(nameof(DragEventArgs), ".ctor");
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, relativeTo, point]);
        args.RoutedEvent = routed;
        return args;
    }

    private static async Task ArchiveWaitAsync(Func<bool> finished, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!finished() && clock.ElapsedMilliseconds < milliseconds) await Task.Delay(10);
    }

    private static bool ArchiveBytesEqual(string path, byte[] bytes)
    {
        try { return File.ReadAllBytes(path).SequenceEqual(bytes); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static byte[] ArchivePayload()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = new StreamWriter(zip.CreateEntry("owned-payload.txt").Open(), Encoding.UTF8))
            entry.Write("archive lifetime payload: exact bytes must outlive the source drag");
        return output.ToArray();
    }

    private static void ArchiveDeleteOwned(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing archive fixture cleanup outside its owned directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private sealed class ArchiveDelayedData(string owned, byte[] payload, bool freshPaths, string filename = "inner.zip") : IDataObject
    {
        private readonly DataObject _other = new();
        private bool _dropping;
        public string StagingRoot { get; } = Path.Combine(owned, "source-staging");
        public string PreviewPath => Path.Combine(StagingRoot, "preview", filename);
        public string FreshPath => freshPaths ? Path.Combine(StagingRoot, "drop", filename) : PreviewPath;
        public bool ThrowOnDropRead { get; init; }
        public int PreviewReads { get; private set; }
        public int DropReads { get; private set; }
        public void BeginDrop() { _dropping = true; if (freshPaths) ArchiveDeleteOwned(Path.Combine(StagingRoot, "preview"), owned); }
        public void DropReturned() => ArchiveDeleteOwned(StagingRoot, owned);
        public object? GetData(string format, bool autoConvert)
        {
            if (format != DataFormats.FileDrop) return _other.GetData(format, autoConvert);
            var path = _dropping ? FreshPath : PreviewPath;
            if (_dropping) DropReads++; else PreviewReads++;
            if (_dropping && ThrowOnDropRead) throw new COMException("owned delayed data read failed", unchecked((int)0x80004005));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _dropping || !freshPaths ? payload : Encoding.UTF8.GetBytes("stale preview payload"));
            return new[] { path };
        }
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName!, true);
        public bool GetDataPresent(string format, bool autoConvert) => format == DataFormats.FileDrop || _other.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, true);
        public string[] GetFormats(bool autoConvert) => new[] { DataFormats.FileDrop }.Concat(_other.GetFormats(autoConvert)).Distinct().ToArray();
        public string[] GetFormats() => GetFormats(true);
        public void SetData(string format, object data, bool autoConvert) => _other.SetData(format, data, autoConvert);
        public void SetData(string format, object data) => _other.SetData(format, data);
        public void SetData(Type format, object data) => _other.SetData(format, data);
        public void SetData(object data) => _other.SetData(data);
    }
}
