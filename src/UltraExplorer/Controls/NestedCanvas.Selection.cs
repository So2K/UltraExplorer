using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// The selection as the canvas holds it and changes it: several items at
// once, picked by clicks with Ctrl and Shift, by a rectangle drawn over a
// folder, by the keys, by Ctrl+A.  Each gesture changes the canvas's copy
// (NestedSelection) first, draws it, and hands the window the same change as
// one edit in the same call, so a drag or a menu opened straight after it
// already acts on what the user sees.  The rectangle itself - where it
// started, what it touches, the view sliding on at an edge - is worked out
// once a frame; the mouse only moves its corner.
public sealed partial class NestedCanvas
{
    /// <summary>A container this narrow on screen (DIPs) is too small to draw a rectangle in: its parent is used instead.</summary>
    internal const double MinimumContainerPixels = 64;

    /// <summary>How often, at most, the count of what a rectangle will select is handed on while it is drawn.</summary>
    private static readonly TimeSpan MarqueeCountInterval = TimeSpan.FromMilliseconds(66);

    /// <summary>
    /// Stands for "the paths <see cref="SetSelection"/> left have been taken
    /// in": its own string, so no path handed in can ever be it.
    /// </summary>
    private static readonly string SelectionTaken = new('\u0001', 1);

    private readonly NestedSelection _selection = new();
    private NestedMarquee? _marquee;
    private NestedPointer? _pointer;
    private NestedTree? _pendingTree;
    private readonly List<string> _vanished = [];

    /// <summary>
    /// One change of the selection made on the canvas - a click, a range, a
    /// rectangle let go of, Ctrl+A, Esc, items found gone - for the window
    /// to apply to the selection it shares.  Raised once per gesture, after
    /// the canvas already shows it.
    /// </summary>
    public event Action<SelectionEdit>? SelectionCommitted;

    /// <summary>
    /// While a rectangle is drawn: how many items it would select if let go
    /// now, about fifteen times a second at most; -1 once it is let go or
    /// cancelled.  For the status bar.
    /// </summary>
    public event Action<int>? MarqueePreview;

    /// <summary>A rectangle started being drawn: the window's cue for the one-time hint about left-drag.</summary>
    public event Action? MarqueeStarted;

    /// <summary>What a left drag does where nothing is picked up: select an area (the default) or pan.</summary>
    public NestedLeftDrag LeftDrag { get; set; }

    /// <summary>For tests and the bench: the mouse, driven by hand.</summary>
    internal NestedPointer Pointer => _pointer ??= new NestedPointer(this);

    /// <summary>For tests: the rectangle being drawn, or null.</summary>
    internal NestedMarquee? Marquee => _marquee;

    /// <summary>For tests: the canvas's own copy of the selection.</summary>
    internal NestedSelection SelectionState
    {
        get
        {
            TakeSetSelection();
            return _selection;
        }
    }

    /// <summary>Items selected on the canvas, the rectangle's not counted until it is let go.</summary>
    public int SelectedCount
    {
        get
        {
            TakeSetSelection();
            return _selection.Count;
        }
    }

    /// <summary>
    /// How many of the window's selected items this canvas shows, for Delete
    /// to ask about the rest; null when the canvas holds another version of
    /// the selection than <paramref name="version"/> and cannot say.  An item
    /// waiting for a folder the canvas has not read - a search result
    /// elsewhere, a folder the list went into - counts as shown: nothing
    /// says it is left out; so does one in a folder too large for the canvas
    /// to list whole.  One left out of a folder the canvas has read - hidden
    /// while hidden items are hidden, or a file while the Files layer is
    /// off - does not.
    /// </summary>
    internal int? ShownOfSelection(long version)
    {
        TakeSetSelection();
        if (_selection.LoadedVersion != version)
        {
            return null;
        }

        return _selection.Count + _selection.CountWaiting(path =>
            _tree?.Find(path) is not { } folder || !folder.IsLoaded && !folder.IsComputer
            || folder.IsTruncated || folder.UnlistedFileCount > 0);
    }

    /// <summary>
    /// Takes in the window's selection, unless the canvas already holds that
    /// version - its own gestures come back this way, and are ignored.  What
    /// the rectangle being drawn would do is kept on top of it.
    /// </summary>
    public void LoadSelection(ItemSelection selection)
    {
        // Whatever SetSelection left is older than this.
        _selected.Clear();
        _activePath = SelectionTaken;
        if (!_selection.Load(selection, _tree))
        {
            return;
        }

        WatchPending();
        SelectionChangedHere();
    }

    /// <summary>The window has applied the canvas's last edit as <paramref name="version"/>: that version is the canvas's own.</summary>
    public void AcknowledgeSelection(long version) => _selection.LoadedVersion = version;

    /// <summary>
    /// What <see cref="SetSelection"/> was handed, taken into the selection
    /// the first time anything reads it: the paths it keeps become items,
    /// its active path the focus and the anchor.
    /// </summary>
    private void TakeSetSelection()
    {
        if (ReferenceEquals(_activePath, SelectionTaken))
        {
            return;
        }

        var active = _activePath;
        var paths = _selected.ToArray();
        _selected.Clear();
        _activePath = SelectionTaken;
        if (_tree is null)
        {
            return;
        }

        var handed = new ItemSelection();
        handed.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [.. paths.Select(path => new SelectionItem(path, _tree.Find(path) is not null, 0))],
            Anchor = active.Length > 0 ? active : null,
            Focus = active.Length > 0 ? active : null,
            Source = SelectionSource.Command
        });
        _selection.Load(handed, _tree);

        // Not the window's version: the next one it hands in is always news.
        _selection.LoadedVersion = -1;
        WatchPending();
        SelectionChangedHere();
    }

    /// <summary>The selection's picture is out of date: drawn again on the next frame.</summary>
    private void SelectionChangedHere() => RequestFrame(Layers.Overlay);

    /// <summary>
    /// While paths wait for folders the tree has not read, each folder it
    /// reads is looked at for them.
    /// </summary>
    private void WatchPending()
    {
        if (_selection.HasPending && _tree is { } tree && !ReferenceEquals(_pendingTree, tree))
        {
            if (_pendingTree is not null)
            {
                _pendingTree.FolderLoaded -= OnFolderLoadedForSelection;
            }

            _pendingTree = tree;
            tree.FolderLoaded += OnFolderLoadedForSelection;
        }
    }

    private void OnFolderLoadedForSelection(NestedFolder folder)
    {
        if (!ReferenceEquals(_pendingTree, _tree))
        {
            if (_pendingTree is not null)
            {
                _pendingTree.FolderLoaded -= OnFolderLoadedForSelection;
            }

            _pendingTree = null;
            return;
        }

        if (_selection.ResolvePending(folder))
        {
            SelectionChangedHere();
        }

        if (!_selection.HasPending && _pendingTree is not null)
        {
            _pendingTree.FolderLoaded -= OnFolderLoadedForSelection;
            _pendingTree = null;
        }
    }

    // ---- gestures ----------------------------------------------------------------

    /// <summary>
    /// One item alone - a plain click, an arrow, Backspace, a mark or a match
    /// gone to - with the anchor and the focus on it.  Going somewhere, as
    /// far as back and forward are concerned.
    /// </summary>
    private void SelectOnly(in NestedItemKey key)
    {
        _selection.Clear();
        _selection.Set(key, true);
        _selection.Anchor = key;
        _selection.Active = key;
        var path = key.Path;
        Commit(new SelectionEdit
        {
            Clear = true,
            Container = key.Container.FullPath,
            Added = [ItemOf(key, path)],
            Anchor = path,
            Focus = path,
            RecordsNavigation = true,
            Source = SelectionSource.Canvas
        });
    }

    /// <summary>Ctrl+click: the item in or out, and the anchor and focus on it either way, as in Explorer.</summary>
    private void ToggleItem(in NestedItemKey key)
    {
        var selected = !_selection.Contains(key);
        _selection.Set(key, selected);
        _selection.Anchor = key;
        _selection.Active = key;
        var path = key.Path;
        List<string> removed = selected ? [] : [path];
        var above = selected && DeselectFoldersAbove(key.Container, removed);
        Commit(new SelectionEdit
        {
            Container = above ? null : key.Container.FullPath,
            Added = selected ? [ItemOf(key, path)] : [],
            Removed = removed,
            Anchor = path,
            Focus = path,
            Source = SelectionSource.Canvas
        });
    }

    /// <summary>
    /// Items added inside a folder that is selected itself - the folder gone
    /// into, which going there selects - take that folder and every selected
    /// folder above it out, their paths added to <paramref name="removed"/>:
    /// kept, a Delete or a Move would act on the whole folder rather than on
    /// what was picked in it.  True when there were any; the edit's items are
    /// then not all in one folder.
    /// </summary>
    private bool DeselectFoldersAbove(NestedFolder container, List<string> removed)
    {
        var any = false;
        for (var folder = container; folder.Parent is not null; folder = folder.Parent)
        {
            if (_selection.SetFolder(folder, false))
            {
                removed.Add(folder.FullPath);
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Shift+click, Shift+arrows: everything from the anchor - or
    /// <paramref name="start"/>, when given - to <paramref name="to"/> in the
    /// order on screen, in place of what was selected - or,
    /// <paramref name="add"/>, on top of it.  The focus moves
    /// to the far end; the anchor stays.  False when there is no anchor in
    /// the same folder, and the gesture is a plain one instead.
    /// </summary>
    private bool SelectRange(in NestedItemKey to, bool add, NestedItemKey? start = null)
    {
        if ((start ?? _selection.Anchor) is not { } anchor || !ReferenceEquals(anchor.Container, to.Container) || _tree is null)
        {
            return false;
        }

        var container = to.Container;
        Ensure(container);
        var first = OrdinalOf(anchor);
        var last = OrdinalOf(to);
        if (first < 0 || last < 0)
        {
            return false;
        }

        if (first > last)
        {
            (first, last) = (last, first);
        }

        var items = new List<SelectionItem>(last - first + 1);
        if (!add)
        {
            _selection.Clear();
        }

        var children = container.Children;
        var files = container.Files;
        var prefix = PathPrefix(container);
        var fileSet = last >= children.Count ? _selection.FilesFor(container) : null;
        var filesBefore = fileSet?.Count ?? 0;
        for (var ordinal = first; ordinal <= last; ordinal++)
        {
            if (ordinal < children.Count)
            {
                var child = children[ordinal];
                if (FolderMatches(child) && (_selection.SetFolder(child, true) || !add))
                {
                    items.Add(new SelectionItem(child.FullPath, true, 0));
                }
            }
            else
            {
                var index = ordinal - children.Count;
                var file = files[index];
                if (FileMatches(file) && (fileSet!.Set(index, true) || !add))
                {
                    items.Add(new SelectionItem(string.Concat(prefix, file.Name), false, file.Length));
                }
            }
        }

        if (fileSet is not null)
        {
            _selection.Settle(container, fileSet, filesBefore);
        }

        List<string> removed = [];
        var above = add && DeselectFoldersAbove(container, removed);
        _selection.Anchor = anchor;
        _selection.Active = to;
        _selection.CurrentFolder = container;
        Commit(new SelectionEdit
        {
            Clear = !add,
            Container = above ? null : container.FullPath,
            Added = items,
            Removed = removed,
            Anchor = anchor.Path,
            Focus = to.Path,
            Source = SelectionSource.Canvas
        });
        return true;
    }

    /// <summary>
    /// Ctrl+A: every sub-folder and file of the current folder - the one the
    /// last rectangle, click or key was in, while it is on screen, otherwise
    /// the one the camera is on - and only what matches while the filter is
    /// on.  The anchor and the focus stay where they were.
    /// </summary>
    private void SelectAll()
    {
        if (_tree is null)
        {
            return;
        }

        var folder = _selection.CurrentFolder;
        if (folder is null || NestedTree.IsDetached(folder) || !NestedTree.IsOnCanvas(folder) || !IsOnScreen(folder))
        {
            folder = _anchor ?? _tree.Root;
        }

        Ensure(folder);
        _selection.Clear();
        var items = new List<SelectionItem>(folder.Children.Count + folder.Files.Count);
        foreach (var child in folder.Children)
        {
            if (FolderMatches(child))
            {
                _selection.SetFolder(child, true);
                items.Add(new SelectionItem(child.FullPath, true, 0));
            }
        }

        var files = folder.Files;
        if (files.Count > 0)
        {
            var prefix = PathPrefix(folder);
            var fileSet = _selection.FilesFor(folder);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (FileMatches(file))
                {
                    fileSet.Set(index, true);
                    items.Add(new SelectionItem(string.Concat(prefix, file.Name), false, file.Length));
                }
            }

            _selection.Settle(folder, fileSet, 0);
        }

        _selection.CurrentFolder = folder;
        Commit(new SelectionEdit
        {
            Clear = true,
            Container = folder.FullPath,
            Added = items,
            Source = SelectionSource.Canvas
        });
    }

    /// <summary>
    /// Esc, or a click on nothing: nothing selected, the focus and the anchor
    /// kept.  False when nothing was - not even something waiting for a
    /// folder to be read, which the canvas cannot show yet but the window
    /// still holds, and a Delete would act on.
    /// </summary>
    private bool ClearSelection()
    {
        if (_selection.IsEmpty && !_selection.HasPending)
        {
            return false;
        }

        _selection.Clear();
        Commit(new SelectionEdit { Clear = true, Source = SelectionSource.Canvas });
        return true;
    }

    /// <summary>Hands a gesture's edit to the window, and has the selection drawn again.</summary>
    private void Commit(SelectionEdit edit)
    {
        SelectionChangedHere();
        SelectionCommitted?.Invoke(edit);
    }

    // ---- the rectangle -------------------------------------------------------------

    /// <summary>
    /// Starts a rectangle over <paramref name="container"/> from
    /// <paramref name="at"/>: the start is fixed to the content under it,
    /// what the container held selected is noted in the order its items are
    /// placed, and in a replacement everything selected elsewhere goes out of
    /// sight at once - it is still there for Esc to bring back.
    /// </summary>
    private bool StartMarquee(NestedFolder container, Point at, NestedSelectMode mode)
    {
        StopFlight();
        Ensure(container);
        BuildChain();
        if (CellOf(container, place: true) is not { } cell || !(cell.W > 0))
        {
            return false;
        }

        var marquee = new NestedMarquee(container, (at.X - cell.X) / cell.W, (at.Y - cell.Y) / cell.W, at, mode);
        _marquee = marquee;
        TakeBase(marquee);
        _selection.CurrentFolder = container;
        _hover = null;
        MarqueeStarted?.Invoke();
        RequestFrame(Layers.Overlay);
        return true;
    }

    /// <summary>What the container holds selected, in the order its items are placed now, as the rectangle's base.</summary>
    private void TakeBase(NestedMarquee marquee)
    {
        var container = marquee.Container;
        var children = container.Children;
        var folders = new ulong[(children.Count + 63) >> 6];
        if (_selection.FoldersIn(container) > 0)
        {
            for (var index = 0; index < children.Count; index++)
            {
                if (_selection.IsSelected(children[index]))
                {
                    NestedMarquee.SetBit(folders, index);
                }
            }
        }

        var files = _selection.FilesOf(container, _vanished) is { } set
            ? (ulong[])set.Bits.Clone()
            : new ulong[(container.Files.Count + 63) >> 6];
        marquee.SetBase(folders, files);
    }

    /// <summary>
    /// The rectangle, brought up to this frame: cancelled when its folder
    /// went away, its base taken again when the folder was sorted or read
    /// again, the pointer turned into the container's unit frame through
    /// this frame's camera, and what it touches worked out as two blocks.
    /// Cheap enough to run twice in a frame, and it may.
    /// </summary>
    private void UpdateMarquee()
    {
        if (_marquee is not { } marquee)
        {
            return;
        }

        var container = marquee.Container;
        if (NestedTree.IsDetached(container) || !NestedTree.IsOnCanvas(container))
        {
            CancelMarquee();
            return;
        }

        Ensure(container);
        if (CellOf(container, place: true) is not { } cell || !(cell.W > 0))
        {
            CancelMarquee();
            return;
        }

        if (!marquee.IsBaseCurrent)
        {
            // Sorted or read again: the base and the matches by place are
            // both for the places before.
            TakeBase(marquee);
            if (_filter is not null)
            {
                TakeMatches(marquee);
            }
        }

        if (_filter is null ? marquee.MatchStamp != -1 : marquee.MatchStamp != _filterStamp)
        {
            TakeMatches(marquee);
        }

        var pointerX = (marquee.Pointer.X - cell.X) / cell.W;
        var pointerY = (marquee.Pointer.Y - cell.Y) / cell.W;
        var left = Math.Min(marquee.StartX, pointerX);
        var right = Math.Max(marquee.StartX, pointerX);
        var top = Math.Min(marquee.StartY, pointerY);
        var bottom = Math.Max(marquee.StartY, pointerY);
        marquee.SetBlocks(container.Grid.Touching(left, top, right, bottom), container.FileGrid.Touching(left, top, right, bottom));
    }

    /// <summary>Which of the container's items the filter lets a rectangle take, once per filter and placing.</summary>
    private void TakeMatches(NestedMarquee marquee)
    {
        if (_filter is null)
        {
            marquee.SetMatches(null, null, -1);
            marquee.MatchCount = -1;
            return;
        }

        var container = marquee.Container;
        var children = container.Children;
        var folders = new ulong[(children.Count + 63) >> 6];
        var matching = 0;
        for (var index = 0; index < children.Count; index++)
        {
            if (FolderMatches(children[index]))
            {
                NestedMarquee.SetBit(folders, index);
                matching++;
            }
        }

        var files = container.Files;
        var fileBits = new ulong[(files.Count + 63) >> 6];
        for (var index = 0; index < files.Count; index++)
        {
            if (_filter(files[index].Name))
            {
                NestedMarquee.SetBit(fileBits, index);
                matching++;
            }
        }

        marquee.SetMatches(folders, fileBits, _filterStamp);
        marquee.MatchCount = matching;
    }

    /// <summary>
    /// The count handed on while the rectangle is drawn, at most every
    /// <see cref="MarqueeCountInterval"/>: what would be selected in all if
    /// it were let go now.
    /// </summary>
    private void CountMarquee(NestedMarquee marquee)
    {
        if (!marquee.CountStale)
        {
            return;
        }

        var now = _clock.Now;
        if (marquee.CountRaisedAt != TimeSpan.MinValue && now - marquee.CountRaisedAt < MarqueeCountInterval)
        {
            // Asked for again once the interval is up.
            RequestFrame(Layers.Overlay);
            return;
        }

        var inside = marquee.CountSelected();
        var total = marquee.Mode == NestedSelectMode.Replace
            ? inside
            : _selection.Count - BaseCount(marquee) + inside;
        marquee.CountStale = false;
        marquee.CountRaisedAt = now;
        if (total != marquee.HitCount)
        {
            marquee.HitCount = total;
            MarqueePreview?.Invoke(total);
        }
    }

    private static int BaseCount(NestedMarquee marquee) =>
        NestedMarquee.PopCount(marquee.BaseFolders, 0, marquee.BaseFolders.Length << 6)
        + NestedMarquee.PopCount(marquee.BaseFiles, 0, marquee.BaseFiles.Length << 6);

    /// <summary>
    /// Lets go of the rectangle: its mode applied to what it touches, in one
    /// walk over the two blocks; the anchor put on the item it touches
    /// nearest where it started and the focus on the one nearest where it
    /// ended, so a Shift+click after a sweep runs on from its first corner;
    /// and the whole change handed on as one edit.
    /// </summary>
    private void CommitMarquee()
    {
        UpdateMarquee();
        if (_marquee is not { } marquee)
        {
            return;
        }

        _marquee = null;
        var container = marquee.Container;
        var cell = CellOf(container, place: true);
        var endX = cell is { } c ? (marquee.Pointer.X - c.X) / c.W : marquee.StartX;
        var endY = cell is { } d ? (marquee.Pointer.Y - d.Y) / d.W : marquee.StartY;

        var touched = container.Grid.CountIn(marquee.Folders) + container.FileGrid.CountIn(marquee.Files);
        var added = new List<SelectionItem>(marquee.Mode == NestedSelectMode.Toggle ? 0 : touched);
        var removed = new List<string>();
        var replace = marquee.Mode == NestedSelectMode.Replace;
        if (replace)
        {
            _selection.Clear();
        }

        // The items nearest the two corners, found on the way: by zone and
        // place, made into keys once at the end.
        var nearStart = (Zone: -1, Index: -1, Distance: double.MaxValue);
        var nearEnd = (Zone: -1, Index: -1, Distance: double.MaxValue);
        void Consider(int zone, int index, double centreX, double centreY)
        {
            var toStart = Square(centreX - marquee.StartX) + Square(centreY - marquee.StartY);
            if (toStart < nearStart.Distance)
            {
                nearStart = (zone, index, toStart);
            }

            var toEnd = Square(centreX - endX) + Square(centreY - endY);
            if (toEnd < nearEnd.Distance)
            {
                nearEnd = (zone, index, toEnd);
            }
        }

        var grid = container.Grid;
        var children = container.Children;
        var block = marquee.Folders;
        var matches = marquee.FolderMatches;
        for (var row = block.FirstRow; !block.IsEmpty && row <= block.LastRow; row++)
        {
            for (var column = block.FirstColumn; column <= block.LastColumn; column++)
            {
                var index = grid.IndexOf(row, column);
                if (index >= children.Count || matches is not null && !NestedMarquee.Bit(matches, index))
                {
                    continue;
                }

                var child = children[index];
                var before = NestedMarquee.Bit(marquee.BaseFolders, index);
                var after = marquee.IsSelected(before, touched: true);
                _selection.SetFolder(child, after);
                if (after && (replace || !before))
                {
                    added.Add(new SelectionItem(child.FullPath, true, 0));
                }
                else if (!after && before)
                {
                    removed.Add(child.FullPath);
                }

                var (x, y) = grid.Origin(index);
                Consider(0, index, x + grid.Scale / 2, y + grid.Scale * NestedLayout.CellHeight / 2);
            }
        }

        var tiles = container.FileGrid;
        var files = container.Files;
        var prefix = PathPrefix(container);
        var fileSet = marquee.Files.IsEmpty ? null : _selection.FilesFor(container);
        var filesBefore = fileSet?.Count ?? 0;
        block = marquee.Files;
        matches = marquee.FileMatches;
        var halfWidth = tiles.TileWidth / 2;
        var halfHeight = tiles.TileHeight / 2;
        for (var row = block.FirstRow; !block.IsEmpty && row <= block.LastRow; row++)
        {
            var centreY = tiles.Top + row * tiles.StepY + halfHeight;
            for (var column = block.FirstColumn; column <= block.LastColumn; column++)
            {
                var index = tiles.IndexOf(row, column);
                if (index >= files.Count || matches is not null && !NestedMarquee.Bit(matches, index))
                {
                    continue;
                }

                var before = NestedMarquee.Bit(marquee.BaseFiles, index);
                var after = marquee.IsSelected(before, touched: true);
                fileSet!.Set(index, after);
                if (after && (replace || !before))
                {
                    var file = files[index];
                    added.Add(new SelectionItem(string.Concat(prefix, file.Name), false, file.Length));
                }
                else if (!after && before)
                {
                    removed.Add(string.Concat(prefix, files[index].Name));
                }

                Consider(1, index, tiles.Left + column * tiles.StepX + halfWidth, centreY);
            }
        }

        if (fileSet is not null)
        {
            _selection.Settle(container, fileSet, filesBefore);
        }

        NestedItemKey? KeyAt((int Zone, int Index, double Distance) near) => near.Zone switch
        {
            0 => NestedItemKey.OfFolder(children[near.Index]),
            1 => NestedItemKey.OfFile(container, files[near.Index].Name),
            _ => null
        };

        var anchorKey = KeyAt(nearStart);
        var focusKey = KeyAt(nearEnd);
        if (anchorKey is { } anchor)
        {
            _selection.Anchor = anchor;
        }

        if (focusKey is { } focus)
        {
            _selection.Active = focus;
        }

        var above = !replace && added.Count > 0 && DeselectFoldersAbove(container, removed);
        _selection.CurrentFolder = container;
        MarqueePreview?.Invoke(-1);
        Commit(new SelectionEdit
        {
            Clear = replace,
            Container = above ? null : container.FullPath,
            Added = added,
            Removed = removed,
            Anchor = anchorKey?.Path,
            Focus = focusKey?.Path,
            Source = SelectionSource.Canvas
        });

        static double Square(double value) => value * value;
    }

    /// <summary>
    /// Esc, or the rectangle's folder went away: it is gone, and the
    /// selection from before it - which it never touched - is shown again as
    /// it was, every other folder's included.
    /// </summary>
    private void CancelMarquee()
    {
        if (_marquee is null)
        {
            return;
        }

        _marquee = null;
        if (_press == PressKind.Left)
        {
            // The button is still down: the rest of this press does nothing.
            _pressIntent = PressIntent.Spent;
        }

        MarqueePreview?.Invoke(-1);
        SelectionChangedHere();
    }

    /// <summary>
    /// The view sliding on by itself while the pointer is held at an edge of
    /// it during a rectangle - after a moment inside the view, at once
    /// outside it - so the rectangle can reach what is off screen.  One step
    /// a frame, as long as the frame took; each through <see cref="Pan"/>,
    /// as a drag would be.  Runs at the very end of the frame, after
    /// everything was drawn for the camera as it was, so the step shows on
    /// the next frame, whole.
    /// </summary>
    private void SlideAtEdges()
    {
        if (_marquee is not { } marquee || !_inFrameLoop)
        {
            return;
        }

        if (CellOf(marquee.Container, place: true) is not { } found)
        {
            marquee.EdgeSince = null;
            marquee.AutoPanning = false;
            return;
        }

        var cell = new Rect(found.X, found.Y, found.W, found.W * NestedLayout.CellHeight);
        var view = new Size(_viewWidth, _viewHeight);
        var band = NestedMarquee.EdgeBand(_viewWidth, _viewHeight);
        var pointer = marquee.Pointer;
        if (!NestedMarquee.WantsToSlide(pointer, view, cell, band))
        {
            marquee.EdgeSince = null;
            marquee.AutoPanning = false;
            return;
        }

        var now = _clock.Now;
        if (marquee.EdgeSince is not { } since)
        {
            marquee.EdgeSince = now;
            marquee.LastEdgeStep = now;
            since = now;
        }

        var sliding = now - since - (NestedMarquee.IsOutside(pointer, view) ? TimeSpan.Zero : NestedMarquee.EdgeDelay);
        var step = now - marquee.LastEdgeStep;
        marquee.LastEdgeStep = now;
        RequestFrame(Layers.Overlay);
        if (sliding <= TimeSpan.Zero || step <= TimeSpan.Zero)
        {
            return;
        }

        var seconds = Math.Min(step.TotalSeconds, NestedMarquee.EdgeStepLimit.TotalSeconds);
        var ramp = NestedMarquee.Ramp(sliding);
        var dx = NestedMarquee.EdgeStep(pointer.X, _viewWidth, cell.Left, cell.Right, band, seconds) * ramp;
        var dy = NestedMarquee.EdgeStep(pointer.Y, _viewHeight, cell.Top, cell.Bottom, band, seconds) * ramp;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        marquee.AutoPanning = true;
        Pan(new Vector(dx, dy));
    }

    // ---- where things are --------------------------------------------------------

    /// <summary>
    /// A folder's cell on screen from the camera's chain, like
    /// <see cref="RectOf"/> but by recursion rather than a list, so asking
    /// for it every frame for every folder with selected items in it costs
    /// no garbage.
    /// </summary>
    private (double X, double Y, double W)? CellOf(NestedFolder folder, bool place)
    {
        if (_chain.TryGetValue(folder, out var rect))
        {
            return rect;
        }

        if (folder.Index < 0 || folder.IsForgotten || folder.Parent is not { } parent || CellOf(parent, place) is not { } outer)
        {
            return null;
        }

        if (place)
        {
            Ensure(parent);
        }

        Place(parent, folder, outer.X, outer.Y, outer.W, out var x, out var y, out var w);
        return (x, y, w);
    }

    private bool IsOnScreen(NestedFolder folder) =>
        CellOf(folder, place: false) is { } cell
        && cell.X < _viewWidth && cell.Y < _viewHeight && cell.X + cell.W > 0 && cell.Y + cell.W * NestedLayout.CellHeight > 0;

    /// <summary>An item's place in its folder's reading order: sub-folders first, then files, row by row; -1 when it is not there.</summary>
    private int OrdinalOf(in NestedItemKey key)
    {
        if (key.Folder is { } folder)
        {
            return ReferenceEquals(folder.Parent, key.Container) && folder.Index >= 0 ? folder.Index : -1;
        }

        var index = _tree?.FindFileIndex(key.Container, key.FileName!) ?? -1;
        return index < 0 ? -1 : key.Container.Children.Count + index;
    }

    /// <summary>The item a hit is, by what it is.</summary>
    private static NestedItemKey? KeyOf(in NestedHit hit)
    {
        if (hit.IsFile)
        {
            return NestedItemKey.OfFile(hit.Folder, hit.File.Name);
        }

        return hit.Folder.Parent is null ? null : NestedItemKey.OfFolder(hit.Folder);
    }

    /// <summary>The item with the focus, placed for the current order: what arrows move from and Enter opens.</summary>
    private (NestedFolder Folder, int FileIndex)? ActiveTarget()
    {
        if (_selection.Active is not { } active || _tree is null)
        {
            return null;
        }

        if (active.Folder is { } folder)
        {
            return NestedTree.IsDetached(folder) ? null : (folder, -1);
        }

        var index = _tree.FindFileIndex(active.Container, active.FileName!);
        return index >= 0 && !NestedTree.IsDetached(active.Container) ? (active.Container, index) : null;
    }

    private static NestedItemKey KeyOf((NestedFolder Folder, int FileIndex) target) =>
        target.FileIndex >= 0
            ? NestedItemKey.OfFile(target.Folder, target.Folder.Files[target.FileIndex].Name)
            : NestedItemKey.OfFolder(target.Folder);

    /// <summary>
    /// A folder's path with its separator, once, for the paths of thousands
    /// of its files: joined to each name by one concatenation rather than a
    /// Path.Combine apiece.
    /// </summary>
    private static string PathPrefix(NestedFolder folder) =>
        folder.FullPath.EndsWith(Path.DirectorySeparatorChar) ? folder.FullPath : folder.FullPath + Path.DirectorySeparatorChar;

    /// <summary>What the window is told about an item: its path, whether it is a folder, and its size.</summary>
    private static SelectionItem ItemOf(in NestedItemKey key, string path)
    {
        if (key.Folder is not null)
        {
            return new SelectionItem(path, true, 0);
        }

        var files = key.Container.Files;
        var index = NestedTree.FileIndexAsPlaced(key.Container, key.FileName!);
        return new SelectionItem(path, false, index >= 0 && index < files.Count ? files[index].Length : 0);
    }

    /// <summary>Whether the filter lets a gesture take this sub-folder: always with no filter, else only a match.</summary>
    private bool FolderMatches(NestedFolder child) => _filter is null || (FilterStateOf(child) & FilterSelf) != 0;

    private bool FileMatches(in NestedFile file) => _filter is null || _filter(file.Name);
}
