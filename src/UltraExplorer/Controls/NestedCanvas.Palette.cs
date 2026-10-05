using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// Colours: a cell's from its hue, its depth, its mark and the name filter,
// a file tile's from its extension - and the name filter itself, what it
// matches and what it lights or fades, which is what most of a palette
// change is about.
public sealed partial class NestedCanvas
{
    /// <summary>
    /// Where a colour and a note for a path come from.  A lookup that is a
    /// <see cref="FolderMarkService"/>'s own <see cref="FolderMarkService.Get(string)"/>
    /// brings the service with it (<see cref="Marks"/>), and the canvas then
    /// asks the service by folder rather than by path.
    /// </summary>
    public Func<string, FolderMark>? MarkLookup
    {
        get => _markLookup;
        set
        {
            _markLookup = value;
            _markService = value?.Target as FolderMarkService;
            InvalidateMarks();
        }
    }

    /// <summary>
    /// The marks themselves, when they come from a <see cref="FolderMarkService"/>:
    /// then a cell's mark is looked up by its folder's path as it is, with
    /// nothing normalised, and the files of a folder that holds no mark -
    /// nearly all of them - are not looked up at all (see
    /// <see cref="FolderMarkService.MarkedFolders"/>).  Setting it sets
    /// <see cref="MarkLookup"/> to the service's lookup too.
    /// </summary>
    public FolderMarkService? Marks
    {
        get => _markService;
        set
        {
            _markLookup = value is null ? null : value.Get;
            _markService = value;
            InvalidateMarks();
        }
    }

    private FolderMarkService? _markService;

    /// <summary>A folder's own mark, from the service by folder when there is one, else by its path.</summary>
    private FolderMark MarkOfFolder(NestedFolder folder) =>
        _markService is { } marks ? marks.Get(folder) : _markLookup?.Invoke(folder.FullPath) ?? FolderMark.None;

    /// <summary>Marks changed: every cell's colours are worked out again on its next draw.</summary>
    public void InvalidateMarks()
    {
        _paletteStamp++;
        RequestFrame(Layers.All);
    }

    // ---- filter ---------------------------------------------------------------------

    /// <summary>
    /// Narrows the canvas to names: what matches is lit, what holds a match
    /// keeps its colour so the way to it shows, and everything else fades.
    /// Nothing moves - a filter that re-laid the drive out would lose the one
    /// thing a spatial view is for, knowing where things are.
    ///
    /// Plain text matches anywhere in a name, ignoring case; * and ? make it a
    /// wildcard pattern for the whole name (*.png); several patterns can be
    /// separated by ';'.  Only what has been read can match: a folder not read
    /// yet stays half faded until it is, and then it is judged like the rest.
    ///
    /// The first <see cref="FilterNamesAtOnce"/> names read are judged before
    /// this returns - all of them, but in a window that has read a few big
    /// drives - and the rest a slice at a time between the frames and the
    /// input (<see cref="JudgeFilterOn"/>), lighting folders and adding
    /// matches as it goes.  Judged in one go, a filter over a tree read for
    /// hours held the window still for most of a second on every key.
    /// </summary>
    public void SetFilter(string? text)
    {
        var matcher = CompileFilter(text);
        if (matcher is null && _filter is null)
        {
            return;
        }

        _filter = matcher;
        _filterText = matcher is null ? string.Empty : text!.Trim();
        _filterStamp++;
        _filterCursor = -1;
        _filterMatches.Clear();
        _filterMatchSet.Clear();
        _filterJudging = null;
        if (matcher is not null && _tree is not null)
        {
            var judging = new FilterJudging(_tree, _tree.IsSorting ? -1 : _tree.SortGeneration);
            judging.Steps.Add(EnterForFilter(_tree.Root));

            // Counted in names rather than time, so a tree of any but the
            // biggest size is judged before this returns however busy the
            // machine.  A thread whose dispatcher nobody runs has nobody to
            // judge the rest later: it judges everything now, as it always did.
            var budget = SynchronizationContext.Current is DispatcherSynchronizationContext
                ? new FrameBudget { Deadline = long.MaxValue, Items = FilterNamesAtOnce }
                : FrameBudget.Unlimited;
            if (JudgeFilter(judging, ref budget))
            {
                FilterJudged(judging);
            }
            else
            {
                _filterJudging = judging;
                _filterOrderGeneration = judging.OrderGeneration;
                (_filterDriver ??= new DispatcherFrameDriver(Dispatcher, JudgeFilterOn, () => _filterJudging is not null, DispatcherPriority.Background)).Wake();
            }
        }

        _paletteStamp++;
        RequestFrame(Layers.All);
        RaiseFilterChanged();
    }

    /// <summary>How many names <see cref="SetFilter"/> judges before it leaves the rest to slices between the frames: about ten milliseconds' worth.</summary>
    private const int FilterNamesAtOnce = 250_000;

    /// <summary>Names judged between two looks at the budget: a folder of files is judged whole, however many it holds.</summary>
    private const int FilterNamesPerCheck = 512;

    /// <summary>
    /// A filter's judging of the whole tree, while it is under way: the
    /// folders it has gone into and not yet come out of, outermost first, and
    /// the order the tree was placed for when it began - or -1 when the tree
    /// was still placing folders for a new one.
    /// </summary>
    private sealed class FilterJudging(NestedTree tree, int orderGeneration)
    {
        public NestedTree Tree { get; } = tree;

        public int OrderGeneration { get; } = orderGeneration;

        public List<FilterStep> Steps { get; } = [];
    }

    /// <summary>
    /// A folder the judging has gone into: how its own name and its files
    /// were judged on the way in, and the sub-folders it is going through -
    /// the ones it had then - with how many of them it has gone into so far.
    /// </summary>
    private struct FilterStep(NestedFolder folder, IReadOnlyList<NestedFolder> children, int state)
    {
        public readonly NestedFolder Folder = folder;
        public readonly IReadOnlyList<NestedFolder> Children = children;
        public readonly int State = state;
        public int Next;
    }

    /// <summary>The judging of the filter still under way, or null; see <see cref="SetFilter"/>.</summary>
    private FilterJudging? _filterJudging;

    /// <summary>What judges the rest of a filter a slice at a time: the dispatcher, below input and rendering.</summary>
    private DispatcherFrameDriver? _filterDriver;

    /// <summary>
    /// Judges on from where <paramref name="judging"/> left off, until the
    /// whole tree is judged - true - or <paramref name="budget"/> is spent.
    /// The walk <see cref="Evaluate"/> makes, kept on a list of its own rather
    /// than on the stack so it can stop anywhere: each folder's own name and
    /// then its files on the way in, so the matches are listed in the same
    /// order, and whether it holds a match on the way out.
    /// </summary>
    private bool JudgeFilter(FilterJudging judging, ref FrameBudget budget)
    {
        var steps = judging.Steps;
        var names = 0;
        while (steps.Count > 0)
        {
            if (names >= FilterNamesPerCheck)
            {
                budget.Take(names);
                names = 0;
                if (budget.Spent)
                {
                    return false;
                }
            }

            ref var step = ref CollectionsMarshal.AsSpan(steps)[^1];
            if (step.Next < step.Children.Count)
            {
                // A sub-folder read since the judging began was judged, with
                // everything below it, there and then; one gone or hidden
                // since is not on the canvas to be judged.
                var child = step.Children[step.Next++];
                if (child.FilterStamp != _filterStamp && child.Index >= 0)
                {
                    steps.Add(EnterForFilter(child));
                    names += 1 + child.Files.Count;
                }

                continue;
            }

            var (folder, state) = (step.Folder, step.State);
            steps.RemoveAt(steps.Count - 1);
            if (folder.FilterStamp != _filterStamp)
            {
                LeaveForFilter(folder, state);
            }

            names += 1 + folder.Children.Count;
        }

        return true;
    }

    /// <summary>The judging goes into <paramref name="folder"/>: its own name and its files are judged, and matches listed, as <see cref="Evaluate"/> does first.</summary>
    private FilterStep EnterForFilter(NestedFolder folder)
    {
        var matcher = _filter!;
        var state = 0;
        if (!folder.IsComputer && matcher(folder.Name))
        {
            state |= FilterSelf;
            AddMatch(folder.FullPath);
        }

        foreach (var file in folder.Files)
        {
            if (matcher(file.Name))
            {
                state |= FilterInside;
                AddMatch(folder.PathOf(file));
            }
        }

        return new FilterStep(folder, folder.Children, state);
    }

    /// <summary>
    /// The judging comes out of <paramref name="folder"/>, every sub-folder
    /// judged: whether it holds a match - asked of what its sub-folders hold
    /// now, which reads since may have changed - and its colours worked out
    /// again if that changes how it is drawn, as <see cref="Evaluate"/> ends.
    /// </summary>
    private void LeaveForFilter(NestedFolder folder, int state)
    {
        if (AnyChildMatches(folder))
        {
            state |= FilterInside;
        }

        if (!folder.IsComputer && !folder.IsLoaded)
        {
            state |= FilterUnknown;
        }

        if (FilterStateOf(folder) != state)
        {
            folder.PaletteStamp = PaletteOutOfDate;
        }

        folder.FilterStamp = _filterStamp;
        folder.FilterState = state;
    }

    /// <summary>Whether one of a folder's sub-folders matches or holds a match, as the filter last judged it.</summary>
    private bool AnyChildMatches(NestedFolder folder)
    {
        foreach (var child in folder.Children)
        {
            if ((FilterStateOf(child) & (FilterSelf | FilterInside)) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one of a folder's own files matches.</summary>
    private bool AnyFileMatches(NestedFolder folder)
    {
        var matcher = _filter!;
        foreach (var file in folder.Files)
        {
            if (matcher(file.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A slice of the judging under way, from the dispatcher between frames
    /// and input.  Reads applied since the last slice are taken account of
    /// first (<see cref="TrimFilterSteps"/>).  What it judged is drawn as
    /// folders read are, at loading's rate, and new matches are said once a
    /// frame; once the whole tree is judged, that is said too.
    /// </summary>
    private void JudgeFilterOn(ref FrameBudget budget)
    {
        if (_filterJudging is not { } judging)
        {
            return;
        }

        if (_filter is null || !ReferenceEquals(judging.Tree, _tree))
        {
            _filterJudging = null;
            return;
        }

        var before = _filterMatches.Count;
        TrimFilterSteps(judging);
        var done = JudgeFilter(judging, ref budget);
        if (done)
        {
            _filterJudging = null;
            FilterJudged(judging);
        }

        _loadDirtyEverywhere = true;
        HoldLoadRedraw(Layers.All);
        if (done || _filterMatches.Count != before)
        {
            RaiseFilterChangedWithFrame();
        }
    }

    /// <summary>
    /// Before a slice: what reads applied since the last one did to the
    /// folders the judging is inside.  One read again was judged whole there
    /// and then (<see cref="OnFolderLoadedForFilter"/>), and one gone or
    /// hidden since is not on the canvas: the judging comes out of either -
    /// and out of everything it had gone into below it - without judging it
    /// again.
    /// </summary>
    private void TrimFilterSteps(FilterJudging judging)
    {
        var steps = judging.Steps;
        for (var index = 0; index < steps.Count; index++)
        {
            var folder = steps[index].Folder;
            if (folder.FilterStamp == _filterStamp || folder.Parent is not null && folder.Index < 0)
            {
                steps.RemoveRange(index, steps.Count - index);
                return;
            }
        }
    }

    /// <summary>Whether <paramref name="folder"/> is one the judging under way has gone into and not yet come out of.</summary>
    private bool IsBeingJudged(NestedFolder folder)
    {
        if (_filterJudging is { } judging)
        {
            foreach (var step in judging.Steps)
            {
                if (ReferenceEquals(step.Folder, folder))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The whole tree is judged.  Gathered while the tree was still placing
    /// folders for a new order - or across a change of order - the list is in
    /// a mixture of orders: it is put in the current one when the placing is
    /// done.
    /// </summary>
    private void FilterJudged(FilterJudging judging)
    {
        var tree = judging.Tree;
        var placed = !tree.IsSorting && judging.OrderGeneration == tree.SortGeneration;
        _filterOrderGeneration = placed ? tree.SortGeneration : -1;
        if (!placed)
        {
            _ = ReorderFilterWhenPlacedAsync();
        }
    }

    /// <summary>A filter is still being judged, a slice at a time.</summary>
    partial void PendingFilterWork(ref bool pending)
    {
        if (_filterJudging is not null)
        {
            pending = true;
        }
    }

    /// <summary>
    /// Raised when the filter's matches changed: new text, or folders read
    /// since.  For folders read, once per frame however many were read in it
    /// (<see cref="FlushFrameEvents"/>).
    /// </summary>
    public event Action? FilterChanged;

    public bool IsFiltering => _filter is not null;

    /// <summary>
    /// Paths of everything matching the filter among what has been read, in
    /// walking order: each folder, then its files and sub-folders as the
    /// current order shows them, left to right and top to bottom.
    /// </summary>
    public IReadOnlyList<string> FilterMatches => _filterMatches;

    /// <summary>Which match the last step went to, or -1.</summary>
    public int FilterCursor => _filterCursor;

    /// <summary>
    /// Goes to the next match (or the previous one), selects it and flies to
    /// where it can be read.  False when nothing matches.  Next is next in
    /// the order on screen, even straight after the order changed.
    /// </summary>
    public bool GoToMatch(int direction)
    {
        // The order on screen is known only of what has been judged: a step
        // taken while the filter is still being judged judges the rest first.
        if (_filterJudging is not null)
        {
            var budget = FrameBudget.Unlimited;
            JudgeFilterOn(ref budget);
        }

        if (_filterMatches.Count == 0)
        {
            return false;
        }

        if (_tree is not null && _filterOrderGeneration != _tree.SortGeneration)
        {
            ReorderFilterMatches();
        }

        _filterCursor = ((_filterCursor < 0 && direction < 0 ? 0 : _filterCursor) + direction + _filterMatches.Count) % _filterMatches.Count;
        var path = _filterMatches[_filterCursor];
        if (Resolve(path) is not { } target)
        {
            return false;
        }

        MarkSelected(path);
        SelectRequested?.Invoke(path, false);
        FlyToReadable(target.Folder, target.FileIndex);
        RaiseFilterChanged();
        return true;
    }

    private static Func<string, bool>? CompileFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tests = new List<Func<string, bool>>(parts.Length);
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['*', '?']) >= 0)
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(part).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";

                // Without backtracking: "*a*a*a*a*a*b" against a long name
                // would otherwise try every way of splitting it, on the UI
                // thread, for every name read.  The pattern is only literals,
                // "." and ".*" between two anchors, all of which this engine
                // takes, in time that grows with the name alone.
                var regex = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
                tests.Add(regex.IsMatch);
            }
            else
            {
                var fragment = part;
                tests.Add(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (tests.Count == 0)
        {
            return null;
        }

        return tests.Count == 1 ? tests[0] : name => tests.Any(test => test(name));
    }

    /// <summary>
    /// Works out, below one folder, what matches and what holds a match.
    /// Returns whether anything in or under it matches.
    /// </summary>
    private bool Evaluate(NestedFolder folder)
    {
        var matcher = _filter!;
        var state = 0;
        if (!folder.IsComputer && matcher(folder.Name))
        {
            state |= FilterSelf;
            AddMatch(folder.FullPath);
        }

        foreach (var file in folder.Files)
        {
            if (matcher(file.Name))
            {
                state |= FilterInside;
                AddMatch(folder.PathOf(file));
            }
        }

        foreach (var child in folder.Children)
        {
            if (Evaluate(child))
            {
                state |= FilterInside;
            }
        }

        if (!folder.IsComputer && !folder.IsLoaded)
        {
            state |= FilterUnknown;
        }

        // A folder the filter now judges differently is drawn in other
        // colours: its own are worked out again, and nobody else's.
        if (FilterStateOf(folder) != state)
        {
            folder.PaletteStamp = PaletteOutOfDate;
        }

        folder.FilterStamp = _filterStamp;
        folder.FilterState = state;
        return (state & (FilterSelf | FilterInside)) != 0;
    }

    /// <summary>
    /// What a folder's <see cref="NestedFolder.PaletteStamp"/> is set to when
    /// only its own colours are out of date: a stamp the canvas's never is,
    /// since that starts at nought and only counts up.
    /// </summary>
    private const int PaletteOutOfDate = -1;

    private void AddMatch(string path)
    {
        if (_filterMatchSet.Add(path))
        {
            _filterMatches.Add(path);
        }
        else
        {
            _filterUnconfirmed?.Remove(path);
        }
    }

    /// <summary>
    /// While a folder read again is judged (<see cref="OnFolderLoadedForFilter"/>):
    /// the matches at or under it from before, each crossed off as the
    /// judging finds it again.  What is left went with the read - deleted, or
    /// renamed to another name - and is taken out.  Null the rest of the time.
    /// </summary>
    private HashSet<string>? _filterUnconfirmed;

    /// <summary>The matches at or under the folder <paramref name="root"/>; null when there are none.</summary>
    private HashSet<string>? MatchesAtOrUnder(string root)
    {
        HashSet<string>? found = null;
        foreach (var path in _filterMatches)
        {
            if (IsAtOrUnder(path, root))
            {
                (found ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(path);
            }
        }

        return found;
    }

    /// <summary>
    /// Takes <paramref name="gone"/> out of the matches, in one pass, keeping
    /// the step the user is on - or, when that is one of them, the step
    /// before it, so that Next goes on to the match that followed it.
    /// </summary>
    private void RemoveMatches(HashSet<string> gone)
    {
        var kept = 0;
        var cursor = -1;
        for (var index = 0; index < _filterMatches.Count; index++)
        {
            var path = _filterMatches[index];
            if (gone.Contains(path))
            {
                _filterMatchSet.Remove(path);
                if (index == _filterCursor)
                {
                    cursor = kept - 1;
                }

                continue;
            }

            if (index == _filterCursor)
            {
                cursor = kept;
            }

            _filterMatches[kept++] = path;
        }

        _filterMatches.RemoveRange(kept, _filterMatches.Count - kept);
        _filterCursor = _filterCursor < 0 ? -1 : cursor;
    }

    /// <summary>
    /// A folder was read while a filter is on: judge what came in, and let
    /// every folder above it know if something in it matched - or if nothing
    /// in it does any more.  Only the folders whose standing changed - the one
    /// read, what came in with it, and the ancestors that now hold a match for
    /// the first time, or hold one no longer - have their colours worked out
    /// again; every other cell on screen keeps its own, where a read used to
    /// make every cell of the frame work its colours and its mark out afresh.
    /// The window hears of new matches once per frame, however many folders
    /// the frame took in.
    /// </summary>
    private void OnFolderLoadedForFilter(NestedFolder folder)
    {
        if (_filter is null)
        {
            return;
        }

        // Read again, a folder may have lost matches it had: a file deleted,
        // or renamed - to a name the filter still takes, which is a new match
        // and would otherwise be counted beside the old one.  What it held is
        // noted and whatever the judging does not find again goes.  Only a
        // folder that held a match inside it can lose one: its own name, the
        // only other match it can be, does not change while it is the same
        // folder - so a folder's first read, which has nothing inside it to
        // lose, never looks through the matches.  Nor can one the judging of
        // a new filter has not come to yet; one it is still going through
        // may already have matches listed under it.
        var before = _filterMatches.Count;
        var held = folder.FilterStamp == _filterStamp && (folder.FilterState & (FilterSelf | FilterInside)) != 0;
        _filterUnconfirmed = folder.FilterStamp == _filterStamp && (folder.FilterState & FilterInside) != 0 || IsBeingJudged(folder)
            ? MatchesAtOrUnder(folder.FullPath)
            : null;
        bool matched;
        HashSet<string>? gone;
        try
        {
            matched = Evaluate(folder);
        }
        finally
        {
            gone = _filterUnconfirmed;
            _filterUnconfirmed = null;
        }

        var lost = false;
        if (gone is { Count: > 0 })
        {
            RemoveMatches(gone);
            lost = true;
        }

        if (matched)
        {
            for (var parent = folder.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.FilterStamp == _filterStamp && (parent.FilterState & FilterInside) == 0)
                {
                    parent.FilterState |= FilterInside;
                    parent.PaletteStamp = PaletteOutOfDate;
                }
            }
        }
        else if (held)
        {
            // The last match at or under it went with the read: the folders
            // above it that held a match only through it hold none either,
            // up to the first that still holds another, and fade too.
            for (var parent = folder.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.FilterStamp != _filterStamp || (parent.FilterState & FilterInside) == 0
                    || AnyChildMatches(parent) || AnyFileMatches(parent))
                {
                    break;
                }

                parent.FilterState &= ~FilterInside;
                parent.PaletteStamp = PaletteOutOfDate;
            }
        }

        if (_filterMatches.Count != before || lost)
        {
            RaiseFilterChangedWithFrame();
        }
    }

    // ---- the frame's held-back events ------------------------------------------------

    private bool _filterChangePending;

    /// <summary>
    /// The filter's matches changed because folders were read: said once,
    /// at the end of the frame's intake, for every folder the frame took in
    /// - a read wave of a hundred folders used to be a hundred FilterChanged,
    /// each restarting the window's beacon timer.  A canvas that is not on
    /// screen has no frame to wait for, and says it at once.
    /// </summary>
    private void RaiseFilterChangedWithFrame()
    {
        if (!IsVisible && !HoldsFrameEventsForTests)
        {
            RaiseFilterChanged();
            return;
        }

        _filterChangePending = true;
        RequestFrame(Layers.None);
    }

    /// <summary>The filter's matches changed: said now, which also says whatever was held back for the frame.</summary>
    private void RaiseFilterChanged()
    {
        _filterChangePending = false;
        FilterChanged?.Invoke();
    }

    /// <summary>For tests: holds the frame's events back for <see cref="FlushFrameEvents"/> as a canvas on screen does, though this one is in no window.</summary>
    internal bool HoldsFrameEventsForTests { get; set; }

    /// <summary>For tests: a folder read while the filter is on, as the tree says it.</summary>
    internal void FolderLoadedForTests(NestedFolder folder) => OnFolderLoadedForFilter(folder);

    /// <summary>For tests: the stamp a folder's colours are kept under while they are up to date.</summary>
    internal int PaletteStampForTests => _paletteStamp;

    /// <summary>Phase 7 of the frame: the events held back while the frame took in its reads, raised once each.</summary>
    partial void FlushFrameEvents()
    {
        if (_filterChangePending)
        {
            RaiseFilterChanged();
        }
    }

    /// <summary>
    /// Puts the matches in walking order for the current order, keeping the
    /// step the user is on.  Only folders that match or hold a match are
    /// walked - what the filter already found out about each folder says
    /// which - and each is placed for the current order before its files and
    /// sub-folders are read, so this is right even while the tree is still
    /// placing the rest.  A match the walk no longer reaches - its folder
    /// hidden since - keeps its place at the end, as it would have before.
    /// </summary>
    private void ReorderFilterMatches()
    {
        if (_tree is null || _filter is null)
        {
            return;
        }

        var current = _filterCursor >= 0 && _filterCursor < _filterMatches.Count ? _filterMatches[_filterCursor] : null;
        var ordered = new List<string>(_filterMatches.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMatches(_tree.Root, ordered, seen);
        foreach (var path in _filterMatches)
        {
            // Not reached, but still there - its folder hidden since, say -
            // it keeps a place at the end; gone, it goes, so a step never
            // lands on something that is not there.
            if (seen.Add(path) && ResolveAsPlaced(path) is not null)
            {
                ordered.Add(path);
            }
        }

        _filterMatches.Clear();
        _filterMatches.AddRange(ordered);
        _filterMatchSet.Clear();
        _filterMatchSet.UnionWith(ordered);
        _filterCursor = current is null ? -1 : _filterMatches.FindIndex(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase));
        _filterOrderGeneration = _tree.SortGeneration;
    }

    /// <summary>The matches in and under one folder, in the order <see cref="Evaluate"/> walks it.</summary>
    private void CollectMatches(NestedFolder folder, List<string> into, HashSet<string> seen)
    {
        var state = FilterStateOf(folder);
        if (!folder.IsComputer && (state & FilterSelf) != 0 && seen.Add(folder.FullPath))
        {
            into.Add(folder.FullPath);
        }

        if ((state & FilterInside) == 0)
        {
            return;
        }

        Ensure(folder);
        foreach (var file in folder.Files)
        {
            if (_filter!(file.Name))
            {
                var path = folder.PathOf(file);
                if (seen.Add(path))
                {
                    into.Add(path);
                }
            }
        }

        foreach (var child in folder.Children)
        {
            if ((FilterStateOf(child) & (FilterSelf | FilterInside)) != 0)
            {
                CollectMatches(child, into, seen);
            }
        }
    }

    /// <summary>
    /// Once the tree has placed every folder for its current order, puts the
    /// matches in that order - the list the window shows marks for, and
    /// counts through, then follows what is on screen.  One wait at a time;
    /// an order changed again meanwhile is simply waited for too.
    /// </summary>
    private async Task ReorderFilterWhenPlacedAsync()
    {
        if (_filterReorderWaiting)
        {
            return;
        }

        _filterReorderWaiting = true;
        try
        {
            while (_tree is { } tree && _filter is not null && _filterOrderGeneration != tree.SortGeneration)
            {
                await tree.WhenSortIdleAsync();
                if (!ReferenceEquals(_tree, tree) || _filter is null || tree.IsSorting)
                {
                    continue;
                }

                if (_filterOrderGeneration != tree.SortGeneration)
                {
                    ReorderFilterMatches();
                    RaiseFilterChanged();
                }
            }
        }
        finally
        {
            _filterReorderWaiting = false;
        }
    }

    private int FilterStateOf(NestedFolder folder) =>
        folder.FilterStamp == _filterStamp ? folder.FilterState : FilterUnknown;

    /// <summary>Whether a folder is faded by the filter: neither a match nor on the way to one.</summary>
    private bool IsFilteredOut(NestedFolder folder) =>
        _filter is not null && !folder.IsComputer && (FilterStateOf(folder) & (FilterSelf | FilterInside)) == 0;

    /// <summary>
    /// A file's colours come from its extension, the way a folder's come from
    /// its path: every .png the same hue, every .cs another, stable across runs.
    /// </summary>
    private (uint Body, uint Stripe, uint Speck) FilePalette(string extension)
    {
        if (_filePalette.TryGetValue(extension, out var palette))
        {
            return palette;
        }

        var hash = 2166136261u;
        foreach (var character in extension)
        {
            hash = (hash ^ character) * 16777619u;
        }

        var hue = extension.Length == 0 ? 210 : hash % 360u;
        var saturation = extension.Length == 0 ? 0.05 : 0.14;
        var body = NestedRaster.FromHsl(hue, saturation, 0.19);
        var stripe = NestedRaster.FromHsl(hue, extension.Length == 0 ? 0.08 : 0.6, 0.6);
        palette = (body, stripe, NestedRaster.Mix(body, stripe, 0.45));
        _filePalette[extension] = palette;
        return palette;
    }

    /// <summary>
    /// A cell's colours: a dark tint of the folder's own hue, one step lighter
    /// on every other level so a child reads against its parent, and the
    /// user's colour label over the title when there is one and the marks
    /// layer is showing.
    ///
    /// Worked out once and kept on the folder under the canvas's palette
    /// stamp, which moves on only when every cell's colours may have changed
    /// - the marks, a new filter.  What changes one folder's colours alone,
    /// the filter judging it anew once it is read, puts that folder's stamp
    /// out of date and no other (<see cref="PaletteOutOfDate"/>).
    /// </summary>
    private void EnsurePalette(NestedFolder folder)
    {
        if (folder.PaletteStamp == _paletteStamp)
        {
            return;
        }

        folder.PaletteStamp = _paletteStamp;

        // With the marks layer off, every folder is drawn as if unmarked.
        var mark = folder.IsComputer || !Shows(CanvasLayer.Marks) ? FolderMark.None : MarkOfFolder(folder);
        folder.HasNote = !string.IsNullOrWhiteSpace(mark.Note);

        var hue = folder.Hue;
        var saturation = folder.Kind switch
        {
            NestedFolderKind.Computer => 0.0,
            NestedFolderKind.Drive => 0.08,
            _ => 0.2
        };
        var lightness = folder.IsComputer ? 0.075 : 0.112 + 0.03 * (folder.Depth % 2);
        var body = NestedRaster.FromHsl(hue, saturation, lightness);
        var header = NestedRaster.FromHsl(hue, saturation + 0.06, lightness + 0.05);
        var rim = NestedRaster.FromHsl(hue, Math.Min(0.32, saturation + 0.12), folder.IsComputer ? 0.16 : 0.27);
        var stripe = NestedRaster.FromHsl(hue, 0.52, Math.Clamp(0.66 - folder.Depth * 0.022, 0.42, 0.66));

        folder.HasLabel = false;
        if (TryParseColour(mark.AccentHex, out var label))
        {
            var packed = NestedRaster.Pack(label);
            header = NestedRaster.Mix(header, packed, 0.5);
            body = NestedRaster.Mix(body, packed, 0.1);
            rim = NestedRaster.Mix(rim, packed, 0.9);
            stripe = packed;
            folder.HasLabel = true;
        }

        if (folder.IsHidden)
        {
            body = NestedRaster.Mix(body, CanvasColour, 0.4);
            header = NestedRaster.Mix(header, CanvasColour, 0.4);
            rim = NestedRaster.Mix(rim, CanvasColour, 0.4);
        }

        if (_filter is not null && !folder.IsComputer)
        {
            var state = FilterStateOf(folder);
            if ((state & FilterSelf) != 0)
            {
                // A match: its own colours, and a title in the filter's colour.
                header = NestedRaster.Mix(header, FilterColour, 0.45);
                rim = FilterColour;
            }
            else if ((state & FilterInside) == 0)
            {
                // Faded: far for what is known not to match, less for what
                // has not been read and still might.
                var fade = (state & FilterUnknown) != 0 ? 0.45 : 0.72;
                body = NestedRaster.Mix(body, CanvasColour, fade);
                header = NestedRaster.Mix(header, CanvasColour, fade);
                rim = NestedRaster.Mix(rim, CanvasColour, fade);
                stripe = NestedRaster.Mix(stripe, CanvasColour, fade);
            }
        }

        folder.BodyColour = body;
        folder.HeaderColour = header;
        folder.RimColour = rim;
        folder.StripeColour = stripe;
    }

    /// <summary>
    /// Only #RRGGBB or #AARRGGBB.  The converter also takes names and 'sc#'
    /// forms, and some of those throw exceptions it does not document - from
    /// inside a frame, where an exception ends the program.  A mark only ever
    /// holds a hex colour from the palette, so anything else is ignored.
    /// </summary>
    internal static bool IsHexColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#' || hex.Length is not (7 or 9))
        {
            return false;
        }

        for (var index = 1; index < hex.Length; index++)
        {
            if (!char.IsAsciiHexDigit(hex[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseColour(string hex, out Color colour)
    {
        colour = default;
        if (!IsHexColour(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
