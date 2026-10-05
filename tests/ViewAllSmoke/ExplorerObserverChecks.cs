using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task<int> ExplorerObserverReadOnlyProbe(string[] args)
    {
        var flag = Array.IndexOf(args, "--explorer-observer-probe");
        if (flag + 2 >= args.Length || !long.TryParse(args[flag + 1], out var hwnd) || hwnd <= 0
            || !uint.TryParse(args[flag + 2], out var process) || process == 0)
        {
            Console.Error.WriteLine("--explorer-observer-probe <exact-frame-HWND> <exact-process-PID>");
            return 2;
        }
        using var observer = new ExplorerWindowObserver(new(process, new nint(hwnd)));
        observer.Start(_ => { }); // Explicitly no handoff action.
        var snapshots = await observer.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(8));
        if (snapshots.Count != 1)
        {
            Console.Error.WriteLine("The exact window has no supported standalone filesystem view: " + observer.LastReadFailure);
            return 1;
        }
        var snapshot = snapshots[0];
        Console.WriteLine($"Frame={snapshot.RootHwnd}; Browser={snapshot.BrowserHwnd}; View={snapshot.ViewHwnd}; "
            + $"PID={snapshot.SourceProcessId}; TID={snapshot.SourceThreadId}; Generation={snapshot.Generation}");
        Console.WriteLine($"Folder={snapshot.FolderPath}; PIDL bytes={snapshot.FolderPidl.Length}");
        foreach (var path in snapshot.SelectedPaths) Console.WriteLine($"Selected={path}");
        Console.WriteLine($"Focused={snapshot.FocusedPath}");
        var valid = await observer.ValidateSnapshotAsync(snapshot).WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine($"Validated={valid}");
        return valid ? 0 : 1;
    }

    private static async Task ExplorerObserverChecks()
    {
        Section("Explorer transfer identity and exact test scope");
        var snapshot = new ShellFolderSnapshot(101, 102, 103, 104, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            105, @"C:\sample", new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(new[] { @"C:\sample\a.txt", @"C:\sample\b.txt" }),
            @"C:\sample\b.txt", 7);
        Check("a full unchanged snapshot preserves folder, PIDL, selection, focus and identity",
            snapshot.SameContentAndIdentity(snapshot with { Generation = 8 }));
        Check("a reused HWND from a later process lifetime cannot be transferred",
            !snapshot.SameContentAndIdentity(snapshot with { SourceProcessStartUtc = snapshot.SourceProcessStartUtc.AddSeconds(1) }));
        Check("another active tab is a different source even when the frame and path match",
            !snapshot.SameContentAndIdentity(snapshot with { BrowserHwnd = 202 }));
        Check("a new active view in the same tab invalidates the prepared transfer",
            !snapshot.SameContentAndIdentity(snapshot with { ViewHwnd = 203 }));
        Check("a selection change cannot close the original after stale preparation",
            !snapshot.SameContentAndIdentity(snapshot with { SelectedPaths = [@"C:\sample\a.txt"] }));
        Check("a focus change invalidates prepared state too",
            !snapshot.SameContentAndIdentity(snapshot with { FocusedPath = @"C:\sample\a.txt" }));
        Check("same-named namespace folders cannot be confused by path alone",
            !snapshot.SameContentAndIdentity(snapshot with { FolderPidl = new byte[] { 2, 0, 1, 0 } }));
        Check("selection in a different physical parent is rejected",
            !ExplorerWindowObserver.IsDirectChild(@"C:\sample", @"C:\outside\a.txt"));
        Check("a selected subdirectory is supported like a selected file",
            ExplorerWindowObserver.IsDirectChild(@"C:\sample", @"C:\sample\directory"));
        Check("UNC selection and drive-root selection retain their direct parent",
            ExplorerWindowObserver.IsDirectChild(@"\\server\share", @"\\server\share\file.txt")
            && ExplorerWindowObserver.IsDirectChild(@"C:\", @"C:\file.txt"));

        var scope = new ExplorerObserverScope(104, 101);
        Check("a test scope matches its exact process AND frame", scope.Matches(104, 101));
        Check("a shared Explorer process cannot broaden a test to other user windows", !scope.Matches(104, 999));
        Check("an HWND reused by another process cannot escape the test scope", !scope.Matches(999, 101));
        var invalidScope = false;
        try { using var invalid = new ExplorerWindowObserver(new(104, 0)); }
        catch (ArgumentException) { invalidScope = true; }
        Check("a process-only test scope is refused", invalidScope);

        // Handle 1 cannot be an owned Explorer frame in this process. This checks
        // the real observer lifecycle without reading any user's folder state or
        // creating, moving, selecting, hiding or closing a native window.
        var callbacks = 0;
        using var observer = new ExplorerWindowObserver(new(checked((uint)Environment.ProcessId), 1));
        observer.Start(_ => Interlocked.Increment(ref callbacks));
        var observed = await observer.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check("an exact unrelated scope reads no eligible Explorer windows", observed.Count == 0 && callbacks == 0);
        Check("an unobserved source is never approved for closing", !await observer.ValidateSnapshotAsync(snapshot));
        observer.Dispose();
        Check("checkbox-off disposal cancels pending close validation", !await observer.ValidateSnapshotAsync(snapshot));
    }
}
