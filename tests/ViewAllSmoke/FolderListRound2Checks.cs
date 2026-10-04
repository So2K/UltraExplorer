using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The second review's issues with the folder list (J005, J015, J027, J088,
/// J114 and J115 of coordination/review/20261004-claude-review2-full.md),
/// each replayed as it went wrong: the list's folder deleted elsewhere
/// selected the folder above on the canvas, for the next Delete to recycle;
/// rows past the three hundredth never got an icon; the folder before stayed
/// under the next one's name while that was read, its rows answering Enter
/// and a double-click; every refill asked the canvas about every row for a
/// value nothing showed; a filter typed in a folder that could not be read
/// said it was empty; and a row clicked while another row's reveal was under
/// way was dropped.
/// </summary>
internal static partial class Program
{
    private static Task FolderListRound2Checks()
    {
        Section("folder list: second review");
        RunOnSta("list leaves a deleted folder without the canvas", ListGoneLeavesCanvasAsync);
        RunOnSta("list leaves a folder found gone as it is read", ListGoneOnReadAsync);
        RunOnSta("list leaves a deleted folder, end to end", ListGoneEndToEndAsync);
        RunOnSta("list icons past the first rows", ListIconsPastBudgetAsync);
        RunOnSta("list while the next folder is read", ListLoadingAsync);
        RunOnSta("list refill cost", ListRefillCostAsync);
        RunOnSta("list filter in a folder that could not be read", ListFilterKeepsFailureAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// J005: the list's folder deleted outside the app took the list to the
    /// folder above - and the canvas with it, selecting that folder, so the
    /// next Delete recycled the whole parent.  Hidden, the list did the same.
    /// Only the list moves; what the canvas selects is the tree's to prune.
    /// </summary>
    private static async Task ListGoneLeavesCanvasAsync()
    {
        Section("folder list round 2: its folder deleted, the canvas left alone");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListGone2", Guid.NewGuid().ToString("N"));
        var doomed = Path.Combine(root, "doomed");
        var hidden = Path.Combine(root, "hidden");
        Directory.CreateDirectory(doomed);
        Directory.CreateDirectory(hidden);
        try
        {
            using var icons = new ShellIconService();
            var activated = new List<string>();
            var list = new FolderListViewModel(
                (path, _, _) => Directory.Exists(path)
                    ? Task.FromResult(ReviewSnapshot(path, false, "a.txt", "sub"))
                    : Task.FromException<ViewAllDirectorySnapshot>(new DirectoryNotFoundException(path)),
                (path, _) => { activated.Add(path); return Task.CompletedTask; },
                _ => false,
                icons) { IsVisible = true };

            await list.NavigateAsync(doomed);
            Check("the list's own step into a folder takes the canvas there", activated.Count == 1 && ViewAllPath.Equals(activated[0], doomed));
            activated.Clear();
            Directory.Delete(doomed);
            list.OnFolderChanged(ReviewChange(doomed, ChangeKinds.Gone));
            var up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root) && !list.IsLoading, 2_000);
            await Task.Delay(50);
            Check($"its folder deleted, the list goes to the folder above ({up} ms, {list.Items.Count} rows)", up >= 0 && list.Items.Count == 2);
            Check($"and asks the canvas to go nowhere - it would select the folder above for Delete ({activated.Count} asked: {string.Join(", ", activated.Select(Path.GetFileName))})",
                activated.Count == 0);

            await list.NavigateAsync(hidden);
            list.IsVisible = false;
            activated.Clear();
            Directory.Delete(hidden);
            list.OnFolderChanged(ReviewChange(hidden, ChangeKinds.Gone));
            up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root), 2_000);
            await Task.Delay(50);
            Check($"hidden, it goes up too ({up} ms), and the canvas is not asked either ({activated.Count} asked)",
                up >= 0 && activated.Count == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// J005: a folder already gone when the list is pointed at it - the
    /// canvas still holding its node - failed its read and the list stayed
    /// on "This folder could not be read." for good: the change that said
    /// it went had come before the list was there to hear it.  It goes to
    /// the folder above, as a folder deleted under it does, and only it.
    /// </summary>
    private static async Task ListGoneOnReadAsync()
    {
        Section("folder list round 2: a folder found gone as it is read");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListGoneRead", Guid.NewGuid().ToString("N"));
        var gone = Path.Combine(root, "gone");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        try
        {
            using var icons = new ShellIconService();
            var activated = new List<string>();
            var list = new FolderListViewModel(
                (path, _, _) => Directory.Exists(path)
                    ? Task.FromResult(ReviewSnapshot(path, false, "a.txt", "sub"))
                    : Task.FromException<ViewAllDirectorySnapshot>(new DirectoryNotFoundException(path)),
                (path, _) => { activated.Add(path); return Task.CompletedTask; },
                _ => false,
                icons) { IsVisible = true };

            await list.NavigateAsync(other);
            activated.Clear();
            list.SetTarget(gone, null);
            var up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root) && !list.IsLoading && list.Items.Count == 2, 3_000);
            Check($"pointed at a folder that is gone, the list goes to the folder above and reads it ({up} ms, in {Path.GetFileName(list.FolderPath)}, {list.EmptyText})",
                up >= 0 && list.EmptyText.Length == 0);
            Check($"without asking the canvas to go there ({activated.Count} asked)", activated.Count == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// J005 end to end: the canvas has a file selected in the list's folder,
    /// and the folder is deleted by another program.  The list goes up, and
    /// the folder above it must not end up selected.
    /// </summary>
    private static async Task ListGoneEndToEndAsync()
    {
        Section("folder list round 2: its folder deleted elsewhere, end to end");
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerListGoneLive", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(baseDirectory, "J", "parent");
        var doomed = Path.Combine(parent, "doomed");
        var inside = Path.Combine(doomed, "a.txt");
        Directory.CreateDirectory(doomed);
        File.WriteAllText(inside, "a");
        File.WriteAllText(Path.Combine(parent, "keep.txt"), "k");
        try
        {
            using var icons = new ShellIconService();
            var marks = new FolderMarkService(Path.Combine(baseDirectory, "marks.json"));
            using var hub = new ChangeHub(TimeProvider.System);
            using var tree = new ViewAllViewModel(marks, icons, Path.Combine(baseDirectory, "tree.json")) { PreferLightReveal = true };
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.IsCanvasShown = false;
            await tree.InitializeAsync(parent);
            tree.FolderList.IsVisible = true;

            await tree.SelectPathAsync(inside);
            var there = await LiveWait(() => ViewAllPath.Equals(tree.FolderList.FolderPath, doomed) && !tree.FolderList.IsLoading, 3_000);
            Check($"a file selected, the list shows its folder ({there} ms)", there >= 0);

            Directory.Delete(doomed, recursive: true);
            var up = await LiveWait(() => ViewAllPath.Equals(tree.FolderList.FolderPath, parent), 3_000);
            await Task.Delay(600);
            Check($"the folder deleted elsewhere, the list goes up ({up} ms, in {Path.GetFileName(tree.FolderList.FolderPath)}, {tree.FolderList.EmptyText})", up >= 0);
            Check($"and the folder above is not selected for the next Delete ({tree.Selection.Count} selected: {string.Join(", ", tree.Selection.Paths.Select(Path.GetFileName))})",
                !tree.Selection.Contains(parent));
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// J015: rows past the three hundredth were never asked for an icon -
    /// not as the list filled, nor when scrolled to - and kept the glyph for
    /// good, however well known their type's icon was.  A row asks for its
    /// icon when the list box first shows it - binds its icon - and only
    /// then: five thousand programs are not five thousand questions.
    /// </summary>
    private static async Task ListIconsPastBudgetAsync()
    {
        Section("folder list round 2: icons for rows past the first");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerListIcons");
        var picture = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        picture.Freeze();
        using var icons = new ShellIconService((_, _) => picture, Dispatcher.CurrentDispatcher);
        string[] types = [".txt", ".png", ".log", ".dat", ".exe"];
        var names = Enumerable.Range(0, 5_000).Select(index => $"f{index:D4}{types[index % types.Length]}").ToArray();
        var list = new FolderListViewModel(
            (path, _, _) => Task.FromResult(ReviewSnapshot(path, false, names)),
            (_, _) => Task.CompletedTask,
            _ => false,
            icons) { IsVisible = true };

        await list.NavigateAsync(folder);
        var first = await LiveWait(() => list.Items.Take(300).All(row => row.Icon is not null), 3_000);
        await Task.Delay(100);
        var askedAtFill = icons.ExtractionCount;
        Check($"the first rows have their icons as the list fills ({first} ms)", first >= 0);

        // What the list box does for a row it shows: binds its icon.
        var shown = list.Items.Skip(4_000).Take(40).ToList();
        var images = shown.Select(row =>
        {
            var image = new Image { DataContext = row };
            image.SetBinding(Image.SourceProperty, new Binding(nameof(FolderListItem.Icon)));
            return image;
        }).ToList();
        var settled = await LiveWait(() => images.All(image => image.Source is not null), 2_000);
        var missing = images.Count(image => image.Source is null);
        Check($"rows scrolled to past the first three hundred get their icons ({missing} of {images.Count} without one, {settled} ms)",
            missing == 0);

        // Programs are asked about one by one: the rows never shown are not.
        var programsShown = shown.Count(row => row.DisplayName.EndsWith(".exe", StringComparison.Ordinal));
        var asked = icons.ExtractionCount - askedAtFill;
        Check($"only the rows shown are asked for ({asked} questions for {programsShown} programs shown, of {names.Length / types.Length} in the folder)",
            asked <= programsShown + types.Length);

        // Ordered afresh, a row that moved past the first rows is asked for when shown too.
        list.Sort = new ItemSort(SortColumn.Name, Descending: true);
        var moved = list.Items[4_321];
        var movedImage = new Image { DataContext = moved };
        movedImage.SetBinding(Image.SourceProperty, new Binding(nameof(FolderListItem.Icon)));
        var resorted = await LiveWait(() => movedImage.Source is not null, 2_000);
        Check($"after another order, a row shown far down still gets its icon ({resorted} ms)", resorted >= 0);
    }

    /// <summary>
    /// J027: while the next folder was read, the list kept the folder
    /// before's rows and count under the next one's name - for seconds on a
    /// slow share - and Enter, a double-click or a click acted on those rows.
    /// A moment into a read that is taking its time, the rows go and the
    /// list says it is loading; a row of the folder before is not acted on.
    /// Reading the same folder again keeps its rows where they are.
    /// </summary>
    private static async Task ListLoadingAsync()
    {
        Section("folder list round 2: the next folder being read");
        var before = Path.Combine(Path.GetTempPath(), "UltraExplorerListBefore");
        var next = Path.Combine(Path.GetTempPath(), "UltraExplorerListNext");
        using var icons = new ShellIconService();
        TaskCompletionSource<ViewAllDirectorySnapshot>? gate = null;
        var activated = new List<string>();
        var list = new FolderListViewModel(
            (path, _, _) => ViewAllPath.Equals(path, next) && gate is { } slow
                ? slow.Task
                : Task.FromResult(ViewAllPath.Equals(path, next)
                    ? ReviewSnapshot(path, false, "n1.txt", "n2.txt")
                    : ReviewSnapshot(path, false, "b1.txt", "b2.txt", "b3.txt")),
            (path, _) => { activated.Add(path); return Task.CompletedTask; },
            _ => false,
            icons) { IsVisible = true };

        await list.NavigateAsync(before);
        activated.Clear();
        var old = list.Items[0];
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var going = list.NavigateAsync(next);
        list.RevealCommand.Execute(old);
        list.ActivateCommand.Execute(old);
        list.OpenFirstMatchCommand.Execute(null);
        await Task.Delay(20);
        Check($"a row of the folder before, clicked, opened or entered while the next is read, is not acted on ({activated.Count} acted on)",
            activated.Count == 0);

        var cleared = await LiveWait(() => list.Items.Count == 0, 1_500);
        Check($"a moment into a slow read, the rows of the folder before go ({cleared} ms, {list.Items.Count} rows left)", cleared is >= 0 and < 1_000);
        Check($"with no count, and the list says it is loading ({list.CountText}|{list.EmptyText})",
            list.CountText.Length == 0 && list.EmptyText == "Loading…");
        list.Filter = "n";
        Check($"a filter typed meanwhile still says it is loading ({list.EmptyText})", list.EmptyText == "Loading…");
        list.Filter = string.Empty;

        gate.SetResult(ReviewSnapshot(next, false, "n1.txt", "n2.txt"));
        await going;
        Check($"the read in, its rows are shown ({list.Items.Count}, {list.CountText}, {list.EmptyText})",
            list.Items.Count == 2 && list.CountText == "2" && list.EmptyText.Length == 0);
        Check("and the list's own step took the canvas there", activated.Count == 1 && ViewAllPath.Equals(activated[0], next));

        // The same folder read again - F5, a change - keeps its rows meanwhile.
        var kept = list.Items[0];
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var again = list.ReloadAsync();
        await Task.Delay(400);
        Check($"the same folder read again keeps its rows while it is read ({list.Items.Count}, {list.EmptyText})",
            list.Items.Count == 2 && ReferenceEquals(list.Items[0], kept) && list.EmptyText.Length == 0);
        activated.Clear();
        list.RevealCommand.Execute(kept);
        Check("and they can be clicked meanwhile", activated.Count == 1);
        gate.SetResult(ReviewSnapshot(next, false, "n1.txt", "n2.txt"));
        await again;

        // A read that answers at once never shows the loading state.
        gate = null;
        await list.NavigateAsync(before);
        Check($"a folder read at once shows its rows with no loading state ({list.Items.Count}, {list.EmptyText})",
            list.Items.Count == 3 && list.EmptyText.Length == 0);
    }

    /// <summary>
    /// J088: every refill - a key typed in the filter, another order, a
    /// change merged - asked the canvas, row by row, whether it held a node
    /// for the row's path, normalising every path on the way, for a value
    /// nothing showed.  A row is asked about when that is read, and only then.
    /// </summary>
    private static async Task ListRefillCostAsync()
    {
        Section("folder list round 2: what a refill costs");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerListRefill");
        var names = Enumerable.Range(0, 5_000).Select(index => $"f{index:D4}.txt").ToArray();
        using var icons = new ShellIconService();
        using var graph = new ViewAllGraphService();
        var asked = 0;
        var list = new FolderListViewModel(
            (path, _, _) => Task.FromResult(ReviewSnapshot(path, false, names)),
            (_, _) => Task.CompletedTask,
            path => { asked++; return graph.TryGetNode(path, out _); },
            icons) { IsVisible = true };
        await list.NavigateAsync(folder);

        // A filter of blanks is no filter: each of these refills the rows whole.
        for (var warm = 0; warm < 4; warm++)
        {
            list.Filter = warm % 2 == 0 ? " " : string.Empty;
        }

        asked = 0;
        const int Refills = 40;
        var watch = Stopwatch.StartNew();
        for (var refill = 0; refill < Refills; refill++)
        {
            list.Filter = refill % 2 == 0 ? " " : string.Empty;
        }

        watch.Stop();
        var each = watch.Elapsed.TotalMilliseconds / Refills;
        Console.WriteLine($"  info  a refill of {list.Items.Count:N0} rows takes {each:0.00} ms; the canvas was asked {asked / Refills:N0} times a refill");
        Check($"a refill asks the canvas nothing about its rows ({asked / Refills:N0} questions a refill, {each:0.00} ms)", asked == 0);
        Check("and still shows every row", list.Items.Count == names.Length);

        asked = 0;
        var onCanvas = list.Items[4_321].IsOnCanvas;
        Check($"a row read for whether the canvas holds it asks then, for itself ({asked} asked, {onCanvas})", asked == 1 && !onCanvas);
    }

    /// <summary>
    /// J114: a filter typed in a folder that could not be read - access
    /// denied, a share gone - put "This folder is empty." in place of why,
    /// and clearing the filter left it there; likewise with no folder.
    /// </summary>
    private static async Task ListFilterKeepsFailureAsync()
    {
        Section("folder list round 2: a filter where nothing could be read");
        var denied = Path.Combine(Path.GetTempPath(), "UltraExplorerListDenied");
        var broken = Path.Combine(Path.GetTempPath(), "UltraExplorerListBroken");
        using var icons = new ShellIconService();
        var list = new FolderListViewModel(
            (path, _, _) => Task.FromException<ViewAllDirectorySnapshot>(ViewAllPath.Equals(path, denied)
                ? new UnauthorizedAccessException("denied for the check")
                : new IOException("not readable for the check")),
            (_, _) => Task.CompletedTask,
            _ => false,
            icons);

        list.IsVisible = true;
        var nothing = list.EmptyText;
        list.Filter = "abc";
        var filtered = list.EmptyText;
        list.Filter = string.Empty;
        Check($"with no folder, a filter keeps saying nothing is selected ({nothing} | {filtered} | {list.EmptyText})",
            nothing == "Nothing is selected." && filtered == nothing && list.EmptyText == nothing);

        await list.NavigateAsync(denied);
        var said = list.EmptyText;
        list.Filter = "abc";
        filtered = list.EmptyText;
        list.Filter = string.Empty;
        Check($"a folder denied keeps saying so through a filter and after it ({said} | {filtered} | {list.EmptyText})",
            said == "Access denied." && filtered == said && list.EmptyText == said);

        await list.NavigateAsync(broken);
        said = list.EmptyText;
        list.Filter = "abc";
        filtered = list.EmptyText;
        list.Filter = string.Empty;
        Check($"a folder that could not be read keeps saying so ({said} | {filtered} | {list.EmptyText})",
            said == "This folder could not be read." && filtered == said && list.EmptyText == said);
    }
}
