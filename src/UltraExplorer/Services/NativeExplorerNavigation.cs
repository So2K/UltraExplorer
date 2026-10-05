using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UltraExplorer.Picker.Integration;
using static UltraExplorer.Services.ExplorerWindowInterop;

[assembly: InternalsVisibleTo("NativeExplorerNavigationSmoke")]

namespace UltraExplorer.Services;

internal sealed record NativeExplorerNavigationIdentity(nint Window, uint Process, DateTime StartedUtc,
    uint Thread, string ClassName, string Executable);

/// <summary>Preserves an immediate child-folder action originating inside the
/// exact foreground Explorer frame. Desktop/external/manual/Home requests keep
/// their ordinary route. This uses documented out-of-process Shell interfaces.</summary>
// Parent-process/immediate-PIDL/BrowseObject algorithm reference (Files, MIT):
// https://github.com/files-community/Files/blob/0e3c17ca44d143fb656be27eacf2b25c22a62043/src/Files.App.Launcher/FilesLauncher.cpp
internal static class NativeExplorerNavigation
{
    private const uint SameBrowserAbsolute = 0x0001; // SBSP_SAMEBROWSER | SBSP_ABSOLUTE (0).
    private const uint RequiredAttributes = 0x60000000; // SFGAO_FOLDER | SFGAO_FILESYSTEM.
    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    internal static bool TryHandle(FolderInvocation invocation, IReadOnlyList<string> arguments)
    {
        if (!DialogStartup.IsAllowed || !IsEligible(invocation, arguments)) return false;
        var context = CaptureCaller();
        if (context is null) return false;
        var attempt = new NativeExplorerNavigationAttempt();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completed.TrySetResult(Navigate(invocation.FolderPath, context, attempt)); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (attempt.Committed) DialogIntegrationStore.Log("Native Explorer navigation could not complete", error);
                completed.TrySetResult(attempt.Committed);
            }
        }) { IsBackground = true, Name = "UltraExplorer native navigation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (completed.Task.Wait(TimeSpan.FromMilliseconds(1500))) return completed.Task.Result;
        // A timed-out lookup cannot later mark/navigate a window after the
        // normal route starts. Already committed native intent never falls
        // through to an UltraExplorer window, even if its COM call is slow.
        attempt.Cancel();
        return attempt.Committed;
    }

    internal static bool IsEligible(FolderInvocation invocation, IReadOnlyList<string> arguments) =>
        invocation.OriginIsShell && invocation.Kind == FolderInvocationKind.OpenFolder
        && invocation.DestinationId == Guid.Empty && invocation.SelectedPaths is { Count: 0 }
        // SHParseDisplayName trims literal name endings. Never navigate a
        // kept-native Explorer frame to that different folder by accident.
        && !Models.ViewAllPath.EndsANameInDotOrSpace(invocation.FolderPath)
        && !arguments.Any(argument => argument.Equals(FolderCommandLine.HomeSwitch, StringComparison.OrdinalIgnoreCase));

    internal static bool IsExplorerContext(uint parentProcess, DateTime callerStartedUtc, NativeExplorerNavigationIdentity context) =>
        context.Window != 0 && context.Process != 0 && context.Thread != 0 && context.Process == parentProcess
        && context.StartedUtc <= callerStartedUtc
        && context.ClassName is "CabinetWClass" or "ExploreWClass"
        && string.Equals(context.Executable, ExplorerPath, StringComparison.OrdinalIgnoreCase);

    internal static bool SameIdentity(NativeExplorerNavigationIdentity expected, NativeExplorerNavigationIdentity? current) =>
        current is not null && expected.Window == current.Window && expected.Process == current.Process
        && expected.StartedUtc == current.StartedUtc && expected.Thread == current.Thread
        && expected.ClassName == current.ClassName
        && string.Equals(expected.Executable, current.Executable, StringComparison.OrdinalIgnoreCase);

    internal static bool IsImmediateChild(bool pidlImmediateParent, string? currentFolder, string targetFolder)
    {
        if (pidlImmediateParent) return true;
        // Known-folder aliases can have different absolute PIDL ancestry while
        // referring to the same filesystem directory (for example Downloads).
        if (string.IsNullOrWhiteSpace(currentFolder) || !Path.IsPathFullyQualified(currentFolder)
            || !Path.IsPathFullyQualified(targetFolder)) return false;
        try
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder)));
            return parent is not null && string.Equals(current, Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static NativeExplorerNavigationIdentity? CaptureCaller()
    {
        var context = ReadForeground();
        if (context is null) return null;
        using var ownProcess = Process.GetCurrentProcess();
        return IsExplorerContext(ParentProcess(), ownProcess.StartTime.ToUniversalTime(), context) ? context : null;
    }

    private static NativeExplorerNavigationIdentity? ReadForeground()
    {
        var window = GetAncestor(GetForegroundWindow(), 2);
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || !IsWindowEnabled(window)) return null;
        var name = ClassName(window);
        if (name is not ("CabinetWClass" or "ExploreWClass")) return null;
        var thread = GetWindowThreadProcessId(window, out var processId);
        if (thread == 0 || processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var executable = process.MainModule?.FileName;
            if (!string.Equals(executable, ExplorerPath, StringComparison.OrdinalIgnoreCase)) return null;
            var started = process.StartTime.ToUniversalTime();
            return !process.HasExited && IsWindow(window) && GetWindowThreadProcessId(window, out var currentProcess) == thread
                && currentProcess == processId && GetAncestor(GetForegroundWindow(), 2) == window
                ? new(window, processId, started, thread, name, executable!) : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }

    private static bool Navigate(string targetPath, NativeExplorerNavigationIdentity context, NativeExplorerNavigationAttempt attempt)
    {
        object? shell = null, windows = null;
        nint targetPidl = 0;
        try
        {
            if (attempt.Cancelled || !SameIdentity(context, ReadForeground())) return false;
            if (FolderShellNative.SHParseDisplayName(targetPath, 0, out targetPidl, RequiredAttributes, out var attributes) < 0
                || targetPidl == 0 || (attributes & RequiredAttributes) != RequiredAttributes || attempt.Cancelled) return false;
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            windows = ((dynamic)shell!).Windows();
            var count = Math.Min(Convert.ToInt32(((dynamic)windows).Count), 512);
            for (var index = 0; index < count && !attempt.Cancelled; index++)
            {
                object? dispatch = null, browserObject = null, folderObject = null;
                ShellView? view = null;
                nint sourcePidl = 0;
                try
                {
                    dispatch = ((dynamic)windows).Item(index);
                    if (dispatch is not IFolderWebBrowserApp webBrowser || webBrowser.get_HWND(out var frame) < 0
                        || GetAncestor(frame, 2) != context.Window || webBrowser.get_Busy(out var busy) < 0 || busy) continue;
                    if (((ServiceProvider)dispatch!).QueryService(TopLevelBrowser, ShellBrowserId, out browserObject) < 0) continue;
                    var browser = (ShellBrowser)browserObject;
                    if (browser.GetWindow(out var browserWindow) < 0 || !BelongsToFrame(browserWindow, context.Window)
                        || !IsWindowVisible(browserWindow) || browser.QueryActiveShellView(out view) < 0
                        || view.GetWindow(out var viewWindow) < 0 || !BelongsToFrame(viewWindow, context.Window)
                        || !VisibleTab(viewWindow, context.Window)) continue;
                    if (((FolderView)view).GetFolder(PersistFolderId, out folderObject) < 0
                        || ((PersistFolder2)folderObject).GetCurFolder(out sourcePidl) < 0 || sourcePidl == 0) continue;
                    var currentFolder = FolderShellNative.FileSystemPath(sourcePidl);
                    if (!IsImmediateChild(ILIsParent(sourcePidl, targetPidl, true), currentFolder, targetPath)) continue;
                    return attempt.Commit(
                        () => SameIdentity(context, ReadForeground()) && BrowserUnchanged(browser, context.Window, viewWindow, sourcePidl),
                        () => NativeExplorerSessionMarker.Mark(context.Window, context.Process, context.StartedUtc),
                        () => browser.BrowseObject(targetPidl, SameBrowserAbsolute),
                        error => DialogIntegrationStore.Log("The original Explorer window remains native: " + error));
                }
                catch (Exception error) when (!attempt.Committed && error is not OutOfMemoryException)
                {
                    // ShellWindows can also contain custom application COM
                    // registrations. One unsupported item must not hide the
                    // exact native Explorer browser later in the collection.
                }
                finally
                {
                    if (sourcePidl != 0) Marshal.FreeCoTaskMem(sourcePidl);
                    Release(folderObject); Release(view); Release(browserObject); Release(dispatch);
                }
            }
            return false;
        }
        finally
        {
            if (targetPidl != 0) Marshal.FreeCoTaskMem(targetPidl);
            Release(windows); Release(shell);
        }
    }

    private static bool BelongsToFrame(nint window, nint root) => window != 0 && IsWindow(window) && GetAncestor(window, 2) == root;
    private static bool VisibleTab(nint window, nint root)
    {
        for (var depth = 0; window != 0 && window != root && depth < 64; depth++, window = GetParent(window))
            if (ClassName(window) == "ShellTabWindowClass") return IsWindowVisible(window);
        return true; // Older Explorer has no tab host; the native view can be hidden.
    }

    private static bool BrowserUnchanged(ShellBrowser browser, nint root, nint originalView, nint originalPidl)
    {
        ShellView? currentView = null;
        object? currentFolder = null;
        nint currentPidl = 0;
        try
        {
            return browser.QueryActiveShellView(out currentView) >= 0 && currentView.GetWindow(out var window) >= 0
                && window == originalView && BelongsToFrame(window, root) && VisibleTab(window, root)
                && ((FolderView)currentView).GetFolder(PersistFolderId, out currentFolder) >= 0
                && ((PersistFolder2)currentFolder).GetCurFolder(out currentPidl) >= 0 && currentPidl != 0
                && ILIsEqual(originalPidl, currentPidl);
        }
        finally
        {
            if (currentPidl != 0) Marshal.FreeCoTaskMem(currentPidl);
            Release(currentFolder); Release(currentView);
        }
    }

    private static uint ParentProcess()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS.
        if (snapshot == -1) return 0;
        try
        {
            var entry = new ProcessEntry { Size = checked((uint)Marshal.SizeOf<ProcessEntry>()) };
            if (!Process32First(snapshot, ref entry)) return 0;
            do { if (entry.Process == Environment.ProcessId) return entry.Parent; } while (Process32Next(snapshot, ref entry));
            return 0;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Process; internal nuint Heap;
        internal uint Module, Threads, Parent; internal int Priority; internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ILIsParent(nint parent, nint child, [MarshalAs(UnmanagedType.Bool)] bool immediate);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ILIsEqual(nint first, nint second);
}

/// <summary>Cancellation wins before native intent is committed. Once claimed,
/// even a failed marker/navigation preserves the native frame without UE fallback.</summary>
internal sealed class NativeExplorerNavigationAttempt
{
    private int _state; // 0: looking up; 1: native intent claimed; 2: lookup canceled.
    internal bool Committed => Volatile.Read(ref _state) == 1;
    internal bool Cancelled => Volatile.Read(ref _state) == 2;
    internal void Cancel() => Interlocked.CompareExchange(ref _state, 2, 0);
    internal bool Commit(Func<bool> validate, Func<bool> mark, Func<int> browse, Action<string>? failure = null)
    {
        try
        {
            // The caller already proved native child-folder intent. Claim it
            // before final validation: a stale source must not open UE either.
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            if (!validate()) { failure?.Invoke("The confirmed native source is no longer current."); return true; }
            if (!mark()) { failure?.Invoke("The session marker was rejected."); return true; }
            if (!validate()) { failure?.Invoke("The source window, tab or folder changed."); return true; }
            var result = browse();
            if (result < 0) failure?.Invoke($"BrowseObject returned 0x{result:X8}.");
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Committed) failure?.Invoke(error.Message);
            return Committed;
        }
    }
}
