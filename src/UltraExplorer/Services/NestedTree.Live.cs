using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UltraExplorer.Models;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Services;

// Live: what the tree does when something changes on disk.  Every folder the
// tree has read is registered with the change hub by its path, and the hub
// hands back, a frame at a time, the folders whose contents changed.  A
// changed folder on screen is read again at once, through the same queue and
// at the same priority as the canvas's own reads, and keeps being drawn as it
// was until the new listing is applied; one off screen is only marked, and is
// read again the moment it is drawn.  So what the user looks at follows the
// disk within a fraction of a second, whatever is selected, and a change
// nobody is looking at costs a flag.
public sealed partial class NestedTree : IChangeSink
{
    /// <summary>Folders a watch that is polled rather than watched is looked at per poll: the widest drawn first.</summary>
    public const int FoldersPerPoll = 32;

    /// <summary>Folders the background walks after F5 or after a subtree was dropped take in one slice.</summary>
    private const int FoldersPerSweepSlice = 4096;

    /// <summary>The renames each set of folder orders was last moved for (<see cref="FirstToMoveOrders"/>).</summary>
    private static readonly ConditionalWeakTable<FolderOrders, RenamePair[]> OrdersMovedFor = new();

    private ChangeHub? _changes;

    /// <summary>
    /// Every folder registered with the hub, which is every folder read and
    /// not dropped since, with the unlisted folders above one reached by
    /// name: what a poll picks its folders from, and what is taken off the
    /// hub again when the hub changes.
    /// </summary>
    private readonly HashSet<NestedFolder> _registered = [];

    /// <summary>Renames seen in a folder whose new listing has not been applied yet, by folder.</summary>
    private readonly Dictionary<NestedFolder, List<RenamePair>> _renames = [];

    /// <summary>Named checks for disappeared descendants whose parents have never been listed.</summary>
    private readonly HashSet<NestedFolder> _sparseGoneChecks = [];

    /// <summary>
    /// Folders whose own path went - removed, or renamed or moved away -
    /// while their parent's listing still holds them: until a listing of the
    /// parent drops them, or their name turns out to be another folder's now
    /// (<see cref="CheckNameTakenAsync"/>).  None below another of them
    /// (<see cref="NoteGone"/>).
    /// </summary>
    private readonly HashSet<NestedFolder> _goneByPath = [];

    /// <summary>
    /// The folders of <see cref="_goneByPath"/> by the parent whose listing
    /// holds them: what an applied listing looks its own up in.  Gone
    /// through whole for every apply anywhere, a deleted subtree of twenty
    /// thousand read folders whose parent was off screen cost every apply
    /// for the rest of the session a copy of them all.
    /// </summary>
    private readonly Dictionary<NestedFolder, List<NestedFolder>> _goneByParent = [];

    /// <summary>Dropped folders whose read descendants are still to be taken off the hub, a slice at a time.</summary>
    private readonly Queue<NestedFolder> _droppedSweep = new();

    /// <summary>Read folders still to be marked out of date by the walk after F5.</summary>
    private readonly Queue<NestedFolder> _staleSweep = new();

    private bool _droppedSweepPosted;
    private bool _staleSweepPosted;

    /// <summary>When the apply now under way began, for what the hub is told a refresh cost.</summary>
    private long _applyStarted;

    /// <summary>The files a folder had before the apply now under way, to tell whether its listing changed.</summary>
    private NestedFile[] _filesBeforeApply = [];

    /// <summary>
    /// The change hub the tree registers its folders with, or null while
    /// nothing watches the disk.  Setting it also has every drive and root
    /// made from now on take its watch from the hub
    /// (<see cref="WatchRootFor"/>), so it belongs before the first
    /// <see cref="SetRoots"/>: a root made before it has no watch, and only
    /// its path decides which lane its reads go in.  Folders already read
    /// are registered at once; a hub replaced has them all taken off it.
    /// </summary>
    public ChangeHub? Changes
    {
        get => _changes;
        set
        {
            if (ReferenceEquals(_changes, value))
            {
                return;
            }

            if (_changes is { } previous)
            {
                foreach (var folder in _registered)
                {
                    previous.Unregister(ChangeConsumer.Nested, folder.FullPath, folder);
                }

                _registered.Clear();
            }

            _changes = value;
            WatchRootFor = value is null ? null : value.RootFor;
            if (value is not null)
            {
                RegisterReadFolders();
            }
        }
    }

    /// <summary>
    /// The folder the camera of the canvas drawing the tree is fixed to, set
    /// by the canvas once a frame: a folder that went from disk with the view
    /// somewhere inside it is on screen even though its own cell is bigger
    /// than the view and is never drawn as one.
    /// </summary>
    internal NestedFolder? CameraAnchor { get; set; }

    // ---- for tests and the bench --------------------------------------------------

    /// <summary>Folders registered with the hub right now.</summary>
    internal int LiveRegisteredCount => _registered.Count;

    /// <summary>Changes the hub handed over for folders still in the tree.</summary>
    internal int LiveChangesTaken { get; private set; }

    /// <summary>Of those, the folders on screen that were queued to be read again at once.</summary>
    internal int LiveRereadsAsked { get; private set; }

    /// <summary>Of those, the folders off screen that were only marked, to be read when next drawn.</summary>
    internal int LiveMarkedOnly { get; private set; }

    /// <summary>Changes to files' sizes and dates taken in with no read.</summary>
    internal int LivePatches { get; private set; }

    /// <summary>Renamed folders that took their old listing with them.</summary>
    internal int LiveCarriedOver { get; private set; }

    /// <summary>Folders a poll looked at, and of those, the ones found changed.</summary>
    internal (int Looked, int Changed) LivePolls => (Volatile.Read(ref _pollsLooked), Volatile.Read(ref _pollsChanged));

    private int _pollsLooked;
    private int _pollsChanged;

    // ---- the hub's changes ----------------------------------------------------------

    /// <summary>
    /// Something changed in a folder the tree has read, on the tree's thread
    /// within a frame's allowance for changes: the hub has already waited for
    /// a burst of changes to go quiet, so this is once per folder per burst.
    /// A folder of another tree is none of this one's: the two panes of a
    /// split view each have a tree, both registered with the one hub, and the
    /// window hands every change to both (see <see cref="Owns"/>).
    /// </summary>
    void IChangeSink.FolderChanged(ChangeConsumer consumer, object target, in FolderChange change)
    {
        if (consumer == ChangeConsumer.Nested && target is NestedFolder folder && Owns(folder))
        {
            OnFolderChanged(folder, change);
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is one of this tree's: its parents
    /// lead up to this tree's This PC.  A folder dropped since is still
    /// counted - its parents are kept - and <see cref="OnFolderChanged"/>
    /// leaves it alone for that.  A walk up as many levels as the folder is
    /// deep, once per folder per burst of changes.
    /// </summary>
    internal bool Owns(NestedFolder folder)
    {
        var top = folder;
        while (top.Parent is { } parent)
        {
            top = parent;
        }

        return ReferenceEquals(top, Root);
    }

    /// <summary>
    /// Changes under a watch may have been missed - it overflowed, or was armed
    /// again.  Every folder read before this knows by its epoch
    /// (<see cref="NestedFolder.NeedsRefresh"/>), and nothing walks the tree to
    /// say so: the folders on screen only have to be drawn once more to ask
    /// for their reads, and the rest ask when they are next drawn.
    /// </summary>
    void IChangeSink.EpochBumped(WatchRoot root)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var drive in Root.AllChildren)
        {
            if (ReferenceEquals(drive.Watch, root) && (WasDrawnRecently(drive) || CameraAnchor is { } anchor && drive.Contains(anchor)))
            {
                RaiseChanged();
                return;
            }
        }
    }

    /// <summary>
    /// A watch that is polled rather than watched - a volume that refuses to be
    /// watched, a watch that is down - is due its look.  The widest folders of
    /// it on screen, <see cref="FoldersPerPoll"/> at most, have the directory's
    /// own last-write time compared, off this thread, with the one taken when
    /// they were read; a folder whose time moved on is touched, and comes back
    /// as a change like any other.  A folder read while the watch was up took
    /// no time to compare: it is touched to be read once more, which takes
    /// one now the watch is down - and catches what changed since it went
    /// down.  One whose time cannot be had at all is left alone, rather than
    /// read again at every look.
    /// </summary>
    void IChangeSink.PollDue(WatchRoot root)
    {
        if (_changes is not { } hub || _disposed)
        {
            return;
        }

        var picked = new List<NestedFolder>(FoldersPerPoll);
        foreach (var folder in DrawnFoldersOf(root))
        {
            if (!folder.IsLoaded || !WasDrawnRecently(folder))
            {
                continue;
            }

            if (picked.Count < FoldersPerPoll)
            {
                picked.Add(folder);
                continue;
            }

            var narrowest = 0;
            for (var index = 1; index < picked.Count; index++)
            {
                if (picked[index].LastDrawnWidth < picked[narrowest].LastDrawnWidth)
                {
                    narrowest = index;
                }
            }

            if (folder.LastDrawnWidth > picked[narrowest].LastDrawnWidth)
            {
                picked[narrowest] = folder;
            }
        }

        if (picked.Count == 0)
        {
            return;
        }

        var looks = new (string Path, long Ticks)[picked.Count];
        for (var index = 0; index < looks.Length; index++)
        {
            looks[index] = (picked[index].FullPath, picked[index].DirWriteTicks);
        }

        // A directory's time on a share is a round trip: never on this thread.
        ThreadPool.UnsafeQueueUserWorkItem(
            state =>
            {
                foreach (var (path, ticks) in state.Looks)
                {
                    Interlocked.Increment(ref state.Tree._pollsLooked);
                    var now = NestedDirectoryReader.DirectoryWriteTicks(path);
                    if (now != 0 && now != ticks)
                    {
                        // No time kept is not a change seen: that read waits
                        // its turn like a change heard.
                        Interlocked.Increment(ref state.Tree._pollsChanged);
                        state.Hub.Touch(path, immediate: ticks != 0);
                    }
                }
            },
            (Tree: this, Hub: hub, Looks: looks),
            preferLocal: false);
    }

    /// <summary>
    /// The folders of <paramref name="root"/> that are on screen, found without
    /// looking at the rest: down from its drives and roots, into a sub-folder
    /// only when it was drawn lately or the view is inside it - a cell bigger
    /// than the view is never drawn as one, and the folders on screen are
    /// inside it.  However many folders were read, this visits about as many
    /// as the last picture drew.
    /// </summary>
    private IEnumerable<NestedFolder> DrawnFoldersOf(WatchRoot root)
    {
        var aroundView = new HashSet<NestedFolder>();
        for (var folder = CameraAnchor; folder is not null; folder = folder.Parent)
        {
            aroundView.Add(folder);
        }

        var stack = new Stack<NestedFolder>();
        foreach (var drive in Root.AllChildren)
        {
            if (ReferenceEquals(drive.Watch, root))
            {
                stack.Push(drive);
            }
        }

        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            yield return folder;
            foreach (var child in folder.Children)
            {
                if (WasDrawnRecently(child) || aroundView.Contains(child))
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// A change in <paramref name="folder"/>.  What it is decides what it costs:
    /// <list type="bullet">
    /// <item>the folder itself went: nothing, unless the view is on it or
    /// inside it, when its parent is read again at once so it leaves the
    /// screen - its parent's own change would be read only if the parent's
    /// cell were drawn, and a cell bigger than the view never is - and a
    /// look at whether its name is a folder's again, which has it read
    /// again if so.  The folder itself is never read for it, whatever went
    /// on inside it as it went: a read would only fail.  Gone with a folder
    /// above it, nothing at all: that folder's own change does it;</item>
    /// <item>files grew or were written to, and the hub knows their new sizes
    /// and times: those are put into the listing as they are, with no read;</item>
    /// <item>only a sub-folder's own date moved: nothing, unless the folders
    /// are ordered by date, or the sub-folder was hidden or shown by its
    /// attributes - or more sub-folders moved than the change could list,
    /// any of which may have been - when that is a change like any other;</item>
    /// <item>anything else: the folder is marked out of date, and if it was
    /// drawn in the last few frames it is queued to be read again at once, at
    /// the priority its size on screen gives it.  It is drawn as it was until
    /// the new listing is applied.</item>
    /// </list>
    /// A rename within the folder is remembered, so the renamed folder can take
    /// its listing with it when the folder's new listing comes; and a renamed
    /// sub-folder's own order goes to its new name, in a folder reached by
    /// name and never listed too.
    /// </summary>
    internal void OnFolderChanged(NestedFolder folder, in FolderChange change)
    {
        if (_disposed || folder.IsComputer || IsDetached(folder))
        {
            return;
        }

        LiveChangesTaken++;
        var kinds = change.Kinds;
        if ((kinds & ChangeKinds.Gone) != 0)
        {
            if (folder.Parent is { IsComputer: false, HasPartialListing: true, IsLoaded: false })
            {
                // A named navigation can have loaded the target without ever
                // listing its parent. No later parent refresh can remove it.
                // Verify only the known names rather than enumerate ancestors.
                _ = CheckSparseGoneAsync(folder);
                return;
            }

            // Gone with a folder above it, the parent went too and cannot be
            // read: the folder that went is told by its own change, and its
            // parent - still there - is what is read again.
            if ((kinds & ChangeKinds.AncestorGone) == 0
                && folder.Parent is { IsComputer: false } parent && parent.IsLoaded)
            {
                if (IsInView(folder))
                {
                    Refresh(parent);
                }

                // The parent's listing matches its folders by name, so a name
                // taken again - renamed away and made anew, two folders
                // swapped - would keep this folder, with what was read of the
                // one that went.  Asked about when the parent's next listing
                // still holds it; at once only when nothing is on its way to
                // the parent, whose listing may already be one taken since.
                if (NoteGone(folder) && !parent.NeedsRefresh && parent.QueuedRead == ReadKind.None)
                {
                    _ = CheckNameTakenAsync(folder);
                }

                // Deleted with what was in it - Shift+Del, rm -rf - the folder
                // hears its contents go too, and read for that it would only
                // fail, and be drawn as a red cell saying it no longer exists
                // until its parent's listing takes it off.  A folder made anew
                // under its name is caught above.
                return;
            }

            if ((kinds & ~(ChangeKinds.Gone | ChangeKinds.AncestorGone)) == 0)
            {
                return;
            }
        }

        if (!change.Renames.IsEmpty)
        {
            NoteRenames(folder, change.Renames);
        }

        // Files patched in place leave everything else as it was: not taken
        // when a sub-folder's own date moved in the same change and the
        // folder is ordered by date, which only a read puts right.  A
        // sub-folder hidden or shown by its attributes comes as its date
        // moving, and only a read puts it in or takes it out - and when more
        // moved than the change could list, attrib +h * /d on a dozen, which
        // did cannot be told without one.
        var structural = (kinds & ChangeKinds.Structural) != 0
            || (kinds & ChangeKinds.DirDate) != 0 && (change.FoldersIncomplete || HiddenChanged(folder, change.Folders.Span));
        var byDate = SortOf(folder).Column == SortColumn.Modified;
        if (!structural && (kinds & ChangeKinds.Content) != 0 && ((kinds & ChangeKinds.DirDate) == 0 || !byDate)
            && TryPatchFiles(folder, change.Files.Span))
        {
            return;
        }

        if (!structural && (kinds & ChangeKinds.Content) == 0 && !byDate)
        {
            return;
        }

        folder.IsStale = true;
        if ((folder.IsLoaded || folder.LoadState == NestedLoadState.Failed) && WasDrawnRecently(folder))
        {
            LiveRereadsAsked++;
            Request(folder, folder.LastDrawnWidth);
        }
        else
        {
            LiveMarkedOnly++;
        }
    }

    private async Task CheckSparseGoneAsync(NestedFolder folder)
    {
        if (!_sparseGoneChecks.Add(folder)) return;
        try
        {
            var known = new List<NestedFolder> { folder };
            for (var parent = folder.Parent; parent is { IsComputer: false, HasPartialListing: true };
                 parent = parent.Parent)
            {
                // Drive roots are refreshed by the drive collection, not by
                // a descendant disappearing from a removable or offline disk.
                if (parent.Parent?.IsComputer == true) break;
                known.Add(parent);
            }

            var stale = await _pathFileSystem.FindStaleAsync(known.Select(node => node.FullPath).ToArray(), _lifetimeToken);
            if (_disposed || IsDetached(folder)) return;

            for (var index = known.Count - 1; index >= 0; index--)
            {
                if (!stale[index]) continue;
                var gone = known[index];
                if (IsDetached(gone) || gone.Parent is not { IsComputer: false } parent) continue;
                if (!parent.HasPartialListing || parent.IsLoaded)
                {
                    if (parent.IsLoaded && IsInView(gone)) Refresh(parent);
                    return;
                }

                var at = Array.IndexOf(parent.AllChildren, gone);
                if (at < 0) return;
                var remaining = new NestedFolder[parent.AllChildren.Length - 1];
                Array.Copy(parent.AllChildren, 0, remaining, 0, at);
                Array.Copy(parent.AllChildren, at + 1, remaining, at, remaining.Length - at);
                parent.AllChildren = remaining;
                _knownCount--;
                Forget(gone);
                ApplyVisibleChildren(parent);
                RaiseChanged();
                return;
            }

            // A burst may remove and recreate the same path before the hub
            // hands it over. Keep its identity, but reread its own listing.
            folder.IsStale = true;
            if (folder.IsLoaded && IsInView(folder)) Refresh(folder);
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeToken.IsCancellationRequested)
        {
        }
        finally
        {
            _sparseGoneChecks.Remove(folder);
        }
    }

    /// <summary>
    /// Whether the name of a folder whose own path went is a folder's again,
    /// asked off this thread: if it is, the folder is read again as the one
    /// now there (<see cref="TakeName"/>).  Asked whenever a listing of the
    /// parent still holds the folder - a name taken seconds later, while
    /// nothing drew the parent, is caught when the parent is next read.
    /// </summary>
    private async Task CheckNameTakenAsync(NestedFolder folder)
    {
        try
        {
            var stale = await _pathFileSystem.FindStaleAsync([folder.FullPath], _lifetimeToken);
            if (!stale[0] && !_disposed && !IsDetached(folder) && _goneByPath.Contains(folder))
            {
                TakeName(folder);
            }
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks again about the folders of <paramref name="parent"/> whose own path went and that its listing just applied still holds.</summary>
    private void CheckNamesTaken(NestedFolder parent)
    {
        if (!_goneByParent.TryGetValue(parent, out var gone))
        {
            return;
        }

        foreach (var folder in gone.ToArray())
        {
            _ = CheckNameTakenAsync(folder);
        }
    }

    /// <summary>
    /// Notes a folder whose own path went (<see cref="_goneByPath"/>), under
    /// its parent; false when it is not noted, being below a folder noted
    /// already.  Deleted with all that was read in it - npm ci, cargo clean,
    /// rm -rf - every read folder of the subtree is heard to go, deepest
    /// first, and each one noted lets go of those noted below it: only a
    /// listing of a folder that is still there could ask about them, and a
    /// folder above that went has none, while one made anew under its name
    /// has everything read below it marked out of date (<see cref="TakeName"/>).
    /// </summary>
    private bool NoteGone(NestedFolder folder)
    {
        for (var above = folder.Parent; above is not null; above = above.Parent)
        {
            if (_goneByPath.Contains(above))
            {
                return false;
            }
        }

        if (!_goneByPath.Add(folder))
        {
            return true;
        }

        if (folder.Parent is { } parent)
        {
            if (!_goneByParent.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                _goneByParent[parent] = siblings;
            }

            siblings.Add(folder);
        }

        if (_goneByParent.Remove(folder, out var below))
        {
            foreach (var child in below)
            {
                _goneByPath.Remove(child);
            }
        }

        return true;
    }

    /// <summary>Takes a folder off <see cref="_goneByPath"/>, and off its parent's entry with it.</summary>
    private void ForgetGone(NestedFolder folder)
    {
        if (!_goneByPath.Remove(folder) || folder.Parent is not { } parent || !_goneByParent.TryGetValue(parent, out var siblings))
        {
            return;
        }

        siblings.Remove(folder);
        if (siblings.Count == 0)
        {
            _goneByParent.Remove(parent);
        }
    }

    /// <summary>
    /// The folder's name is another folder's now: what was read in it and
    /// below it belongs to the one that went.  On screen it is read again at
    /// once, and what was read below it as it is drawn, as F5 does.  Off
    /// screen it and everything read below it are only marked out of date,
    /// and read when drawn - the folder itself at once only when a read
    /// slot is free, never ahead of the folders on screen: read at once
    /// whatever it took, a deleted and remade subtree - npm ci, a clean
    /// build - had every folder of it read before anything on screen, each
    /// level's listing asking about the next.  Whatever was noted below it
    /// goes with it: marked out of date, those folders are read again anyway.
    /// </summary>
    private void TakeName(NestedFolder folder)
    {
        ForgetGone(folder);
        if (_goneByPath.Count > 0)
        {
            List<NestedFolder>? below = null;
            foreach (var gone in _goneByPath)
            {
                if (folder.Contains(gone))
                {
                    (below ??= []).Add(gone);
                }
            }

            if (below is not null)
            {
                foreach (var gone in below)
                {
                    ForgetGone(gone);
                }
            }
        }

        if (IsInView(folder))
        {
            RefreshDeep(folder);
            return;
        }

        if (_disposed || IsDetached(folder))
        {
            return;
        }

        folder.IsStale = true;
        lock (_gate)
        {
            if (folder.LoadState is not (NestedLoadState.NotLoaded or NestedLoadState.Queued))
            {
                EnqueueLocked(folder, ReadKind.Refresh, sticky: false);
            }
        }

        MarkStaleBelow(folder);
    }

    /// <summary>
    /// Whether the folder was drawn large enough to be read in the last few
    /// pictures (<see cref="ExpireAfterFrames"/>): on screen now, as near as
    /// the tree can tell - at rest the picture is the last one drawn.
    /// </summary>
    private bool WasDrawnRecently(NestedFolder folder) => folder.LastDrawnFrame >= Frame - ExpireAfterFrames;

    /// <summary>Drawn lately, or the view is somewhere inside it.</summary>
    private bool IsInView(NestedFolder folder) =>
        WasDrawnRecently(folder) || CameraAnchor is { } anchor && folder.Contains(anchor);

    /// <summary>
    /// New sizes and times for files already in the listing, from the hub's
    /// record of the change, put in without reading the folder: a log growing,
    /// a download filling in.  The listing is copied rather than written to -
    /// the array may be held by what drew it - and the folder is placed again,
    /// which under an order by size or date moves the files; the folder then
    /// goes out with the next batch of applied reads, so the canvas redraws its
    /// cell like any folder read.  False when there is nothing to put in, a
    /// file is not in the listing, a file was hidden and no longer is, or a
    /// read of the folder is on its way.
    /// </summary>
    private bool TryPatchFiles(NestedFolder folder, ReadOnlySpan<FileDelta> deltas)
    {
        if (deltas.IsEmpty || deltas.Length > 64 || !folder.IsLoaded || folder.QueuedRead != ReadKind.None)
        {
            return false;
        }

        var files = folder.AllFiles;
        Span<int> places = stackalloc int[deltas.Length];
        for (var index = 0; index < deltas.Length; index++)
        {
            var place = SearchFiles(files, deltas[index].Name);
            if (place < 0 || !string.Equals(files[place].Name, deltas[index].Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // The watch leaves out a file that became hidden or system, but
            // one that stopped being so - attrib -h - comes like any other
            // change: shown now, or counted apart no more, it is placed again
            // only by a read.
            var hidden = (deltas[index].Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            if (hidden != files[place].IsHidden)
            {
                return false;
            }

            places[index] = place;
        }

        var patched = (NestedFile[])files.Clone();
        for (var index = 0; index < deltas.Length; index++)
        {
            var file = patched[places[index]];
            patched[places[index]] = new NestedFile(file.Name, file.IsHidden, deltas[index].Length, deltas[index].ModifiedTicks);
        }

        folder.AllFiles = patched;
        ApplyVisibleChildren(folder);
        LivePatches++;

        // Announced with the next drain of finished reads - in this very frame
        // when the canvas takes the hub's changes in, otherwise on the wake.
        _batch.Applied.Add(folder);
        Driver.Wake();
        return true;
    }

    /// <summary>
    /// Whether a sub-folder whose own entry changed is hidden now where the
    /// listing has it shown, or shown where it has it hidden: hidden or shown
    /// by its attributes - attrib +h, the Hidden box in Properties - which the
    /// watch tells only as its date moving.  A binary search per sub-folder
    /// the change names, eight at most.
    /// </summary>
    private static bool HiddenChanged(NestedFolder folder, ReadOnlySpan<FileDelta> folders)
    {
        foreach (var delta in folders)
        {
            if (FindChild(folder, delta.Name) is { } child
                && child.IsHidden != ((delta.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0))
            {
                return true;
            }
        }

        return false;
    }

    // ---- F5 -------------------------------------------------------------------------------

    /// <summary>
    /// Everything read in and below <paramref name="folder"/> is out of date -
    /// the user said so with F5.  The folder itself is read again at once;
    /// below it, every folder that was read is marked in a walk of background
    /// slices, and the ones on screen are queued as the walk reaches them.
    /// The rest are read when they are next drawn: F5 on a drive read fifty
    /// thousand folders deep reads what the user can see, not the drive.
    /// </summary>
    public void RefreshDeep(NestedFolder folder)
    {
        if (_disposed || IsDetached(folder))
        {
            return;
        }

        Refresh(folder);
        MarkStaleBelow(folder);
    }

    /// <summary>Has every folder read below <paramref name="folder"/> marked out of date in the walk of background slices, the ones on screen queued as it reaches them.</summary>
    private void MarkStaleBelow(NestedFolder folder)
    {
        foreach (var child in folder.AllChildren)
        {
            if (child.IsLoaded)
            {
                _staleSweep.Enqueue(child);
            }
        }

        if (_staleSweep.Count > 0 && !_staleSweepPosted)
        {
            _staleSweepPosted = true;
            PostSweep(RunStaleSweep);
        }
    }

    private void RunStaleSweep()
    {
        _staleSweepPosted = false;
        for (var taken = 0; taken < FoldersPerSweepSlice && _staleSweep.TryDequeue(out var folder); taken++)
        {
            if (_disposed || !folder.IsLoaded || IsDetached(folder))
            {
                continue;
            }

            folder.IsStale = true;
            if (WasDrawnRecently(folder))
            {
                Request(folder, folder.LastDrawnWidth);
            }

            foreach (var child in folder.AllChildren)
            {
                if (child.IsLoaded)
                {
                    _staleSweep.Enqueue(child);
                }
            }
        }

        if (_staleSweep.Count > 0 && !_disposed)
        {
            _staleSweepPosted = true;
            PostSweep(RunStaleSweep);
        }
    }

    // ---- registering ---------------------------------------------------------------

    /// <summary>
    /// A successful read is about to be applied: when it started, and the
    /// files the folder had, for what the hub is told afterwards.  Then the
    /// soft-update capture, which needs the folder as the last picture drew it.
    /// </summary>
    partial void BeforeApply(NestedFolder folder, ReadResult result)
    {
        _applyStarted = Stopwatch.GetTimestamp();
        _filesBeforeApply = folder.AllFiles;
        CaptureBeforeApply(folder, result);
    }

    /// <summary>
    /// A successful read was applied.  A folder renamed in it takes the listing
    /// it had under its old name; one whose own path went and that it still
    /// holds is asked about again, in case its name was taken; the folder is
    /// registered with the hub, if it was not already; and a refresh tells
    /// the hub what it cost and whether anything changed, which sets how soon
    /// the folder may be read again and, on a share, whether to look once
    /// more in case the share's cache was behind.  Then the soft-update
    /// capture.
    /// </summary>
    partial void AfterApply(NestedFolder folder, ReadResult result)
    {
        if (_renames.Count > 0 && _renames.TryGetValue(folder, out var renames))
        {
            CarryOverRenames(folder, result, renames);
        }

        if (_goneByParent.Count > 0)
        {
            CheckNamesTaken(folder);
        }

        Register(folder);
        if (result.Kind == ReadKind.Refresh && _changes is { } hub)
        {
            var applyMilliseconds = Stopwatch.GetElapsedTime(_applyStarted).TotalMilliseconds;
            var changed = result.Removed.Length > 0
                || result.Children.Length != result.Basis.Length
                || !SameFiles(_filesBeforeApply, folder.AllFiles);
            hub.ReportRefresh(folder.FullPath, result.ReadMilliseconds, applyMilliseconds, changed);
        }

        _filesBeforeApply = [];
        CaptureAfterApply(folder, result);
    }

    /// <summary>
    /// A folder's first read was queued: it is registered with the hub now,
    /// not once the read is applied.  A file written after the listing was
    /// taken and before it is applied - a download finishing, an archive
    /// unpacking its last file - then marks the folder stale, and it is read
    /// again, where before it was dropped as a change in a folder nobody
    /// watched and never shown.
    /// </summary>
    partial void OnFirstReadQueued(NestedFolder folder) => Register(folder);

    /// <summary>
    /// The soft-update capture before a successful read is applied
    /// (NestedTree.Transitions.cs): <see cref="BeforeApply"/> is taken here,
    /// by the live registry, and hands on to this straight away.
    /// </summary>
    partial void CaptureBeforeApply(NestedFolder folder, ReadResult result);

    /// <summary>
    /// The soft-update capture after a successful read was applied and placed
    /// (NestedTree.Transitions.cs), last of what <see cref="AfterApply"/> does.
    /// </summary>
    partial void CaptureAfterApply(NestedFolder folder, ReadResult result);

    /// <summary>
    /// A folder was dropped - gone from its parent's listing, replaced, or its
    /// drive removed: it is taken off the hub now, and whatever was read below
    /// it is taken off in background slices.  Dropping marks only the folder
    /// itself, and walking a subtree of thousands in the middle of an apply
    /// was the slow part of a refresh; left registered, what is below would
    /// only cost the hub a look that finds it cut off.
    /// </summary>
    partial void OnForgotten(NestedFolder folder)
    {
        _renames.Remove(folder);
        ForgetGone(folder);
        if (_goneByParent.Remove(folder, out var gone))
        {
            foreach (var child in gone)
            {
                _goneByPath.Remove(child);
            }
        }

        if (_registered.Remove(folder))
        {
            _changes?.Unregister(ChangeConsumer.Nested, folder.FullPath, folder);
        }

        if (folder.AllChildren.Length > 0 && _registered.Count > 0)
        {
            _droppedSweep.Enqueue(folder);
            if (!_droppedSweepPosted)
            {
                _droppedSweepPosted = true;
                PostSweep(RunDroppedSweep);
            }
        }
    }

    private void RunDroppedSweep()
    {
        _droppedSweepPosted = false;
        for (var taken = 0; taken < FoldersPerSweepSlice && _droppedSweep.TryDequeue(out var folder); taken++)
        {
            foreach (var child in folder.AllChildren)
            {
                if (_registered.Remove(child))
                {
                    _changes?.Unregister(ChangeConsumer.Nested, child.FullPath, child);
                }

                // A rename waiting for a listing that will never come would
                // keep the folder, and the branch it is in, alive.
                _renames.Remove(child);
                ForgetGone(child);
                if (child.AllChildren.Length > 0)
                {
                    _droppedSweep.Enqueue(child);
                }
            }
        }

        if (_droppedSweep.Count > 0)
        {
            _droppedSweepPosted = true;
            PostSweep(RunDroppedSweep);
        }
    }

    /// <summary>Queues a slice of one of the walks above as the background pass after a change of order queues its slices, or runs it now where nothing can queue it.</summary>
    private void PostSweep(Action slice)
    {
        if (PostBackground is { } post)
        {
            post(slice);
        }
        else
        {
            slice();
        }
    }

    private void Register(NestedFolder folder)
    {
        if (_changes is not { } hub)
        {
            return;
        }

        if (_registered.Add(folder))
        {
            hub.Register(ChangeConsumer.Nested, folder.FullPath, folder);
        }

        // A folder reached by name hangs below folders that were never
        // listed, which nothing else registers: one of them renamed, moved
        // or recycled would take the folder with it unheard.  Registered, its
        // own path going is heard, and checked as for any folder named below
        // an unlisted one (CheckSparseGoneAsync).  The drive or root at the
        // top is the drive list's to keep.
        for (var parent = folder.Parent;
             parent is { IsComputer: false, HasPartialListing: true, IsLoaded: false, Parent.IsComputer: false } && _registered.Add(parent);
             parent = parent.Parent)
        {
            hub.Register(ChangeConsumer.Nested, parent.FullPath, parent);
        }
    }

    /// <summary>Registers every folder read so far, for a hub given to a tree that has already read some.</summary>
    private void RegisterReadFolders()
    {
        var stack = new Stack<NestedFolder>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            if (!folder.IsComputer && folder.IsLoaded)
            {
                Register(folder);
            }

            foreach (var child in folder.AllChildren)
            {
                stack.Push(child);
            }
        }
    }

    private static bool SameFiles(NestedFile[] before, NestedFile[] after)
    {
        if (ReferenceEquals(before, after))
        {
            return true;
        }

        if (before.Length != after.Length)
        {
            return false;
        }

        for (var index = 0; index < before.Length; index++)
        {
            var old = before[index];
            var now = after[index];
            if (old.Length != now.Length
                || old.ModifiedTicks != now.ModifiedTicks
                || old.IsHidden != now.IsHidden
                || !string.Equals(old.Name, now.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ---- renames ---------------------------------------------------------------------

    /// <summary>
    /// Remembers renames within a folder until its next listing is applied,
    /// a handful at most; a renamed sub-folder's own order, if it has one,
    /// goes to its new name at once (<see cref="FolderOrders.Move"/>).  A
    /// folder reached by name and never listed - a window opened on a path
    /// - has no listing for a renamed sub-folder to take along, and nothing
    /// is remembered; its sub-folders' orders move all the same.
    /// </summary>
    private void NoteRenames(NestedFolder folder, ReadOnlyMemory<RenamePair> pairs)
    {
        List<RenamePair>? noted = null;
        if (folder.IsLoaded && !_renames.TryGetValue(folder, out noted))
        {
            noted = [];
            _renames[folder] = noted;
        }

        var moveOrders = _orders.Count > 0 && FirstToMoveOrders(pairs);
        foreach (var pair in pairs.Span)
        {
            if (noted is { Count: < 16 })
            {
                noted.Add(pair);
            }

            // A sub-folder sorted by itself keeps its order under its new
            // name, as do the folders inside it.  Moved whether or not the
            // listing has the old name: renames in a chain - A to tmp, B to
            // A, tmp to B - name folders the listing has not caught up with,
            // and a name no order has is nothing to move.  Only a name the
            // listing has as a file is left out: its rename is no business
            // of the orders, and a lookup by name says which it is.
            if (moveOrders && !HoldsFile(folder, pair.OldName))
            {
                _orders.Move(Path.Combine(folder.FullPath, pair.OldName), Path.Combine(folder.FullPath, pair.NewName));
            }
        }
    }

    /// <summary>
    /// Whether the orders are still to be moved for <paramref name="pairs"/>.
    /// The panes of a split view share one set of orders, and each pane's
    /// tree is handed the very same change - the same renames - so the first
    /// tree to hear it moves them and the others leave them be: a rotation of
    /// names, B to C and A to B, moved twice would give C the order A had.
    /// </summary>
    private bool FirstToMoveOrders(ReadOnlyMemory<RenamePair> pairs)
    {
        if (!MemoryMarshal.TryGetArray(pairs, out var segment) || segment.Array is not { } array)
        {
            return true;
        }

        if (OrdersMovedFor.TryGetValue(_orders, out var moved) && ReferenceEquals(moved, array))
        {
            return false;
        }

        OrdersMovedFor.AddOrUpdate(_orders, array);
        return true;
    }

    /// <summary>
    /// A sub-folder renamed from outside would come back from its parent's
    /// read as a new folder, never read, with everything under it to read
    /// again.  Instead the new folder takes the listing the old one had, one
    /// level deep - its files, and its sub-folders made anew under the new
    /// path, unread - and is marked out of date, so it is drawn as it was at
    /// once and read again when it is drawn.  Renames the listing just applied
    /// does not show yet - a read that began before the rename - wait for the
    /// next one.  A new name the listing keeps a folder already read under -
    /// the name was another's, which went first - has that folder read again.
    /// </summary>
    private void CarryOverRenames(NestedFolder parent, ReadResult result, List<RenamePair> renames)
    {
        for (var index = renames.Count - 1; index >= 0; index--)
        {
            var pair = renames[index];
            var old = FindByName(result.Removed, pair.OldName);
            if (old is null)
            {
                // Not applied yet if the old name is still there; otherwise gone.
                if (FindChild(parent, pair.OldName) is null)
                {
                    renames.RemoveAt(index);
                }

                continue;
            }

            renames.RemoveAt(index);
            var renamed = FindChild(parent, pair.NewName);
            if (renamed is { LoadState: NestedLoadState.Loaded or NestedLoadState.Failed })
            {
                // The new name was a folder's that went first - an updater's
                // swap, current to old and new to current - and what was
                // read under it is that folder's.
                TakeName(renamed);
                continue;
            }

            if (!old.IsLoaded
                || renamed is not { LoadState: NestedLoadState.NotLoaded, QueuedRead: ReadKind.None }
                || renamed.IsReparsePoint != old.IsReparsePoint)
            {
                continue;
            }

            var children = new NestedFolder[old.AllChildren.Length];
            for (var child = 0; child < children.Length; child++)
            {
                var previous = old.AllChildren[child];
                children[child] = NestedFolder.ChildOf(renamed, previous.Name, previous.IsHidden, previous.IsReparsePoint, previous.ModifiedTicks);
            }

            renamed.AllChildren = children;
            renamed.AllFiles = old.AllFiles;
            renamed.FileCount = old.FileCount;
            renamed.HiddenFileCount = old.HiddenFileCount;
            renamed.IsTruncated = old.IsTruncated;
            renamed.ErrorMessage = string.Empty;
            renamed.LoadState = NestedLoadState.Loaded;
            renamed.IsStale = true;
            _knownCount += children.Length;
            ApplyVisibleChildren(renamed);
            Register(renamed);
            LiveCarriedOver++;
            FolderLoaded?.Invoke(renamed);
            _batch.Applied.Add(renamed);
        }

        if (renames.Count == 0)
        {
            _renames.Remove(parent);
        }
    }

    private static NestedFolder? FindByName(NestedFolder[] folders, string name)
    {
        foreach (var folder in folders)
        {
            if (string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }
}
