using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using UltraExplorer.Services;
using static UltraExplorer.Services.ExplorerWindowInterop;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] arguments)
    {
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(Path.GetTempPath(), "UltraExplorerNativeNavigation-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        try
        {
            if (arguments.Contains("--read-only-explorer-probe")) return ReadOnlyExplorerProbe();
            CheckEligibility(); CheckContexts(); CheckParentPolicy(); CheckLifetime(); CheckOwnedMarker();
            Console.WriteLine($"PASS: {_checks} policy, cancellation and owned message-window assertions. No user Explorer, focus or registry changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int ReadOnlyExplorerProbe()
    {
        // Only SDK queries: no marker, BrowseObject, navigation, input or focus.
        object? shell = null, windows = null;
        var rows = 0;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            windows = ((dynamic)shell!).Windows();
            var count = Math.Min(Convert.ToInt32(((dynamic)windows).Count), 512);
            for (var index = 0; index < count; index++)
            {
                object? dispatch = null, browserObject = null;
                ShellView? view = null;
                try
                {
                    dispatch = ((dynamic)windows).Item(index);
                    if (dispatch is not IFolderWebBrowserApp webBrowser || webBrowser.get_HWND(out var frame) < 0) continue;
                    var root = GetAncestor(frame, 2);
                    if (ClassName(root) is not ("CabinetWClass" or "ExploreWClass")) continue;
                    GetWindowThreadProcessId(root, out var processId);
                    using var process = Process.GetProcessById(checked((int)processId));
                    if (!string.Equals(process.MainModule?.FileName,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                    if (((ServiceProvider)dispatch!).QueryService(TopLevelBrowser, ShellBrowserId, out browserObject) < 0) continue;
                    var browser = (ShellBrowser)browserObject;
                    if (browser.GetWindow(out var browserWindow) < 0 || browser.QueryActiveShellView(out view) < 0
                        || view.GetWindow(out var viewWindow) < 0) continue;
                    nint tab = 0;
                    for (var at = viewWindow; at != 0 && at != root; at = ProbeGetParent(at))
                        if (ClassName(at) == "ShellTabWindowClass") { tab = at; break; }
                    var tabs = 0;
                    EnumChildWindows(root, (window, _) => { if (ClassName(window) == "ShellTabWindowClass") tabs++; return true; }, 0);
                    Console.WriteLine($"pid={processId}; root={root}; browser={browserWindow}; browserVisible={IsWindowVisible(browserWindow)}; "
                        + $"activeView={viewWindow}; viewVisible={IsWindowVisible(viewWindow)}; tab={tab}; tabVisible={(tab != 0 && IsWindowVisible(tab))}; tabHosts={tabs}");
                    rows++;
                }
                finally { Release(view); Release(browserObject); Release(dispatch); }
            }
            Console.WriteLine($"Read-only Shell browser rows: {rows}. No folder paths, window titles or modifications.");
            return 0;
        }
        finally { Release(windows); Release(shell); }
    }

    [DllImport("user32.dll", EntryPoint = "GetParent")]
    private static extern nint ProbeGetParent(nint window);

    private static void CheckEligibility()
    {
        var request = new FolderInvocation(FolderInvocationKind.OpenFolder, @"C:\parent\child", [], OriginIsShell: true);
        string[] arguments = ["--shell-request", "--open-folder", request.FolderPath];
        Assert(NativeExplorerNavigation.IsEligible(request, arguments), "a shell-origin ordinary folder is eligible");
        Assert(!NativeExplorerNavigation.IsEligible(request with { OriginIsShell = false }, arguments), "external/manual command keeps UltraExplorer route");
        Assert(!NativeExplorerNavigation.IsEligible(request with { Kind = FolderInvocationKind.Reveal }, arguments), "reveal never navigates an original Explorer session");
        Assert(!NativeExplorerNavigation.IsEligible(request with { DestinationId = Guid.NewGuid() }, arguments), "explicit destination remains separate");
        Assert(!NativeExplorerNavigation.IsEligible(request with { SelectedPaths = [@"C:\parent\child\file"] }, arguments), "selection-bearing requests remain separate");
        Assert(!NativeExplorerNavigation.IsEligible(request, ["--shell-request", "--HOME"]), "legacy Home is excluded case-insensitively");
        Assert(!NativeExplorerNavigation.TryHandle(request with { OriginIsShell = false }, arguments), "manual Win+E route exits before any Shell lookup");
    }

    private static void CheckContexts()
    {
        var started = DateTime.UtcNow.AddMinutes(-1);
        var context = new NativeExplorerNavigationIdentity(100, 200, started, 300, "CabinetWClass",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        Assert(NativeExplorerNavigation.IsExplorerContext(200, DateTime.UtcNow, context), "exact parent Explorer foreground frame qualifies");
        Assert(!NativeExplorerNavigation.IsExplorerContext(201, DateTime.UtcNow, context), "another foreground Explorer process cannot claim the launch");
        Assert(!NativeExplorerNavigation.IsExplorerContext(200, started.AddSeconds(-1), context), "a recycled parent PID newer than the caller is refused");
        foreach (var name in new[] { "Progman", "WorkerW", "#32770", "ConsoleWindowClass" })
            Assert(!NativeExplorerNavigation.IsExplorerContext(200, DateTime.UtcNow, context with { ClassName = name }), "desktop/dialog/external foreground remains on the normal route");
        Assert(!NativeExplorerNavigation.IsExplorerContext(200, DateTime.UtcNow, context with { Executable = @"C:\other\explorer.exe" }), "Explorer filename alone is insufficient");
        Assert(NativeExplorerNavigation.SameIdentity(context, context), "exact source lifetime remains valid");
        foreach (var changed in new[] { context with { Window = 101 }, context with { Process = 201 }, context with { Thread = 301 },
            context with { StartedUtc = started.AddSeconds(1) }, context with { ClassName = "Progman" }, context with { Executable = @"C:\other\explorer.exe" } })
            Assert(!NativeExplorerNavigation.SameIdentity(context, changed), "changed HWND/process/thread/lifetime/class/image refuses navigation");
        Assert(!NativeExplorerNavigation.SameIdentity(context, null), "lost foreground is refused");
    }

    private static void CheckParentPolicy()
    {
        Assert(NativeExplorerNavigation.IsImmediateChild(true, null, @"C:\parent\child"), "SDK immediate PIDL parent is accepted");
        Assert(NativeExplorerNavigation.IsImmediateChild(false, @"C:\Users\Person\Downloads", @"C:\Users\Person\Downloads\Child"), "KnownFolder alias can use exact filesystem parent fallback");
        Assert(NativeExplorerNavigation.IsImmediateChild(false, @"C:\parent\.", @"c:\PARENT\child\."), "filesystem fallback canonicalizes case and dot components");
        Assert(NativeExplorerNavigation.IsImmediateChild(false, @"C:\", @"C:\child"), "drive root is a valid immediate filesystem parent");
        foreach (var target in new[] { @"C:\parent", @"C:\parent\child\grandchild", @"C:\parentish\child", @"D:\parent\child", @"relative\child" })
            Assert(!NativeExplorerNavigation.IsImmediateChild(false, @"C:\parent", target), "same folder/grandchild/prefix/sibling drive/relative path cannot claim native navigation");
        Assert(!NativeExplorerNavigation.IsImmediateChild(false, null, @"C:\parent\child"), "non-filesystem alias needs positive SDK PIDL ancestry");
    }

    private static void CheckLifetime()
    {
        var order = new List<string>();
        var attempt = new NativeExplorerNavigationAttempt();
        Assert(attempt.Commit(() => { order.Add("validate"); return true; }, () => { order.Add("mark"); return true; },
            () => { order.Add("browse"); return 0; }), "verified navigation is handled");
        Assert(order.SequenceEqual(["validate", "mark", "validate", "browse"]) && attempt.Committed, "exact source validation and marker precede the only navigation");
        var canceled = new NativeExplorerNavigationAttempt(); canceled.Cancel();
        Assert(!canceled.Commit(() => true, () => throw new Exception("must not mark"), () => throw new Exception("must not navigate")), "late canceled work has no native side effect");
        var changed = new NativeExplorerNavigationAttempt(); var validates = 0; var browses = 0;
        Assert(changed.Commit(() => ++validates == 1, () => true, () => { browses++; return 0; }) && browses == 0,
            "focus/tab/folder change after marker keeps original native without navigating or UE fallback");
        var missing = new NativeExplorerNavigationAttempt();
        Assert(missing.Commit(() => false, () => throw new Exception("must not mark"), () => throw new Exception("must not navigate")),
            "confirmed native intent whose final source check is stale has no marker/navigation or UE fallback");
        var markFailure = new NativeExplorerNavigationAttempt();
        Assert(markFailure.Commit(() => true, () => false, () => throw new Exception("must not navigate")), "marker rejection keeps native intent without UE fallback");
        var browseFailure = new NativeExplorerNavigationAttempt(); var failures = 0;
        Assert(browseFailure.Commit(() => true, () => true, () => unchecked((int)0x80004005), _ => failures++) && failures == 1,
            "failed SDK BrowseObject reports one failure and cannot fall through to UE");
        var exception = new NativeExplorerNavigationAttempt();
        Assert(exception.Commit(() => true, () => true, () => throw new InvalidOperationException("owned fixture failure")), "COM exception after native claim cannot fall through to UE");
        var race = new NativeExplorerNavigationAttempt(); var raceBrowses = 0;
        Assert(race.Commit(() => { race.Cancel(); return true; }, () => true, () => { raceBrowses++; return 0; }) && raceBrowses == 1,
            "deadline during final validation cannot also start an ordinary route after native intent already won");
        attempt.Cancel();
        Assert(attempt.Committed && !attempt.Cancelled, "deadline after native commit does not launch a duplicate UE window");
    }

    private static void CheckOwnedMarker()
    {
        using var process = Process.GetCurrentProcess();
        var started = process.StartTime.ToUniversalTime();
        var parameters = new HwndSourceParameters("UltraExplorer owned native marker fixture") { ParentWindow = -3, WindowStyle = 0, Width = 1, Height = 1 };
        var owned = new HwndSource(parameters); var window = owned.Handle;
        Assert(!NativeExplorerSessionMarker.IsMarked(window, checked((uint)process.Id), started), "new owned message-only HWND has no marker");
        Assert(NativeExplorerSessionMarker.Mark(window, checked((uint)process.Id), started), "exact owned HWND/process lifetime can be marked without focus");
        Assert(NativeExplorerSessionMarker.IsMarked(window, checked((uint)process.Id), started), "marker is visible for exact owned lifetime");
        Assert(!NativeExplorerSessionMarker.IsMarked(window, checked((uint)process.Id), started.AddSeconds(1)), "recycled process lifetime cannot inherit marker");
        Assert(!NativeExplorerSessionMarker.Mark(window, checked((uint)process.Id + 1), started), "wrong process cannot mark the window");
        owned.Dispose();
        Assert(!NativeExplorerSessionMarker.IsMarked(window, checked((uint)process.Id), started), "destroyed HWND loses its native session marker");
        using var next = new HwndSource(parameters);
        Assert(!NativeExplorerSessionMarker.IsMarked(next.Handle, checked((uint)process.Id), started), "a subsequent HWND does not inherit prior window state");
    }

    private static void Assert(bool condition, string description)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(description);
    }
}
