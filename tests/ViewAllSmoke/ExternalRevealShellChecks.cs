using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string ExternalRevealShellChild = "ULTRAEXPLORER_EXTERNAL_REVEAL_SHELL_CHILD";
    private const string ExternalRevealShellClient = "ULTRAEXPLORER_EXTERNAL_REVEAL_SHELL_CLIENT";
    private sealed record ExternalRevealShellRequest(string Folder, string Target, long Window);

    // ShellWindows is real, but only the newly generated folder and our exact
    // cloaked HWND are registered. No folder verb or global COM class is written.
    private static async Task ExternalRevealShellChecks()
    {
        if (Environment.GetEnvironmentVariable(ExternalRevealShellClient) is { Length: > 0 } requestJson)
        {
            var request = JsonSerializer.Deserialize<ExternalRevealShellRequest>(requestJson)
                ?? throw new InvalidOperationException("Missing owned native reveal request.");
            if (Environment.GetEnvironmentVariable(ExternalRevealShellChild) is not { Length: > 0 } clientRoot
                || Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !Path.GetFullPath(request.Folder).StartsWith(Path.GetFullPath(clientRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native client refuses a folder outside its generated fixture.");
            RunOnSta("owned native reveal client", () =>
            {
                var result = ExternalRevealShellNativeSelect(request.Folder, request.Target, (nint)request.Window);
                Check("SHOpenFolderAndSelectItems returns success: 0x" + result.ToString("X8"), result >= 0);
                return Task.CompletedTask;
            });
            return;
        }
        if (Environment.GetEnvironmentVariable(ExternalRevealShellChild) is { Length: > 0 } owned)
        {
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
                || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory)
                || !Path.GetFullPath(AppPaths.StateDirectory).StartsWith(Path.GetFullPath(owned) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native reveal checks require their own child and isolated state.");
            RunOnSta("native external reveal", () => ExternalRevealShellOnStaAsync(owned));
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerExternalRevealShell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--only");
        start.ArgumentList.Add(nameof(ExternalRevealShellChecks));
        start.Environment[ExternalRevealShellChild] = root;
        start.Environment[AppPaths.StateDirectoryVariable] = Path.Combine(root, "state");
        start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
            foreach (var line in (await output).Split('\n'))
                if (line.TrimEnd() is { Length: > 0 } printed) Console.WriteLine("  | " + printed);
            if (await errors is { Length: > 0 } error) Console.WriteLine(error);
            Check("native external reveal passes through the real Shell API in its isolated process", child.ExitCode == 0);
        }
        catch (TimeoutException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Console.WriteLine(await output);
            Console.WriteLine(await errors);
            Check("native external reveal finishes within four minutes", false);
        }
        finally { TryDelete(root); }
    }

    private static async Task ExternalRevealShellOnStaAsync(string root)
    {
        Section("native Show in folder: fresh view, retained view and tab host in dense folders");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        Directory.CreateDirectory(AppPaths.StateDirectory);
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
        var folder = Path.Combine(root, "fresh native view Ж");
        var tabFolder = Path.Combine(root, "native tab folder");
        var otherFolder = Path.Combine(root, "unrelated tab");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(tabFolder);
        Directory.CreateDirectory(otherFolder);
        for (var index = 0; index < 1200; index++)
            File.WriteAllText(Path.Combine(folder, $"file-{index:0000}.txt"), "native reveal fixture");
        for (var index = 0; index < 650; index++)
            File.WriteAllText(Path.Combine(tabFolder, $"tab-file-{index:0000}.txt"), "native tab fixture");
        var first = Path.Combine(folder, "file-1099.txt");
        var second = Path.Combine(folder, "file-0007.txt");
        var tabTarget = Path.Combine(tabFolder, "tab-file-0601.txt");
        MainWindow? main = null;
        MainWindow? tabs = null;
        using var activation = ActivationGuard.GuardWindowsCreated();
        try
        {
            main = ExternalRevealShellWindow();
            main.Show();
            FolderCommandLine.TryOpenFolder(folder, out var open, out _);
            Check("the new owned view finishes its initial folder navigation without a file selected",
                await main.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(60))
                && main.CurrentFolderSelection.Count == 0);
            var handle = new WindowInteropHelper(main).Handle;
            var callbacks = 0;
            using (var registration = new FolderShellViewRegistration(handle, main.Dispatcher, invocation =>
            {
                callbacks++;
                return main.ApplyFolderInvocationAsync(invocation);
            }))
            {
                ExternalRevealShellRequire(registration.Navigate(folder), "the fresh view registers with real Windows Shell");
                await ExternalRevealShellCaseAsync("fresh", registration, handle, main, first);
                Check("the native API reached the actual new window exactly once", callbacks == 1);
                await ExternalRevealShellCaseAsync("warm", registration, handle, main, second);
                Check("a second native reveal reuses the view and replaces the earlier exact selection", callbacks == 2
                    && main.CurrentFolderSelection.SequenceEqual([second], StringComparer.OrdinalIgnoreCase));
            }
            Check("the fresh view's native registration is revoked when its scope ends", ExternalRevealShellFind(folder) != unchecked((int)handle));

            FolderCommandLine.TryOpenFolder(otherFolder, out var tabOpen, out _);
            tabs = ExternalRevealShellWindow();
            var other = tabs.EnableFolderTabs(tabOpen);
            ((MainViewModel)other.DataContext).SuppressShellWrites = true;
            tabs.Show();
            Check("the unrelated retained tab finishes initialization", await other.ApplyFolderInvocationAsync(tabOpen).WaitAsync(TimeSpan.FromSeconds(60)));
            var targetContext = tabs.NewExplorerTab(tabFolder);
            ((MainViewModel)targetContext.DataContext).SuppressShellWrites = true;
            ExternalRevealShellRequire(await ExternalRevealShellWaitAsync(() => targetContext.IsFolderWindowReady
                && !targetContext.IsFolderInvocationPending), "the second tab completes its real initial navigation");
            var previousCamera = other.ActivePane.Canvas.CaptureCamera();
            var previousSelection = other.CurrentFolderSelection.ToArray();
            var tabHandle = new WindowInteropHelper(tabs).Handle;
            var tabCallbacks = 0;
            using (var registration = new FolderShellViewRegistration(tabHandle, tabs.Dispatcher, invocation =>
            {
                tabCallbacks++;
                return tabs.ApplyFolderInvocationAsync(invocation);
            }))
            {
                ExternalRevealShellRequire(registration.Navigate(tabFolder), "the active retained tab registers below its actual host HWND");
                await ExternalRevealShellCaseAsync("tab host", registration, tabHandle, targetContext, tabTarget);
                Check("the native host callback selects the visible tab without changing the retained tab", tabCallbacks == 1
                    && ReferenceEquals(tabs.ActiveTabContext, targetContext) && tabs.TabCount == 2
                    && other.ActivePane.Canvas.CaptureCamera() == previousCamera
                    && other.CurrentFolderSelection.SequenceEqual(previousSelection, StringComparer.OrdinalIgnoreCase));
            }
            Check("the tab host's native registration is revoked when its scope ends", ExternalRevealShellFind(tabFolder) != unchecked((int)tabHandle));
            Check("both owned host windows remain cloaked, guarded and outside the physical foreground",
                DialogNative.IsCloaked(handle) && DialogNative.IsCloaked(tabHandle)
                && ActivationGuard.IsGuarded(handle) && ActivationGuard.IsGuarded(tabHandle)
                && ExternalRevealShellGetForegroundWindow() != handle && ExternalRevealShellGetForegroundWindow() != tabHandle);
        }
        finally
        {
            tabs?.Close();
            main?.Close();
            await MainWindow.WhenClosingWindowsClosedAsync(application, TimeSpan.FromSeconds(10));
        }
    }

    private static MainWindow ExternalRevealShellWindow()
    {
        var window = new MainWindow { Width = 1400, Height = 900, ShowActivated = false, ShowInTaskbar = false };
        ((MainViewModel)window.DataContext).SuppressShellWrites = true;
        window.PrepareAsNativeProxy(handle =>
        {
            DialogNative.SetNoActivate(handle, true);
            ExternalRevealShellRequire(DialogNative.CloakOwn(handle, true), "the owned native reveal window is cloaked before presentation");
        });
        return window;
    }

    private static async Task ExternalRevealShellCaseAsync(string name, FolderShellViewRegistration registration,
        nint handle, MainWindow context, string target)
    {
        var previous = registration.LastSelection;
        var folder = Path.GetDirectoryName(target)!;
        // The API can otherwise open Explorer. Prove its exact registration
        // before starting it, then check it again on the calling STA. A random
        // private folder cannot resolve to another user's existing view.
        ExternalRevealShellRequire(ExternalRevealShellFind(folder) == unchecked((int)handle), name + ": native lookup resolves only the exact owned HWND");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--only");
        start.ArgumentList.Add(nameof(ExternalRevealShellChecks));
        start.Environment[ExternalRevealShellClient] = JsonSerializer.Serialize(new ExternalRevealShellRequest(folder, target, handle.ToInt64()));
        using (var nativeClient = Process.Start(start)!)
        {
            var output = nativeClient.StandardOutput.ReadToEndAsync();
            var errors = nativeClient.StandardError.ReadToEndAsync();
            try { await nativeClient.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException)
            {
                // End only our known client before the registered view can be
                // revoked. A native call still running must never fall through
                // into opening Explorer after its receiving HWND is gone.
                if (!nativeClient.HasExited) nativeClient.Kill(entireProcessTree: true);
                await nativeClient.WaitForExitAsync();
                throw;
            }
            if (nativeClient.ExitCode != 0) Console.WriteLine(await output + await errors);
            ExternalRevealShellRequire(nativeClient.ExitCode == 0, name + ": real SHOpenFolderAndSelectItems succeeds in its owned STA client");
        }
        ExternalRevealShellRequire(await ExternalRevealShellWaitAsync(() => registration.LastSelection is not null
            && !ReferenceEquals(registration.LastSelection, previous)), name + ": the native callback reaches the real window navigation");
        var applied = await registration.LastSelection!.WaitAsync(TimeSpan.FromSeconds(60));
        ExternalRevealShellRequire(applied, name + ": the actual native selection completes with a ready view");
        await context.Dispatcher.InvokeAsync(() => context.ActivePane.Canvas.UpdateLayout(), DispatcherPriority.ContextIdle);
        var model = (MainViewModel)context.DataContext;
        var canvas = context.ActivePane.Canvas;
        Check(name + ": exactly the requested file is selected in the shared state and the actual canvas",
            context.CurrentFolderSelection.SequenceEqual([target], StringComparer.OrdinalIgnoreCase)
            && model.Tree.Selection.Focus == target && model.Tree.Selection.FileCount == 1
            && canvas.SelectedCount == 1 && canvas.SelectionState.Items().Select(item => item.Path).SequenceEqual([target], StringComparer.OrdinalIgnoreCase));
        var rect = ExternalRevealShellFileRect(context, target);
        Check(name + ": the selected file card is centred, fully visible and readable among its neighbours (" + rect + ")",
            rect is { Width: >= 280 and <= 450, Height: >= 35 and <= 110 } card
            && new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight).Contains(card)
            && Math.Abs(card.X + card.Width / 2 - canvas.ActualWidth / 2) < 1
            && Math.Abs(card.Y + card.Height / 2 - canvas.ActualHeight / 2) < 1);
        Check(name + ": the containing folder remains in the address and the real native window remains cloaked",
            string.Equals(context.CurrentFolderPath, folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Address.CurrentPath, folder, StringComparison.OrdinalIgnoreCase)
            && DialogNative.IsCloaked(handle) && ExternalRevealShellGetForegroundWindow() != handle);
    }

    private static Rect? ExternalRevealShellFileRect(MainWindow context, string path)
    {
        var tree = context.ActivePane.Tree;
        if (tree.Find(Path.GetDirectoryName(path)!) is not { } folder) return null;
        tree.EnsureLayout(folder);
        var index = tree.FindFileIndex(folder, Path.GetFileName(path));
        if (index < 0 || index >= folder.FileGrid.Count || context.ActivePane.Canvas.ScreenRectOf(folder) is not { } cell) return null;
        var grid = folder.FileGrid;
        var (x, y) = grid.Origin(index);
        return new Rect(cell.X + x * cell.Width, cell.Y + y * cell.Width, grid.TileWidth * cell.Width, grid.TileHeight * cell.Width);
    }

    private static async Task<bool> ExternalRevealShellWaitAsync(Func<bool> ready)
    {
        var until = Environment.TickCount64 + 60000;
        while (!ready() && Environment.TickCount64 < until) await Task.Delay(20);
        return ready();
    }

    private static void ExternalRevealShellRequire(bool passed, string description)
    {
        Check(description, passed);
        if (!passed) throw new InvalidOperationException(description);
    }

    private static int ExternalRevealShellNativeSelect(string folder, string target, nint expected)
    {
        if (ExternalRevealShellFind(folder) != unchecked((int)expected))
            throw new InvalidOperationException("Refused native API: the generated folder no longer resolves to its exact owned window.");
        nint parent = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(folder, 0, out parent, 0, out _));
            Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(target, 0, out item, 0, out _));
            return ExternalRevealShellOpenFolderAndSelectItems(parent, 1, [ExternalRevealShellFindLastId(item)], 0);
        }
        finally
        {
            if (parent != 0) Marshal.FreeCoTaskMem(parent);
            if (item != 0) Marshal.FreeCoTaskMem(item);
        }
    }

    private static int ExternalRevealShellFind(string folder)
    {
        nint pidl = 0;
        IFolderShellWindows? windows = null;
        object? dispatch = null;
        try
        {
            Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(folder, 0, out pidl, 0, out _));
            windows = (IFolderShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(FolderShellNative.ShellWindows, true)!)!;
            object location = FolderShellNative.PidlVariant(pidl);
            object? empty = null;
            var result = windows.FindWindowSW(ref location, ref empty, 1, out var handle, 1, out dispatch);
            return result == 0 ? handle : 0;
        }
        finally
        {
            if (dispatch is not null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern nint ExternalRevealShellFindLastId(nint pidl);
    [DllImport("shell32.dll", EntryPoint = "SHOpenFolderAndSelectItems")]
    private static extern int ExternalRevealShellOpenFolderAndSelectItems(nint folder, uint count,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] children, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint ExternalRevealShellGetForegroundWindow();
}
