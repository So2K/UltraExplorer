using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The review's issues with the folder list and the search (see
/// docs/REVIEW_2026-10-02.md), each replayed on its own, as it went wrong.
///
/// The lists are made with a read of their own, so what each read returns -
/// and when - is the check's to say; the folders are real where the list
/// asks the disk itself.
/// </summary>
internal static partial class Program
{
    private static async Task ListSearchReviewChecks()
    {
        Section("folder list and search: review fixes");
        RunOnSta("list held while the canvas goes elsewhere", ListHeldRetargetAsync);
        RunOnSta("list change during a dropped read", ListLiveAgainAsync);
        RunOnSta("list folder there but not readable", ListUnreadableFolderAsync);
        RunOnSta("list folder gone", ListGoneFolderAsync);
        RunOnSta("list filter past one read", ListTruncatedFilterAsync);
        RunOnSta("list rows fading under steady changes", ListFadeUnderChurnAsync);
        RunOnSta("search waiting for Everything", SearchWaitAfterFailureAsync);
        RunOnSta("search rows found again", SearchRowReuseAsync);
        RunOnSta("search probes off the window's thread", SearchProbeThreadAsync);
        SearchReplyWindowChecks();
        SearchQuotedFunctionChecks();
        await SearchWalkReviewChecksAsync();
    }

    // ---- the folder list -------------------------------------------------------------

    /// <summary>A list whose reads are <paramref name="read"/>'s, shown, with the canvas it would drive a no-op.</summary>
    private static FolderListViewModel ReviewList(ShellIconService icons, Func<string, Task<ViewAllDirectorySnapshot>> read) =>
        new((path, _, _) => read(path), (_, _) => Task.CompletedTask, _ => false, icons) { IsVisible = true };

    /// <summary>A listing of <paramref name="folder"/>: a name with a dot is a file, one without a folder.</summary>
    private static ViewAllDirectorySnapshot ReviewSnapshot(string folder, bool truncated, params string[] names) =>
        new([.. names.Select(name => new ViewAllEntryDescriptor(
            Path.Combine(folder, name),
            name,
            name.Contains('.') ? ViewAllEntryKind.File : ViewAllEntryKind.Folder,
            false,
            false,
            name.Contains('.') ? 1 : null,
            DateTime.UtcNow))], truncated, names.Length);

    private static ViewAllNodeViewModel ReviewNode(string path, bool isDirectory, ViewAllNodeViewModel? parent = null) =>
        new(new ViewAllEntryDescriptor(path, Path.GetFileName(path), isDirectory ? ViewAllEntryKind.Folder : ViewAllEntryKind.File,
            false, false, isDirectory ? null : 1, DateTime.UtcNow), parent is null ? 1 : parent.Depth + 1, parent);

    private static FolderChange ReviewChange(string path, ChangeKinds kinds = ChangeKinds.Structural) =>
        new(path, kinds, Stopwatch.GetTimestamp(), default, default);

    /// <summary>
    /// I056: a row being revealed holds the list in its folder - selecting
    /// that row's folder on the canvas must not take the list into it - but
    /// a reveal on a share that does not answer holds it for most of a
    /// minute, and what the canvas, the address bar or Back asked for
    /// meanwhile was dropped: the list stayed where it was once the hold
    /// ended.  The last thing asked for is applied then, unless it is the
    /// list's own doing: its own row, or the canvas getting late to a step
    /// the list took itself.
    /// </summary>
    private static async Task ListHeldRetargetAsync()
    {
        Section("folder list: a hold ends where the canvas went meanwhile");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListHold", Guid.NewGuid().ToString("N"));
        var here = Path.Combine(root, "here");
        var alpha = Path.Combine(here, "alpha");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(elsewhere);
        try
        {
            using var icons = new ShellIconService();
            var contents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [here] = ["alpha", "a.txt"],
                [alpha] = ["inner.txt"],
                [elsewhere] = ["far.txt"],
            };
            var list = ReviewList(icons, path => Task.FromResult(ReviewSnapshot(path, false, contents.GetValueOrDefault(path, []))));
            await list.NavigateAsync(here);
            var hereNode = ReviewNode(here, true);

            using (list.HoldFolder())
            {
                list.SetTarget(ReviewNode(alpha, true, hereNode));
            }

            Check("a folder row picked keeps the list out of it once the hold ends", ViewAllPath.Equals(list.FolderPath, here));

            var hold = list.HoldFolder();
            list.SetTarget(ReviewNode(elsewhere, true));
            Check("held, the list stays where it is while the canvas goes elsewhere", ViewAllPath.Equals(list.FolderPath, here));
            hold.Dispose();
            await LiveWait(() => !list.IsLoading, 2_000);
            Check($"and goes where the canvas went once the hold ends ({Path.GetFileName(list.FolderPath)})",
                ViewAllPath.Equals(list.FolderPath, elsewhere) && list.Items.Any(item => item.DisplayName == "far.txt"));

            await list.NavigateAsync(here);
            var outer = list.HoldFolder();
            var inner = list.HoldFolder();
            list.SetTarget(elsewhere, null);
            inner.Dispose();
            Check("a hold inside another still keeps the list where it is", ViewAllPath.Equals(list.FolderPath, here));
            outer.Dispose();
            Check("the last hold to end applies the folder asked for by path", ViewAllPath.Equals(list.FolderPath, elsewhere));

            // A reveal of a folder's items, as Explorer's "show in folder"
            // asks, points the list at the folder and then selects them under
            // a hold of its own: a folder among them, alone, is lit in that
            // folder once the hold around it all ends, not gone into.
            var alphaNode = ReviewNode(alpha, true, hereNode);
            outer = list.HoldFolder();
            list.SetTarget(here, alphaNode);
            inner = list.HoldFolder();
            list.SetTarget(alphaNode);
            inner.Dispose();
            outer.Dispose();
            await LiveWait(() => !list.IsLoading, 2_000);
            Check($"a folder revealed in its folder during a hold is lit there once the hold ends ({Path.GetFileName(list.FolderPath)}, {list.Selected?.DisplayName})",
                ViewAllPath.Equals(list.FolderPath, here) && list.Selected?.DisplayName == "alpha");

            // The same folder picked on the canvas, with no hold of its own,
            // is gone into, as it is with nothing holding the list.
            await list.NavigateAsync(elsewhere);
            hold = list.HoldFolder();
            list.SetTarget(here, alphaNode);
            list.SetTarget(alphaNode);
            hold.Dispose();
            await LiveWait(() => !list.IsLoading, 2_000);
            Check($"a folder picked on the canvas during a hold is gone into once it ends ({Path.GetFileName(list.FolderPath)})",
                ViewAllPath.Equals(list.FolderPath, alpha));

            await list.NavigateAsync(here);
            hold = list.HoldFolder();
            list.SetTarget(ReviewNode(elsewhere, true));
            list.SetTarget(ReviewNode(Path.Combine(here, "a.txt"), false, hereNode));
            hold.Dispose();
            Check("a row of the list picked after the canvas moved is the newer wish: the list stays",
                ViewAllPath.Equals(list.FolderPath, here) && list.Selected?.DisplayName == "a.txt");

            hold = list.HoldFolder();
            list.SetTarget(ReviewNode(elsewhere, true));
            await list.NavigateAsync(alpha);
            hold.Dispose();
            Check("the list's own navigation during a hold outranks what the canvas asked before it",
                ViewAllPath.Equals(list.FolderPath, alpha));

            // The canvas catching up late with a step the list took before
            // its latest: the list asked for that folder itself, and going
            // back to it would undo the step after.  The activation holds the
            // list while the canvas gets there, as the window's does.
            var gates = new Dictionary<string, TaskCompletionSource>(StringComparer.OrdinalIgnoreCase);
            TaskCompletionSource<ViewAllDirectorySnapshot>? slowRead = null;
            FolderListViewModel? racing = null;
            racing = new FolderListViewModel(
                (path, _, _) =>
                {
                    if (slowRead is { } gate && ViewAllPath.Equals(path, here))
                    {
                        slowRead = null;
                        return gate.Task;
                    }

                    return Task.FromResult(ReviewSnapshot(path, false, contents.GetValueOrDefault(path, [])));
                },
                async (path, _) =>
                {
                    using (racing!.HoldFolder())
                    {
                        var arrived = gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        await arrived.Task;
                        racing.SetTarget(ReviewNode(path, true));
                    }
                },
                _ => false,
                icons) { IsVisible = true };
            var toElsewhere = racing.NavigateAsync(elsewhere);
            await LiveWait(() => gates.ContainsKey(elsewhere), 2_000);
            var read = new TaskCompletionSource<ViewAllDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            slowRead = read;
            var toHere = racing.NavigateAsync(here);
            gates[elsewhere].SetResult();
            await toElsewhere;
            Check("the canvas getting late to a folder the list left keeps the list on its newer step",
                ViewAllPath.Equals(racing.FolderPath, here));
            read.SetResult(ReviewSnapshot(here, false, contents[here]));
            await LiveWait(() => gates.ContainsKey(here), 2_000);
            gates[here].SetResult();
            await toHere;
            Check("and the newer step ends where it was going, its rows read",
                ViewAllPath.Equals(racing.FolderPath, here) && racing.Items.Any(item => item.DisplayName == "a.txt"));

            // A row the list picks itself - a key, Ctrl or Shift on it - goes
            // through the shared selection, and its node, held for, can come
            // only after the list has taken a step of its own: Up, its folder
            // still being read.  The step is newer than the pick it outranks.
            var selection = new ItemSelection();
            TaskCompletionSource<ViewAllDirectorySnapshot>? rootRead = null;
            var picking = new FolderListViewModel(
                (path, _, _) =>
                {
                    if (rootRead is { } gate && ViewAllPath.Equals(path, root))
                    {
                        rootRead = null;
                        return gate.Task;
                    }

                    return Task.FromResult(ReviewSnapshot(path, false, contents.GetValueOrDefault(path, [])));
                },
                (_, _) => Task.CompletedTask,
                _ => false,
                icons) { IsVisible = true, SharedSelection = selection };
            var row = Path.Combine(here, "a.txt");
            await picking.NavigateAsync(here);
            selection.ReplaceSingle(row, false, 1, SelectionSource.List);
            hold = picking.HoldFolder();
            var upRead = rootRead = new TaskCompletionSource<ViewAllDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var up = picking.NavigateAsync(root);
            picking.SetTarget(ReviewNode(row, false, hereNode));
            hold.Dispose();
            upRead.SetResult(ReviewSnapshot(root, false, "here", "elsewhere"));
            await up;
            Check($"a row the list picked, its node come after the list went up, leaves the list up ({Path.GetFileName(picking.FolderPath)})",
                ViewAllPath.Equals(picking.FolderPath, root));

            // Likewise a row of the folder the list has just left, picked
            // while the next one is still being read.
            await picking.NavigateAsync(here);
            var awayRead = rootRead = new TaskCompletionSource<ViewAllDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var away = picking.NavigateAsync(root);
            selection.ReplaceSingle(row, false, 1, SelectionSource.List);
            hold = picking.HoldFolder();
            picking.SetTarget(ReviewNode(row, false, hereNode));
            hold.Dispose();
            awayRead.SetResult(ReviewSnapshot(root, false, "here", "elsewhere"));
            await away;
            Check($"a row of the folder left picked while the next is read leaves the list in the next ({Path.GetFileName(picking.FolderPath)})",
                ViewAllPath.Equals(picking.FolderPath, root));

            // A pick on the canvas meanwhile is still gone to once the hold ends.
            var far = Path.Combine(elsewhere, "far.txt");
            selection.ReplaceSingle(far, false, 1, SelectionSource.Canvas);
            hold = picking.HoldFolder();
            picking.SetTarget(ReviewNode(far, false, ReviewNode(elsewhere, true)));
            hold.Dispose();
            await LiveWait(() => !picking.IsLoading, 2_000);
            Check($"a file picked on the canvas during a hold, the selection shared, is gone to once it ends ({Path.GetFileName(picking.FolderPath)})",
                ViewAllPath.Equals(picking.FolderPath, elsewhere));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// I057: a read for a change of one folder is slow; the list goes to
    /// another, and that one changes while the slow read is still out.  The
    /// slow read is dropped when it lands - it is of the folder before - and
    /// the change it was asked to follow with was dropped with it.  Hidden
    /// before such a read ends, the list was not marked stale either.
    /// </summary>
    private static async Task ListLiveAgainAsync()
    {
        Section("folder list: a change during a read that is dropped is still read");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListLive", Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "old");
        var next = Path.Combine(root, "next");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(next);
        try
        {
            using var icons = new ShellIconService();
            var contents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [old] = ["o.txt"],
                [next] = ["n.txt"],
            };
            TaskCompletionSource<ViewAllDirectorySnapshot>? gate = null;
            string? gated = null;
            var gateTaken = false;
            var list = ReviewList(icons, path =>
            {
                if (gated is not null && ViewAllPath.Equals(path, gated))
                {
                    gated = null;
                    gateTaken = true;
                    return gate!.Task;
                }

                return Task.FromResult(ReviewSnapshot(path, false, contents[path]));
            });
            bool ListHas(string name) => list.Items.Any(item => item.DisplayName == name);

            await list.NavigateAsync(old);
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gated = old;
            list.OnFolderChanged(ReviewChange(old));
            await LiveWait(() => gateTaken, 2_000);
            Check("the old folder's read for a change is out", gateTaken);

            await list.NavigateAsync(next);
            Check("the list is in the new folder", ViewAllPath.Equals(list.FolderPath, next) && ListHas("n.txt"));
            contents[next] = ["n.txt", "new.txt"];
            list.OnFolderChanged(ReviewChange(next));
            gate.SetResult(ReviewSnapshot(old, false, "o.txt"));
            var shown = await LiveWait(() => ListHas("new.txt"), 2_000);
            Check($"a change in the new folder that came while the old read was out is shown once that read is dropped ({shown} ms)", shown >= 0);

            // Hidden while a read for a change is out, with another change come meanwhile.
            await LiveWait(() => !list.IsLoading, 1_000);
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gateTaken = false;
            gated = next;
            var merges = list.LiveMerges;
            list.OnFolderChanged(ReviewChange(next));
            await LiveWait(() => gateTaken, 2_000);
            contents[next] = ["n.txt", "new.txt", "hidden.txt"];
            list.OnFolderChanged(ReviewChange(next));
            list.IsVisible = false;
            gate.SetResult(ReviewSnapshot(next, false, "n.txt", "new.txt"));
            await LiveWait(() => list.LiveMerges > merges, 2_000);
            await Task.Delay(50);
            list.IsVisible = true;
            shown = await LiveWait(() => ListHas("hidden.txt"), 2_000);
            Check($"a change that came during a read, the list hidden before the read ended, is shown when the list is shown again ({shown} ms)", shown >= 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// I058: the list's folder is there but cannot be listed - a dangling
    /// junction, a folder taken out of reach - and a change came.  The read
    /// failed, the folder was found still there, so it was read again, and
    /// failed again, thousands of times a second until the list moved.
    /// </summary>
    private static async Task ListUnreadableFolderAsync()
    {
        Section("folder list: a folder that is there but cannot be read");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListUnreadable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var icons = new ShellIconService();
            Exception? failure = null;
            Exception? failOnce = null;
            var names = new List<string> { "a.txt" };
            var reads = 0;
            var list = ReviewList(icons, path =>
            {
                reads++;
                if (failOnce is { } once)
                {
                    failOnce = null;
                    return Task.FromException<ViewAllDirectorySnapshot>(once);
                }

                return failure is null
                    ? Task.FromResult(ReviewSnapshot(path, false, [.. names]))
                    : Task.FromException<ViewAllDirectorySnapshot>(failure);
            });

            await list.NavigateAsync(root);
            failure = new UnauthorizedAccessException("denied for the check");
            reads = 0;
            list.OnFolderChanged(ReviewChange(root));
            await Task.Delay(500);
            var looped = reads;
            Check($"a change in a folder that cannot be read reads it, not over and over ({looped} reads in half a second)", looped == 1);
            Check("and keeps its rows while it tries once more a moment later", list.Items.Count == 1);
            var said = await LiveWait(() => list.EmptyText == "Access denied.", 3_000);
            await Task.Delay(500);
            Check($"tried again and failing again, it says why the list is empty ({said} ms, {reads} reads, {list.EmptyText})",
                said >= 0 && reads == 2 && list.Items.Count == 0 && list.EmptyText == "Access denied.");

            failure = new IOException("not readable for the check");
            reads = 0;
            list.OnFolderChanged(ReviewChange(root));
            said = await LiveWait(() => list.EmptyText == "This folder could not be read.", 3_000);
            await Task.Delay(500);
            Check($"a later change reads it again, the once more too, and says it could not be read ({said} ms, {reads} reads)",
                said >= 0 && reads == 2);

            failure = null;
            list.OnFolderChanged(ReviewChange(root));
            var back = await LiveWait(() => list.Items.Any(item => item.DisplayName == "a.txt"), 2_000);
            Check($"readable again, the next change brings its rows back ({back} ms)", back >= 0 && list.EmptyText.Length == 0);

            // A share that blinked: the read fails once, and the once more finds it.
            names.Add("b.txt");
            failOnce = new IOException("the network name is no longer available, for a moment");
            reads = 0;
            list.OnFolderChanged(ReviewChange(root));
            var recovered = await LiveWait(() => list.Items.Any(item => item.DisplayName == "b.txt"), 3_000);
            Check($"a read that fails once is read again a moment later, the rows kept meanwhile ({recovered} ms, {reads} reads)",
                recovered >= 0 && reads == 2 && list.EmptyText.Length == 0 && list.Items.Count == 2);

            // Said it could not be read, the folder's time is not kept: on a
            // polled volume - a share that came back - the next poll reads
            // it, though its time is still the one the last good read found.
            failure = new IOException("not readable for the check");
            list.OnFolderChanged(ReviewChange(root));
            said = await LiveWait(() => list.EmptyText == "This folder could not be read.", 3_000);
            failure = null;
            list.OnPollDue(new WatchRoot(root, WatchKind.Polling, isNetwork: false));
            var polled = await LiveWait(() => list.Items.Count == 2, 2_000);
            Check($"answering again, the next poll brings its rows back ({said} ms to say it could not be read, {polled} ms back)",
                said >= 0 && polled >= 0 && list.EmptyText.Length == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// I155 and I212: the list's folder deleted takes the list to the folder
    /// above, and counts it; gone with its whole drive or share there is no
    /// folder above to go to, and the rows that stayed each answered a click
    /// with "That drive is not available".
    /// </summary>
    private static async Task ListGoneFolderAsync()
    {
        Section("folder list: its folder gone");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListGone", Guid.NewGuid().ToString("N"));
        var doomed = Path.Combine(root, "doomed");
        Directory.CreateDirectory(doomed);
        try
        {
            using var icons = new ShellIconService();
            // A folder on a drive that is not there reads as it did before the
            // drive went; one deleted from a drive that is there cannot be read.
            var list = ReviewList(icons, path => Directory.Exists(path) || !Directory.Exists(Path.GetPathRoot(path))
                ? Task.FromResult(ReviewSnapshot(path, false, "a.txt", "sub"))
                : Task.FromException<ViewAllDirectorySnapshot>(new DirectoryNotFoundException(path)));

            await list.NavigateAsync(doomed);
            Directory.Delete(doomed);
            list.OnFolderChanged(ReviewChange(doomed, ChangeKinds.Gone));
            var up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root), 2_000);
            Check($"the list's folder deleted, the list goes to the folder above and counts it ({up} ms)", up >= 0 && list.LeftGoneFolders == 1);

            // A drive letter nobody has: no folder on the way up is there.
            var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(code => (char)code)
                .Reverse()
                .FirstOrDefault(candidate => !DriveInfo.GetDrives().Any(drive => char.ToUpperInvariant(drive.Name[0]) == candidate)
                    && !Directory.Exists($"{candidate}:\\"));
            if (letter == default)
            {
                Check("no free drive letter to stand for a drive that went", true);
                return;
            }

            var away = $@"{letter}:\gone\folder";
            await list.NavigateAsync(away);
            Check("the list is in the folder on the drive", ViewAllPath.Equals(list.FolderPath, away) && list.Items.Count == 2);
            list.OnFolderChanged(ReviewChange(away, ChangeKinds.Gone));
            var cleared = await LiveWait(() => list.Items.Count == 0, 2_000);
            Check($"gone with its drive, the list keeps no rows that lead nowhere ({cleared} ms, {list.EmptyText})",
                cleared >= 0 && list.EmptyText == "This location is no longer available." && list.CountText.Length == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// I154: a folder with more entries than one read holds is filtered
    /// among the rows read; a name past them said "Nothing matches", as if
    /// it were not in the folder at all.
    /// </summary>
    private static async Task ListTruncatedFilterAsync()
    {
        Section("folder list: a filter in a folder bigger than one read");
        using var icons = new ShellIconService();
        var big = Path.Combine(Path.GetTempPath(), "UltraExplorerListBig");
        var small = Path.Combine(Path.GetTempPath(), "UltraExplorerListSmall");
        var list = ReviewList(icons, path => Task.FromResult(ViewAllPath.Equals(path, big)
            ? ReviewSnapshot(path, true, "a.txt", "b.txt", "c.txt")
            : ReviewSnapshot(path, false, "a.txt")));
        await list.NavigateAsync(big);
        list.Filter = "zzz";
        Check($"nothing among the rows read says only those were looked through ({list.EmptyText})",
            list.Items.Count == 0 && list.EmptyText.Contains("first 3", StringComparison.Ordinal) && list.EmptyText.Contains("zzz", StringComparison.Ordinal));
        await list.NavigateAsync(small);
        list.Filter = "zzz";
        Check($"a folder read whole says nothing matches, as before ({list.EmptyText})", list.EmptyText == "Nothing matches “zzz”.");
    }

    /// <summary>
    /// I156: every change restarted the timer that ends the new rows' fade,
    /// so under a steady stream of changes it never fired: every row added
    /// kept fading, and the rows waiting for it - deleted ones included -
    /// only grew.  Each row ends its fade its own time after it came.
    /// </summary>
    private static async Task ListFadeUnderChurnAsync()
    {
        Section("folder list: new rows stop fading under steady changes");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListChurn", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var icons = new ShellIconService();
            var names = new List<string> { "seed.txt" };
            var list = ReviewList(icons, path => Task.FromResult(ReviewSnapshot(path, false, [.. names])));
            await list.NavigateAsync(root);

            names.Add("churn-00.txt");
            list.OnFolderChanged(ReviewChange(root));
            await LiveWait(() => list.Items.Any(item => item.DisplayName == "churn-00.txt"), 2_000);
            var first = list.Items.FirstOrDefault(item => item.DisplayName == "churn-00.txt");
            Check("a row that came fades in", first is { IsNew: true });

            var clock = Stopwatch.StartNew();
            var ended = -1L;
            for (var index = 1; clock.ElapsedMilliseconds < 1_800; index++)
            {
                names.Add($"churn-{index:D2}.txt");
                if (index % 3 == 0)
                {
                    names.RemoveAt(2);
                }

                list.OnFolderChanged(ReviewChange(root));
                var step = Stopwatch.StartNew();
                while (step.ElapsedMilliseconds < 150)
                {
                    if (ended < 0 && first is { IsNew: false })
                    {
                        ended = clock.ElapsedMilliseconds;
                    }

                    await Task.Delay(5);
                }
            }

            Check($"while the folder keeps changing, a new row stops fading {ended} ms after it came (about 600)", ended is >= 0 and <= 1_200);
            var waiting = ((ICollection)typeof(FolderListViewModel).GetField("_newRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(list)!).Count;
            Check($"and only the rows of the last moment wait to stop ({waiting})", waiting <= 8);
            await LiveWait(() => list.Items.All(item => !item.IsNew), 2_000);
            Check("once the changes stop, every row has stopped", list.Items.All(item => !item.IsNew));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the search panel ------------------------------------------------------------

    private static readonly MethodInfo SearchApply = typeof(SearchViewModel).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SearchApplyForTest(SearchViewModel search, SearchSnapshot snapshot, SearchQuery query) =>
        SearchApply.Invoke(search, [snapshot, query]);

    private static void SearchOpenForTest(SearchViewModel search, string? here)
    {
        typeof(SearchViewModel).GetProperty(nameof(SearchViewModel.IsOpen))!.SetValue(search, true);
        typeof(SearchViewModel).GetField("_here", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(search, here);
    }

    /// <summary>
    /// I082: a walk that stands in for an Everything still starting waits for
    /// it and asks it again.  One that stands in for an Everything loaded
    /// that did not answer - a query too slow, a reply that never came - did
    /// the same: it was asked again, failed again, and every drive was
    /// walked again, for as long as the panel was open.
    /// </summary>
    private static Task SearchWaitAfterFailureAsync()
    {
        Section("search: a walk after Everything failed to answer is not repeated");
        using var icons = new ShellIconService();
        using var search = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        SearchOpenForTest(search, null);
        var waiting = (DispatcherTimer)typeof(SearchViewModel).GetField("_waitingForEverything", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search)!;
        var query = SearchQuery.Parse("probe");

        var failed = new SearchSnapshot([], 0, 0, SearchSource.Walk, true, "walked", 1);
        var marked = typeof(SearchSnapshot).GetProperty("EverythingFailed") is { } flag && flag.CanWrite;
        if (marked)
        {
            typeof(SearchSnapshot).GetProperty("EverythingFailed")!.SetValue(failed, true);
        }

        SearchApplyForTest(search, failed, query);
        Check("a walk after a loaded Everything failed to answer does not wait to ask it again", marked && !waiting.IsEnabled);

        SearchApplyForTest(search, new SearchSnapshot([], 0, 0, SearchSource.Walk, true, "walked", 1), query);
        Check("a walk while Everything is starting still waits for it", waiting.IsEnabled);
        search.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// I162: a result found again is the same row, so it keeps its icon and
    /// its selection - and it kept the highlighting of the query it was first
    /// found for, and where it was said to be from the folder searched then.
    /// </summary>
    private static Task SearchRowReuseAsync()
    {
        Section("search: a row found again shows this search");
        using var icons = new ShellIconService();
        using var search = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        static SearchHit Below() => new("Report 2024.xlsx", @"C:\A\B\C", false, 10, null, null) { Place = SearchPlace.Below };
        static SearchSnapshot Of(SearchHit hit) => new([hit], 1, 0, SearchSource.Everything, true, "found", 1);
        static string Lit(SearchResultViewModel row) => string.Concat(row.NameSegments.Where(segment => segment.IsMatch).Select(segment => segment.Text));

        SearchOpenForTest(search, @"C:\A\B");
        SearchApplyForTest(search, Of(Below()), SearchQuery.Parse("Rep"));
        var row = search.Results.Single();
        Check($"found for 'Rep', 'Rep' is lit ({Lit(row)}), and it is in B\\C ({row.Location})", Lit(row) == "Rep" && row.Location == @"B\C");

        SearchApplyForTest(search, Of(Below()), SearchQuery.Parse("report"));
        row = search.Results.Single();
        Check($"found again for 'report', 'Report' is lit ({Lit(row)})", Lit(row) == "Report");

        SearchOpenForTest(search, @"C:\A");
        SearchApplyForTest(search, Of(Below()), SearchQuery.Parse("report"));
        row = search.Results.Single();
        Check($"searched from the folder above, it is in A\\B\\C ({row.Location})", row.Location == @"A\B\C");
        search.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// I210: asking whether Everything is ready waits up to a second for its
    /// answer, and the drives are looked at for subst letters, before the
    /// search's first wait - on the window's thread, at every key.  Seen
    /// here through the subst probe, which comes right after the readiness
    /// one.  Only where Everything is running: otherwise the search walks
    /// every drive.
    /// </summary>
    private static async Task SearchProbeThreadAsync()
    {
        Section("search: the probes of a search are made off the window's thread");
        if (!EverythingClient.IsDatabaseLoaded)
        {
            Console.WriteLine("  (Everything is not running here: skipped)");
            return;
        }

        using var icons = new ShellIconService();
        using var search = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        var window = Environment.CurrentManagedThreadId;
        var probedOn = 0;
        var engine = new SearchEngine(EverythingClient.Shared, letter =>
        {
            Interlocked.CompareExchange(ref probedOn, Environment.CurrentManagedThreadId, 0);
            return VolumeResolver.FromSystem(letter);
        });
        typeof(SearchViewModel).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(search, engine);
        search.HereFolder = () => null;
        search.Text = "uxprobe" + Guid.NewGuid().ToString("N")[..12];
        search.SearchNow();
        await LiveWait(() => Volatile.Read(ref probedOn) != 0, 5_000);
        Check($"the drives are looked at on another thread than the window's ({probedOn} against {window})", probedOn != 0 && probedOn != window);
        await LiveWait(() => !search.IsBusy, 6_000);
        search.Close();
    }

    /// <summary>
    /// I083: Everything answers on a window of this process.  Run elevated,
    /// with Everything at the ordinary level, Windows drops its answer unless
    /// the window says it takes that message from a lower level: every query
    /// waited its five seconds and then walked.  Asked here as Windows tells
    /// it: whether the message was already let through.
    /// </summary>
    private static void SearchReplyWindowChecks()
    {
        Section("search: Everything's replies get through to an elevated window");
        var ensure = typeof(EverythingClient).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance);
        var window = ensure?.Invoke(EverythingClient.Shared, null) is nint made ? made : 0;
        if (window == 0)
        {
            Check("the reply window is made", false);
            return;
        }

        var status = new ReviewChangeFilter { Size = (uint)Marshal.SizeOf<ReviewChangeFilter>() };
        var asked = ReviewChangeWindowMessageFilterEx(window, 0x004A, 1, ref status);
        Check($"the reply window already takes a reply from a lower level (status {status.ExtStatus})", asked && status.ExtStatus == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReviewChangeFilter
    {
        public uint Size;
        public uint ExtStatus;
    }

    [DllImport("user32.dll", EntryPoint = "ChangeWindowMessageFilterEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReviewChangeWindowMessageFilterEx(nint window, uint message, uint action, ref ReviewChangeFilter status);

    /// <summary>
    /// I161: a function's argument in quotes - a folder with a space,
    /// several extensions, a whole name - was read as plain text with a
    /// colon in it, so the walk found nothing where Everything found plenty,
    /// and a <c>!path:</c> left nothing out.
    /// </summary>
    private static void SearchQuotedFunctionChecks()
    {
        Section("search: quoted function arguments in the walk");
        var under = SearchQuery.Parse(@"path:""C:\Program Files\"" setup");
        Check("path: with a quoted folder keeps what is under it",
            under.Matches("setup.exe", @"C:\Program Files\Tool", false) && !under.Matches("setup.exe", @"C:\Other", false));
        var outside = SearchQuery.Parse(@"setup !path:""C:\Program Files\""");
        Check("!path: with a quoted folder leaves it out",
            outside.Matches("setup.exe", @"C:\Other", false) && !outside.Matches("setup.exe", @"C:\Program Files\Tool", false));
        var extensions = SearchQuery.Parse(@"cat ext:""png;jpg""");
        Check("ext: with quoted extensions keeps them", extensions.Matches("cat.png", @"C:\x", false) && !extensions.Matches("cat.txt", @"C:\x", false));
        var whole = SearchQuery.Parse(@"wfn:""my file.txt""");
        Check("wfn: with a quoted name is that whole name", whole.Matches("My File.txt", @"C:\x", false) && !whole.Matches("my file.txt.bak", @"C:\x", false));
        var phrase = SearchQuery.Parse(@"""note: draft""");
        Check("a quoted phrase with a colon inside is still a phrase", phrase.Matches("a note: draft b", @"C:\x", false) && !phrase.Matches("draft", @"C:\x", false));
        Check("and Everything is sent the text as typed", under.Text == @"path:""C:\Program Files\"" setup");
    }

    // ---- the walk --------------------------------------------------------------------

    private static async Task SearchWalkReviewChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearchWalk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await SearchWalkFromSubstAsync(root);
            await SearchWalkPlaceholderAsync(root);
            await SearchWalkTrailingDotAsync(root);
        }
        finally
        {
            TryDelete(@"\\?\" + root);
        }
    }

    /// <summary>
    /// I084: searched from under a subst letter, the walk took that letter's
    /// root out with every other subst letter, and skipped the folder it
    /// stands for on its real drive - so the rest of the letter was walked
    /// under neither spelling.  Here two folders stand for the drive and the
    /// letter.
    /// </summary>
    private static async Task SearchWalkFromSubstAsync(string root)
    {
        Section("search: a walk from a subst letter");
        var plan = typeof(SearchEngine).GetMethod("WalkPlan", BindingFlags.NonPublic | BindingFlags.Static);
        if (plan is null)
        {
            Check("the walk's roots can be worked out on their own", false);
            return;
        }

        IReadOnlyList<(string Letter, string Target)> substs = [(@"R:\", @"C:\Work")];
        var (roots, skip) = ((string[] Roots, string[] Skip))plan.Invoke(null, [@"R:\src", new[] { @"C:\", @"D:\", @"R:\" }, substs])!;
        Check($"from under the letter, its root is walked ({string.Join(" ", roots)})", roots.SequenceEqual([@"C:\", @"D:\", @"R:\"]));
        Check($"and what it stands for is not walked twice ({string.Join(" ", skip)})", skip.SequenceEqual([@"C:\Work"]));
        (roots, skip) = ((string[] Roots, string[] Skip))plan.Invoke(null, [@"C:\Work\src", new[] { @"C:\", @"D:\", @"R:\" }, substs])!;
        Check("from a real path, the letter is left out as before", roots.SequenceEqual([@"C:\", @"D:\"]) && skip.Length == 0);

        var drive = Path.Combine(root, "C") + @"\";
        var letter = Path.Combine(root, "R") + @"\";
        var target = Path.Combine(root, "C", "Work");
        Directory.CreateDirectory(Path.Combine(target, "docs"));
        Directory.CreateDirectory(Path.Combine(root, "C", "other"));
        Directory.CreateDirectory(Path.Combine(root, "R", "src"));
        Directory.CreateDirectory(Path.Combine(root, "R", "docs"));
        File.WriteAllText(Path.Combine(target, "docs", "substfind.txt"), "x");
        File.WriteAllText(Path.Combine(root, "C", "other", "substfind.txt"), "x");
        File.WriteAllText(Path.Combine(root, "R", "docs", "substfind.txt"), "x");
        File.WriteAllText(Path.Combine(root, "R", "src", "substfind.txt"), "x");
        var here = Path.Combine(root, "R", "src");
        (roots, skip) = ((string[] Roots, string[] Skip))plan.Invoke(null, [here, new[] { drive, letter }, (IReadOnlyList<(string, string)>)[(letter, target)]])!;
        var walk = new FolderWalk(SearchQuery.Parse("substfind"), 100);
        await walk.RunAsync(here, roots, skip, CancellationToken.None);
        var found = walk.Drain().Select(hit => hit.Directory[root.Length..]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Check($"the rest of the letter is found, the folder it stands for is not walked again ({string.Join(" ", found)})",
            found.SequenceEqual([@"\C\other", @"\R\docs", @"\R\src"], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// I113: a cloud placeholder - OneDrive's Documents and Desktop - is a
    /// folder with a reparse point that is not a link; the walk took every
    /// reparse point for a link and never went in.  A folder with a reparse
    /// point of a kind nothing here handles stands in for one: it cannot be
    /// listed, but whether the walk tries is what is asked.
    /// </summary>
    private static async Task SearchWalkPlaceholderAsync(string root)
    {
        Section("search: the walk goes into a placeholder folder, not into a link");
        var top = Path.Combine(root, "cloud");
        var plain = Path.Combine(top, "plain");
        var placeholder = Path.Combine(top, "placeholder");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(placeholder);
        File.WriteAllText(Path.Combine(plain, "cloudfind.txt"), "x");
        if (!TrySetForeignReparsePoint(placeholder))
        {
            Console.WriteLine("  note  could not put a reparse point on a folder here; skipped");
            return;
        }

        var linked = TryCreateJunction(Path.Combine(top, "link"), plain);
        var walk = new FolderWalk(SearchQuery.Parse("cloudfind"), 100);
        await walk.RunAsync(null, [top], [], CancellationToken.None);
        var found = walk.Drain();
        Check($"the placeholder is gone into ({walk.FoldersRead} folders read: the folder, plain and the placeholder)", walk.FoldersRead == 3);
        Check($"and a junction{(linked ? string.Empty : " (none could be made)")} is not: the file is found once ({found.Count})", found.Count == 1);
    }

    /// <summary>
    /// I135: a folder named with a trailing dot or space is read without the
    /// extended-length prefix, which drops the dot or the space: "dup." was
    /// listed as its twin "dup", and "lone " as nothing at all.
    /// </summary>
    private static async Task SearchWalkTrailingDotAsync(string root)
    {
        Section("search: the walk reads folders named with a trailing dot or space");
        var nasty = Path.Combine(root, "nasty");
        Directory.CreateDirectory(Path.Combine(nasty, "dup"));
        Directory.CreateDirectory(@"\\?\" + Path.Combine(nasty, "dup."));
        Directory.CreateDirectory(@"\\?\" + Path.Combine(nasty, "lone "));
        File.WriteAllText(Path.Combine(nasty, "dup", "twinfind.txt"), "x");
        File.WriteAllText(@"\\?\" + Path.Combine(nasty, "dup.", "dotfind.txt"), "x");
        File.WriteAllText(@"\\?\" + Path.Combine(nasty, "lone ", "spacefind.txt"), "x");

        foreach (var first in new[] { null, Path.Combine(nasty, "dup.") })
        {
            var walk = new FolderWalk(SearchQuery.Parse("find"), 100);
            await walk.RunAsync(first, [nasty], [], CancellationToken.None);
            var found = walk.Drain();
            var from = first is null ? "walked" : "searched from dup.";
            Check($"{from}: the file in 'dup.' is found there",
                found.Count(hit => hit.Name == "dotfind.txt" && hit.Directory.EndsWith(@"\dup.", StringComparison.Ordinal)) == 1);
            Check($"{from}: the twin's file is found once, in its own folder",
                found.Count(hit => hit.Name == "twinfind.txt") == 1 && found.Single(hit => hit.Name == "twinfind.txt").Directory.EndsWith(@"\dup", StringComparison.Ordinal));
            Check($"{from}: the file in 'lone ' is found there",
                found.Count(hit => hit.Name == "spacefind.txt" && hit.Directory.EndsWith(@"\lone ", StringComparison.Ordinal)) == 1);
            Check($"{from}: no hit carries the extended-length prefix", found.All(hit => !hit.Directory.StartsWith(@"\\?\", StringComparison.Ordinal)));
        }
    }
}
