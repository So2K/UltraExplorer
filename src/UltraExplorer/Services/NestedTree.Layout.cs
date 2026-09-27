using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

// Placing: which children are cells - the hidden, the user's hidden and
// the ones asked for by name - where each one goes in its folder's order,
// and the background pass that places the whole tree again, a few
// milliseconds at a time, after a change that can reach every folder.
public sealed partial class NestedTree
{
    private readonly HashSet<string> _userHidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forcedVisible = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders that have at least one child in either set above.  Only their
    /// children need looking up at all; every other folder's children are
    /// filtered by their attributes alone, without hashing a single path.
    /// </summary>
    private readonly HashSet<string> _filterParents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Budget for one background slice of re-placing folders after a change of
    /// order: well inside a frame, so the canvas keeps drawing while it runs.
    /// Shared with whatever was placed on demand since the slice before (see
    /// <see cref="_placedOnDemandTicks"/>), so a frame and its slice together
    /// stay within it.
    /// </summary>
    private static readonly long SortSliceTicks = Stopwatch.Frequency * 4 / 1000;

    /// <summary>
    /// Less than this left of a slice's allowance and the slice waits for the
    /// next frame instead: a sliver of time places next to nothing, and the
    /// check it takes to find that out is not free either.
    /// </summary>
    private static readonly long MinimumSliceTicks = Stopwatch.Frequency / 2000;

    private FolderOrders _orders;

    /// <summary>Folders the background pass after a change of order has still to visit, shallowest first.</summary>
    private readonly Queue<NestedFolder> _sortSweep = new();

    /// <summary>
    /// Folders the canvas drew in their previous order because its frame had
    /// no allowance left to place them: the next slice places these before
    /// going on down the tree, so what is on screen catches up first and the
    /// thousands of folders off it wait.
    /// </summary>
    private readonly Queue<NestedFolder> _sortFirst = new();

    private bool _sortSlicePosted;
    private List<TaskCompletionSource>? _sortIdleWaiters;

    /// <summary>
    /// Whether the pass under way has placed again a folder on the canvas.
    /// Said once, when the pass is done, rather than after every slice: the
    /// canvas places for itself whatever it draws, and a slice moving folders
    /// nobody is looking at is no reason to draw the whole picture again.
    /// </summary>
    private bool _sortMovedCanvas;

    /// <summary>
    /// Time spent placing folders on demand - for the canvas's picture, a
    /// click, a key - since the last slice of the background pass.  The next
    /// slice takes it off its own allowance, so a frame that had to place a
    /// lot for the picture is not given a whole slice on top.
    /// </summary>
    private long _placedOnDemandTicks;

    /// <summary>
    /// Whether reads look up their files' type names before they come back,
    /// which is while any folder is ordered by type: read by the reading threads.
    /// </summary>
    private volatile bool _warmTypeNames;

    // Scratch for ordering one folder's files by type, reused from folder to
    // folder: placing a tree is tens of thousands of folders, each a handful
    // of kinds, and none of it needs to become garbage.
    private readonly List<string> _typeKinds = [];
    private readonly Dictionary<string, int> _typeSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Folders the user hid from the canvas, with everything under them.</summary>
    public void SetUserHidden(IEnumerable<string> paths)
    {
        _userHidden.Clear();
        foreach (var path in paths)
        {
            _userHidden.Add(Key(path));
        }

        RebuildFilterParents();
        RefilterAll();
    }

    public bool IsUserHidden(string path) => _userHidden.Contains(Key(path));

    /// <summary>
    /// Keeps every folder on the way to these paths on the canvas even when it
    /// is hidden - a marked folder inside AppData still needs a place to be.
    /// </summary>
    public void ForceVisible(IEnumerable<string> paths)
    {
        // Only a folder already read, one of whose children was filtered out
        // and now is not, needs placing again.  A step not read yet needs
        // nothing: its parent consults these sets when it is read.
        var parents = new HashSet<NestedFolder>();
        foreach (var path in paths)
        {
            foreach (var step in Chain(path))
            {
                if (!_forcedVisible.Add(step))
                {
                    continue;
                }

                if (Path.GetDirectoryName(step) is not { Length: > 0 } directory)
                {
                    continue;
                }

                _filterParents.Add(Key(directory));
                if (Find(directory) is not { IsLoaded: true } holder)
                {
                    continue;
                }

                var name = Path.GetFileName(step);
                var child = FindChild(holder, name);
                var isFilteredFolder = child is { Index: < 0 };
                var mayBeHiddenFile = child is null && !_includeHidden && holder.HiddenFileCount > 0;
                if (isFilteredFolder || mayBeHiddenFile)
                {
                    parents.Add(holder);
                }
            }
        }

        foreach (var parent in parents)
        {
            ApplyVisibleChildren(parent);
        }

        if (parents.Count > 0)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Orders every folder without an order of its own by <paramref name="sort"/>
    /// from now on: the default of <see cref="Orders"/>, placed as any change
    /// that can reach every folder is (see <see cref="OnOrdersChanged"/>).
    /// </summary>
    public void SetSort(ItemSort sort) => _orders.SetDefault(sort);

    /// <summary>The order a folder is placed in: its own, or the default; This PC's drives always keep theirs.</summary>
    public ItemSort SortOf(NestedFolder folder) =>
        folder.IsComputer ? ItemSort.Default : _orders.HasFolderOrders ? _orders.SortOf(folder.FullPath) : _orders.Default;

    /// <summary>
    /// Whether a folder's grids fill a column at a time.  This PC's drives
    /// always go along a row: they are the first row of cells, however the
    /// folders inside them are read.
    /// </summary>
    private bool DownFirstIn(NestedFolder folder) => !folder.IsComputer && _orders.DownFirst;

    /// <summary>
    /// An order changed.  One folder's own (<paramref name="path"/>): that
    /// folder is placed again here and now, if it has been read - it is the
    /// one being looked at, and it is one folder - and every other folder
    /// finds on its next look that it is still placed in its order.  Anything
    /// else: nothing is placed again here; <see cref="SortChanged"/> is
    /// raised, and a background pass starts that re-places every folder read
    /// so far whose order is not the one it was placed in, shallowest first,
    /// a few milliseconds at a time, and raises <see cref="Changed"/> once at
    /// the end if it moved something on the canvas.  Whatever is drawn or
    /// looked at before the pass gets to it is placed on the spot by
    /// <see cref="EnsureLayout"/>.  With no <see cref="PostBackground"/> - a
    /// thread with nothing to post to - the whole pass runs here, straight
    /// after the event.
    /// </summary>
    private void OnOrdersChanged(string? path)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            TakeOrderChange(path);
        }
        finally
        {
            LastOrderChangeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    /// <summary>
    /// What the last change of order cost here, the canvas's part included -
    /// it notes what it is looking at on <see cref="SortChanged"/> - and
    /// nothing of the rest of the window's.  For the bench, which times a
    /// click on a header as a whole and needs the canvas's share of it apart.
    /// </summary>
    internal double LastOrderChangeMilliseconds { get; private set; }

    private void TakeOrderChange(string? path)
    {
        _warmTypeNames = _orders.Uses(SortColumn.Type);
        if (_disposed)
        {
            return;
        }

        if (path is not null)
        {
            // Not read yet, or not in the tree at all: it is placed in its
            // order when it is read.  Placed in that order already - its own
            // order set to what it showed anyway - nothing moves.
            if (Find(path) is not { LayoutSortGeneration: >= 0 } folder
                || folder.PlacedSort == SortOf(folder) && folder.PlacedDownFirst == DownFirstIn(folder) && folder.PlacedShowFiles == _showFiles)
            {
                return;
            }

            // A new generation all the same: whatever keeps something by the
            // order the tree is in - the canvas's list of matches, say -
            // knows to look again.  Every other folder's next look finds it
            // placed in its own order and only moves its stamp on.
            SortGeneration++;
            SortChanged?.Invoke();
            EnsureLayout(folder);
            RaiseChanged();
            return;
        }

        SortGeneration++;

        // A pass already under way starts again from the top; a slice it has
        // already queued simply carries on with the new list.  Queued before
        // the event, so whoever handles it already sees the tree sorting.
        // What the last picture asked to have placed first was asked for the
        // order before; the next picture asks again.
        _sortSweep.Clear();
        _sortFirst.Clear();
        _sortMovedCanvas = false;
        _placedOnDemandTicks = 0;
        _sortSweep.Enqueue(Root);

        SortChanged?.Invoke();
        ScheduleSortSlice();
    }

    /// <summary>
    /// Places the folder's sub-folders and files for the current order if they
    /// are not yet; costs a comparison of two numbers when they are.  Anything
    /// that reads a folder's <see cref="NestedFolder.Children"/>, grids or
    /// <see cref="NestedFolder.Files"/>, or its children's places, calls this
    /// first.  A folder never placed - one not read yet - is left alone: it
    /// has nothing to order.
    /// </summary>
    public void EnsureLayout(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp == SortGeneration || stamp < 0)
        {
            return;
        }

        // Timed, because the background pass shares its allowance with
        // whatever had to be placed on demand since its last slice.
        var started = Stopwatch.GetTimestamp();
        if (Relayout(folder))
        {
            _placedOnDemandTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    /// <summary>
    /// Has the next slice of the background pass place these folders before
    /// anything else: the canvas drew them in their previous order, its
    /// frame's allowance for placing spent.  Replaces what the picture before
    /// asked for - whatever of that is still out of date and on screen is in
    /// this list again.
    /// </summary>
    internal void PlaceFirst(List<NestedFolder> folders)
    {
        _sortFirst.Clear();
        if (!IsSorting)
        {
            return;
        }

        foreach (var folder in folders)
        {
            _sortFirst.Enqueue(folder);
        }
    }

    /// <summary>
    /// <see cref="EnsureLayout"/> for every folder from the top down to this
    /// one: what a camera fixed to a folder needs before it works out where
    /// that folder's ancestors, and their other children, are.
    /// </summary>
    public void EnsurePathLayout(NestedFolder folder)
    {
        // Nearly always every folder on the way is up to date already, and
        // this is a walk of number comparisons that allocates nothing.
        var stale = false;
        for (var current = folder; current is not null; current = current.Parent)
        {
            if (current.LayoutSortGeneration >= 0 && current.LayoutSortGeneration != SortGeneration)
            {
                stale = true;
                break;
            }
        }

        if (!stale)
        {
            return;
        }

        var path = new List<NestedFolder>(folder.Depth + 1);
        for (var current = folder; current is not null; current = current.Parent)
        {
            path.Add(current);
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            EnsureLayout(path[index]);
        }
    }

    /// <summary>
    /// Finishes the background pass after a change of order here and now, for
    /// a test or a snapshot that wants every folder placed before it looks.
    /// </summary>
    public void FlushSortWork()
    {
        if (_sortSweep.Count == 0)
        {
            return;
        }

        SweepSort(budgetTicks: 0);
        FinishSortPass();
    }

    /// <summary>Completes once the background pass after a change of order has visited every folder.</summary>
    public Task WhenSortIdleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSorting)
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (_sortIdleWaiters ??= []).Add(waiter);
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return waiter.Task;
    }

    // ---- filtering and placing ---------------------------------------------

    private void RefilterAll()
    {
        var stack = new Stack<NestedFolder>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();

            // Whatever its state - being read again, or failed on a re-read -
            // a folder that has contents gets the rules applied to them.
            if (!folder.IsComputer && folder.AllChildren.Length == 0 && folder.AllFiles.Length == 0)
            {
                continue;
            }

            ApplyVisibleChildren(folder);
            foreach (var child in folder.AllChildren)
            {
                stack.Push(child);
            }
        }

        RaiseChanged();
    }

    private void RebuildFilterParents()
    {
        _filterParents.Clear();
        foreach (var path in _userHidden.Concat(_forcedVisible))
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                _filterParents.Add(Key(directory));
            }
        }
    }

    /// <summary>
    /// Decides which children are cells and where each one goes, in the
    /// folder's order (<see cref="SortOf"/>), and stamps the folder as placed
    /// for it.  Under names from A this is exactly what it always was: the
    /// listing's own order, and the listing itself as the files whenever none
    /// is hidden.
    /// </summary>
    private void ApplyVisibleChildren(NestedFolder folder)
    {
        var sort = SortOf(folder);
        var downFirst = DownFirstIn(folder);

        // Only folders with a hidden or forced child look paths up; the rest
        // decide by attribute alone, which is what keeps placing a folder of
        // fifty thousand sub-folders cheap.
        var hasRules = !folder.IsComputer && _filterParents.Contains(folder.FullPath);
        var all = folder.AllChildren;
        var visible = _visibleChildren;
        visible.Clear();
        foreach (var child in all)
        {
            // "Hide from canvas" is the user's own word and outranks a folder
            // being asked for by name; being asked for outranks only the
            // hidden attribute.
            bool shown;
            if (hasRules && _userHidden.Contains(child.FullPath))
            {
                shown = false;
            }
            else if (hasRules && _forcedVisible.Contains(child.FullPath))
            {
                shown = true;
            }
            else
            {
                shown = _includeHidden || !child.IsHidden;
            }

            if (shown)
            {
                visible.Add(child);
            }
            else
            {
                child.Index = -1;
            }
        }

        // A hidden file someone marked or searched for is shown like a hidden
        // folder on the way to one: asking for it by name outranks the filter.
        bool IsShown(NestedFile file) => !file.IsHidden || hasRules && _forcedVisible.Contains(folder.PathOf(file));

        // This PC's drives keep the order they were given in; everywhere else
        // the chosen order applies.  Names from A is the order the listings
        // already have, so it asks for nothing at all.
        var listingOrder = sort.IsDefault || folder.IsComputer;
        IReadOnlyList<NestedFile> files;
        if (!_showFiles)
        {
            // The Files layer is off: the sub-folders are still put in the
            // order, and no file is shown or ordered.
            if (!listingOrder)
            {
                OrderFolders(visible, sort);
            }

            files = [];
        }
        else if (!listingOrder)
        {
            // Only which cell and which tile each one gets changes: the grids
            // depend on how many there are, not on which is which.
            OrderFolders(visible, sort);
            files = OrderFiles(
                folder.AllFiles,
                _includeHidden || folder.HiddenFileCount == 0 ? null : ShownIndices(folder.AllFiles, IsShown),
                sort);
        }
        else if (_includeHidden || folder.HiddenFileCount == 0)
        {
            files = folder.AllFiles;
        }
        else
        {
            NestedFile[] shown = [.. folder.AllFiles.Where(IsShown)];
            files = shown;
        }

        // Sub-folders take the top of the cell and files what is left under
        // them: the folder grid is fitted into its share first, then the files
        // get every bit of height the folders did not actually use.
        var (folderHeight, _) = NestedLayout.Split(visible.Count, files.Count);
        var grid = NestedLayout.GridFor(visible.Count, folderHeight, downFirst);
        for (var index = 0; index < visible.Count; index++)
        {
            var child = visible[index];
            var (x, y) = grid.Origin(index);
            child.Index = index;
            child.OffsetX = x;
            child.OffsetY = y;
            child.Scale = grid.Scale;
        }

        var used = grid.IsEmpty ? 0 : grid.Height + NestedLayout.ZoneGap;
        folder.Grid = grid;
        folder.Children = ShownChildren(folder, all, listingOrder);
        visible.Clear();
        folder.Files = files;
        folder.FileGrid = NestedLayout.FileGridFor(
            files.Count,
            NestedLayout.HeaderHeight + used,
            NestedLayout.ContentHeight - used,
            downFirst);
        folder.UnlistedFileCount = Math.Max(0, folder.FileCount - folder.AllFiles.Length);
        folder.LayoutSortGeneration = SortGeneration;
        folder.PlacedSort = sort;
        folder.PlacedDownFirst = downFirst;
        folder.PlacedShowFiles = _showFiles;
    }

    /// <summary>
    /// The sub-folders <see cref="ApplyVisibleChildren"/> gathers a folder's
    /// shown children in: one list, reused for every folder and emptied once
    /// placed, so placing a tree makes no list per folder.
    /// </summary>
    private readonly List<NestedFolder> _visibleChildren = [];

    /// <summary>
    /// A folder's shown sub-folders, as gathered in <see cref="_visibleChildren"/>,
    /// kept without a new list wherever one is not needed: none shown is the
    /// one empty list, all of them in the listing's own order is the listing
    /// itself, and the same ones in the same order as before - a folder read
    /// again with nothing new in it, or placed again for an order that leaves
    /// its sub-folders where they were - is the list it had.  Placing a tree
    /// again is mostly such folders, and every list made is one more for the
    /// garbage collector to carry.
    /// </summary>
    private IReadOnlyList<NestedFolder> ShownChildren(NestedFolder folder, NestedFolder[] all, bool listingOrder)
    {
        var visible = _visibleChildren;
        if (visible.Count == 0)
        {
            return [];
        }

        if (listingOrder && visible.Count == all.Length)
        {
            return all;
        }

        var current = folder.Children;
        if (current.Count == visible.Count)
        {
            var same = true;
            for (var index = 0; index < visible.Count && same; index++)
            {
                same = ReferenceEquals(current[index], visible[index]);
            }

            if (same)
            {
                return current;
            }
        }

        return visible.ToArray();
    }

    // ---- ordering ------------------------------------------------------------

    /// <summary>
    /// The default poster for the background pass: the creating thread's
    /// dispatcher, below input and rendering, when that thread is running
    /// one; otherwise its synchronisation context; otherwise none, and the
    /// pass runs at once.  A dispatcher only counts when it is the context
    /// too - a thread can have a dispatcher that nobody runs, and a slice
    /// queued there would wait for ever.
    /// </summary>
    private static Action<Action>? DefaultPostBackground()
    {
        var context = SynchronizationContext.Current;
        if (context is DispatcherSynchronizationContext && Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher)
        {
            return action => dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        }

        if (context is not null)
        {
            return action => context.Post(static state => ((Action)state!).Invoke(), action);
        }

        return null;
    }

    /// <summary>
    /// Places a folder for its current order if it is not yet; true when it
    /// actually had to be placed again, rather than being up to date or only
    /// needing its stamp moved on.
    /// </summary>
    private bool Relayout(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp == SortGeneration || stamp < 0)
        {
            return false;
        }

        // Placed in the order it has now - another folder was sorted, or
        // its own order came back - or with too little in it for any order
        // to differ - one sub-folder and one file go where they go - or This
        // PC, whose drives are never reordered: the places are right, only
        // the stamp is old.  Files shown or hidden since it was placed move
        // everything in a folder that has any, whatever its order.
        var sort = SortOf(folder);
        var downFirst = DownFirstIn(folder);
        var filesAsPlaced = folder.PlacedShowFiles == _showFiles || folder.AllFiles.Length == 0;
        if (filesAsPlaced
            && (folder.PlacedSort == sort && folder.PlacedDownFirst == downFirst
                || folder.IsComputer
                || folder.Children.Count < 2 && folder.Files.Count < 2))
        {
            folder.LayoutSortGeneration = SortGeneration;
            folder.PlacedSort = sort;
            folder.PlacedDownFirst = downFirst;
            folder.PlacedShowFiles = _showFiles;
            return false;
        }

        ApplyVisibleChildren(folder);
        return true;
    }

    private void ScheduleSortSlice()
    {
        if (_sortSlicePosted || _sortSweep.Count == 0)
        {
            return;
        }

        if (PostBackground is not { } post)
        {
            FlushSortWork();
            return;
        }

        _sortSlicePosted = true;
        post(RunSortSlice);
    }

    /// <summary>
    /// One slice of the background pass: a few milliseconds of folders, less
    /// whatever was placed on demand since the slice before, then the rest
    /// queued again.  A slice with next to nothing left of its allowance
    /// leaves its turn to the next frame.
    /// </summary>
    private void RunSortSlice()
    {
        _sortSlicePosted = false;
        if (_disposed)
        {
            _sortSweep.Clear();
            _sortFirst.Clear();
            CompleteSortIdle();
            return;
        }

        // Flushed meanwhile: nothing left for this slice to do.
        if (_sortSweep.Count == 0)
        {
            return;
        }

        // Taken, not just read: the placing it counts is charged to this
        // slice alone, and the next one starts again from what happens after.
        var budget = SortSliceTicks - _placedOnDemandTicks;
        _placedOnDemandTicks = 0;
        if (budget >= MinimumSliceTicks)
        {
            SweepSort(budget);
        }

        if (_sortSweep.Count > 0)
        {
            ScheduleSortSlice();
        }
        else
        {
            FinishSortPass();
        }
    }

    /// <summary>
    /// The pass has visited every folder: the one announcement it makes, if
    /// it moved anything on the canvas, and whoever waits for it let go.
    /// </summary>
    private void FinishSortPass()
    {
        _sortFirst.Clear();
        if (_sortMovedCanvas)
        {
            _sortMovedCanvas = false;
            RaiseChanged();
        }

        CompleteSortIdle();
    }

    /// <summary>
    /// Visits folders of the background pass until the budget is spent (none:
    /// until there are none left): first those the canvas asked to have placed
    /// first (see <see cref="PlaceFirst"/>), then the tree shallowest first.
    /// Notes whether one it placed again is on the canvas, for the
    /// announcement at the end of the pass.
    /// </summary>
    private void SweepSort(long budgetTicks)
    {
        var started = Stopwatch.GetTimestamp();
        bool Spent() => budgetTicks > 0 && Stopwatch.GetTimestamp() - started >= budgetTicks;

        // Only placed here, not walked from: the pass reaches their children
        // in its own time, and anything of theirs on screen is on the list too.
        while (_sortFirst.TryDequeue(out var first))
        {
            if (!first.IsForgotten && Relayout(first))
            {
                _sortMovedCanvas = true;
            }

            if (Spent())
            {
                return;
            }
        }

        while (_sortSweep.TryDequeue(out var folder))
        {
            // Dropped by a refresh since it was queued: nobody can reach it.
            if (folder.IsForgotten)
            {
                continue;
            }

            if (Relayout(folder) && !_sortMovedCanvas)
            {
                _sortMovedCanvas = IsOnCanvas(folder);
            }

            // Hidden ones too: showing hidden items should not find them in
            // an old order.  A child never placed was never read, and neither
            // was anything below it, so it has nothing to visit.
            foreach (var child in folder.AllChildren)
            {
                if (child.LayoutSortGeneration >= 0)
                {
                    _sortSweep.Enqueue(child);
                }
            }

            if (Spent())
            {
                break;
            }
        }
    }

    private void CompleteSortIdle()
    {
        if (_sortIdleWaiters is not { Count: > 0 } waiters)
        {
            return;
        }

        _sortIdleWaiters = null;
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Puts the shown sub-folders in <paramref name="sort"/>.  Only names from
    /// Z and dates move folders: a folder has no size and every folder has the
    /// same type, so under those two they tie, and ties stay in name order.
    /// </summary>
    private static void OrderFolders(List<NestedFolder> visible, ItemSort sort)
    {
        var count = visible.Count;
        if (count < 2)
        {
            return;
        }

        if (sort.Column == SortColumn.Name)
        {
            // Only names from Z get here, and they are the listing backwards.
            visible.Reverse();
            return;
        }

        if (sort.Column != SortColumn.Modified)
        {
            return;
        }

        var keys = ArrayPool<long>.Shared.Rent(count);
        var order = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                keys[index] = KeyOf(visible[index].ModifiedTicks, sort.Descending);
            }

            if (!SortByKeys(keys, order, count))
            {
                return;
            }

            var before = ArrayPool<NestedFolder>.Shared.Rent(count);
            visible.CopyTo(before);
            for (var position = 0; position < count; position++)
            {
                visible[position] = before[order[position]];
            }

            // Cleared on the way back: a pool holding folders would keep a
            // tree that was let go of alive.
            ArrayPool<NestedFolder>.Shared.Return(before, clearArray: true);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>The indices among <paramref name="files"/> of the ones shown.</summary>
    private static int[] ShownIndices(NestedFile[] files, Func<NestedFile, bool> isShown)
    {
        var buffer = ArrayPool<int>.Shared.Rent(files.Length);
        var count = 0;
        for (var index = 0; index < files.Length; index++)
        {
            if (isShown(files[index]))
            {
                buffer[count++] = index;
            }
        }

        var shown = buffer.AsSpan(0, count).ToArray();
        ArrayPool<int>.Shared.Return(buffer);
        return shown;
    }

    /// <summary>
    /// The shown files in <paramref name="sort"/>: the listing itself when every
    /// file is shown and the order comes out as name order anyway - every
    /// file the same type, or the same date - and otherwise the listing with
    /// the order to walk it in.  An index per file rather than a copy of every
    /// file: re-placing a whole tree makes one of these per folder, and the
    /// garbage collector has a tenth as much to carry.
    /// </summary>
    /// <param name="all">Every file the folder read, in name order.</param>
    /// <param name="shownFrom">The indices of the shown ones, in name order; null when all of them are.</param>
    private IReadOnlyList<NestedFile> OrderFiles(NestedFile[] all, int[]? shownFrom, ItemSort sort)
    {
        var count = shownFrom?.Length ?? all.Length;
        int[]? order = null;
        if (count >= 2)
        {
            switch (sort.Column)
            {
                case SortColumn.Name:
                    // Only names from Z get here, and they are the listing backwards.
                    order = new int[count];
                    for (var position = 0; position < count; position++)
                    {
                        order[position] = count - 1 - position;
                    }

                    break;

                case SortColumn.Modified:
                case SortColumn.Size:
                    var byDate = sort.Column == SortColumn.Modified;
                    var keys = ArrayPool<long>.Shared.Rent(count);
                    order = new int[count];
                    for (var index = 0; index < count; index++)
                    {
                        var file = all[shownFrom is null ? index : shownFrom[index]];
                        keys[index] = KeyOf(byDate ? file.ModifiedTicks : file.Length, sort.Descending);
                    }

                    if (!SortByKeys(keys, order, count))
                    {
                        order = null;
                    }

                    ArrayPool<long>.Shared.Return(keys);
                    break;

                default:
                    order = TypeOrder(all, shownFrom, count, sort.Descending);
                    break;
            }
        }

        if (order is null)
        {
            // Name order after all: the listing, or the part of it shown.
            return shownFrom is null ? all : new NestedFileOrder(all, shownFrom, isNameOrder: true);
        }

        if (shownFrom is not null)
        {
            // From places among the shown files to places in the listing.
            for (var position = 0; position < count; position++)
            {
                order[position] = shownFrom[order[position]];
            }
        }

        return new NestedFileOrder(all, order, isNameOrder: false);
    }

    /// <summary>A key that sorts the chosen way round: largest first by flipping every bit, which cannot overflow.</summary>
    private static long KeyOf(long value, bool descending) => descending ? ~value : value;

    /// <summary>
    /// Fills <paramref name="order"/> with the first <paramref name="count"/>
    /// indices smallest key first, equal keys in index order, which is name
    /// order; false when that is the order they were in already.  The keys
    /// are sorted in place.
    /// </summary>
    private static bool SortByKeys(long[] keys, int[] order, int count)
    {
        for (var index = 0; index < count; index++)
        {
            order[index] = index;
        }

        // Sorting plain numbers with their indices riding along never calls a
        // comparer; that sort does not keep ties in order, so each run of
        // equal keys has its indices put back in order afterwards.
        Array.Sort(keys, order, 0, count);
        var start = 0;
        while (start < count)
        {
            var end = start + 1;
            while (end < count && keys[end] == keys[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                Array.Sort(order, start, end - start);
            }

            start = end;
        }

        return !IsIdentity(order, count);
    }

    /// <summary>
    /// The order of the shown files by type name, ties by name, as places
    /// among the shown files; null when that is name order.  Each kind of file
    /// is named and ranked once - a folder of fifty thousand files is a
    /// handful of kinds - and then the files are sorted by rank and index
    /// packed into one number, so no string is compared per file at all.
    /// </summary>
    private int[]? TypeOrder(NestedFile[] all, int[]? shownFrom, int count, bool descending)
    {
        var kinds = _typeKinds;
        var slots = _typeSlots;
        var slotOf = ArrayPool<int>.Shared.Rent(count);
        try
        {
            // Extensions are shared strings, so a kind is found by reference,
            // and a run of the same kind skips even that.
            string? lastExtension = null;
            var lastSlot = -1;
            for (var index = 0; index < count; index++)
            {
                var extension = all[shownFrom is null ? index : shownFrom[index]].Extension ?? string.Empty;
                if (!ReferenceEquals(extension, lastExtension))
                {
                    ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(slots, extension, out var exists);
                    if (!exists)
                    {
                        slot = kinds.Count;
                        kinds.Add(extension);
                    }

                    lastExtension = extension;
                    lastSlot = slot;
                }

                slotOf[index] = lastSlot;
            }

            if (kinds.Count < 2)
            {
                return null;
            }

            // Two extensions can share a name - "jpg" and "jpeg" are both a
            // JPEG image - and then they share a rank too.
            var names = new string[kinds.Count];
            var byName = new int[kinds.Count];
            for (var kind = 0; kind < kinds.Count; kind++)
            {
                names[kind] = FileTypeNames.Of(kinds[kind]);
                byName[kind] = kind;
            }

            var comparer = StringComparer.CurrentCultureIgnoreCase;
            Array.Sort(names, byName, comparer);
            var ranks = new int[kinds.Count];
            var rank = 0;
            for (var position = 0; position < names.Length; position++)
            {
                if (position > 0 && comparer.Compare(names[position - 1], names[position]) != 0)
                {
                    rank++;
                }

                ranks[byName[position]] = rank;
            }

            if (rank == 0)
            {
                return null;
            }

            var keys = ArrayPool<long>.Shared.Rent(count);
            for (var index = 0; index < count; index++)
            {
                var kindRank = ranks[slotOf[index]];
                keys[index] = ((long)(descending ? rank - kindRank : kindRank) << 32) | (uint)index;
            }

            Array.Sort(keys, 0, count);
            var order = new int[count];
            for (var position = 0; position < count; position++)
            {
                order[position] = (int)(keys[position] & 0xFFFF_FFFF);
            }

            ArrayPool<long>.Shared.Return(keys);
            return IsIdentity(order, count) ? null : order;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slotOf);
            kinds.Clear();
            slots.Clear();
        }
    }

    private static bool IsIdentity(int[] order, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (order[index] != index)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A file by name among files in name order: a binary search, and a plain
    /// scan for a name culture order and the file system disagree about.
    /// Culture order calls some different names one - "café.txt" written
    /// with one character for the é and with two, which a folder can hold
    /// side by side - so a name it finds is checked, and its neighbours equal
    /// to it looked through for the file that really has the name.
    /// </summary>
    private static int SearchFiles(IReadOnlyList<NestedFile> files, string name)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var low = 0;
        var high = files.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var order = comparer.Compare(files[middle].Name, name);
            if (order == 0)
            {
                if (string.Equals(files[middle].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return middle;
                }

                for (var index = middle - 1; index >= 0 && comparer.Compare(files[index].Name, name) == 0; index--)
                {
                    if (string.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                for (var index = middle + 1; index < files.Count && comparer.Compare(files[index].Name, name) == 0; index++)
                {
                    if (string.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                break;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (var index = 0; index < files.Count; index++)
        {
            if (string.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
