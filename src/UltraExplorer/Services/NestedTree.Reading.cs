using System.Diagnostics;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Services;

/// <summary>
/// What a batch of applied reads changed, for whoever redraws only what
/// changed: the folders whose listings were applied - read for the first
/// time, read again, or failed - and whether the drives and roots themselves
/// changed.  One instance per tree, filled again for every batch: a listener
/// reads it while the event is raised and keeps nothing of it.
/// </summary>
public sealed class NestedChangeBatch
{
    /// <summary>The folders applied in this batch, in the order they were applied.</summary>
    public List<NestedFolder> Applied { get; } = [];

    /// <summary>Whether the drives and extra roots were replaced (<see cref="NestedTree.SetRoots"/>).</summary>
    public bool RootsChanged { get; internal set; }

    internal void Clear()
    {
        Applied.Clear();
        RootsChanged = false;
    }
}

// Reading: the queue of folders the canvas asked for, the reads in the
// background, and applying what they found - on the tree's own thread,
// where the folders live - including a refresh, which keeps every child
// that is still there with everything read below it.
//
// The reads never come back to the UI thread one at a time.  A read runs on
// a worker, which also builds the child folders, puts the result in an inbox
// and goes straight on to the next folder waiting - no dispatcher operation,
// no round trip through the UI thread to start the next read.  Whoever drives
// the tree - the canvas at the start of each frame, or a dispatcher operation
// while no canvas draws it - takes the finished reads in within a slice of
// the frame, and raises one Changed for all of them.  Before this, each read
// resumed on the UI thread at whatever priority had started it, ahead of the
// input at Render priority or behind a stream of frames at Input, three at a
// time: a deep zoom at 4K applied one to three reads a frame and starved the
// input for seconds.
public sealed partial class NestedTree
{
    /// <summary>
    /// Reads in flight at once on local disks.  A read is mostly waiting on the
    /// file system's cache or the disk; eight keep an SSD busy without making
    /// a spinning disk seek much more than three did.
    /// </summary>
    public const int LocalReadSlots = 8;

    /// <summary>
    /// Reads in flight at once on one share.  A share that hangs holds its read
    /// for as long as the network takes to give up - twenty seconds and more -
    /// and only its own reads wait behind it.
    /// </summary>
    public const int NetworkSlotsPerShare = 1;

    /// <summary>
    /// Pictures a waiting read may go without being asked for again before it
    /// is dropped: the folder has left the screen, and a read of it would be
    /// work for nothing.  A read something is waiting for - a reveal, a
    /// refresh - is never dropped.
    /// </summary>
    public const int ExpireAfterFrames = 2;

    private readonly Func<string, CancellationToken, NestedListing> _reader;

    /// <summary>
    /// Guards the queue: the waiting folders, the slots in use, the waiters,
    /// and every change of a queued folder's <see cref="NestedFolder.QueuedRead"/>,
    /// <see cref="NestedFolder.LoadState"/> and read stamps.  Held for a few
    /// microseconds at a time, never while reading, applying or raising events.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>Folders waiting for a slot, in no order: each worker takes the most wanted one it may.</summary>
    private readonly List<NestedFolder> _pending = [];

    /// <summary>Reads in flight on each share, by share.</summary>
    private readonly Dictionary<string, int> _shareRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders someone is waiting on - a load, a refresh - and who.</summary>
    private readonly Dictionary<NestedFolder, ExplicitRead> _explicit = [];

    /// <summary>Waiters whose read was applied in this drain, told once the drain's events are raised.</summary>
    private readonly List<ReadWaiter> _completed = [];

    private readonly NestedChangeBatch _batch = new();

    private int _localRunning;
    private int _networkRunning;
    private FrameInbox<ReadResult> _results = null!;
    private Action<ReadPick> _runReads = null!;
    private int _picks;
    private long _pickTicks;
    private long _pickTicksMax;

    /// <summary>Reads waiting, running, or finished and not yet applied.</summary>
    public int PendingCount =>
        _pending.Count + Volatile.Read(ref _localRunning) + Volatile.Read(ref _networkRunning) + _results.Count;

    /// <summary>
    /// For the bench and tests: how often a freed slot looked through the
    /// queue for its next folder, and what that cost - the whole of it and
    /// the worst one.  A pass over the queue is cheap until the queue is very
    /// long; this says when it no longer is.
    /// </summary>
    internal (int Picks, double TotalMilliseconds, double WorstMilliseconds) PickCost
    {
        get
        {
            lock (_gate)
            {
                return (_picks, _pickTicks * 1000.0 / Stopwatch.Frequency, _pickTicksMax * 1000.0 / Stopwatch.Frequency);
            }
        }
    }

    /// <summary>
    /// Raised on the owning thread once for every batch of applied reads - all
    /// the reads one frame took in - just before the one <see cref="Changed"/>
    /// the batch raises, and when the drives and roots were replaced.
    /// </summary>
    public event Action<NestedChangeBatch>? BatchApplied;

    /// <summary>
    /// What wakes whoever takes the finished reads in: the canvas drawing the
    /// tree while there is one (<see cref="Controls.NestedCanvas.Drive"/>),
    /// otherwise a driver on the tree's own thread.
    /// </summary>
    internal FrameDriverSlot Driver { get; private set; } = null!;

    /// <summary>Whether finished reads are waiting to be applied: the canvas keeps its frame loop going while this is true.</summary>
    internal bool HasWork => !_results.IsEmpty;

    /// <summary>
    /// Applies the reads that have finished, as many as <paramref name="budget"/>
    /// allows, and says how many were applied: called at the start of every
    /// frame of the canvas drawing the tree, and by <see cref="Driver"/>'s
    /// fallback while none does.  One <see cref="BatchApplied"/> and one
    /// <see cref="Changed"/> for all of them; what does not fit waits for the
    /// next frame.  Whoever awaited a load or a refresh among them hears of it
    /// after the events, when the tree is as the next picture will show it.
    /// </summary>
    internal int DrainResults(ref FrameBudget budget)
    {
        _results.Rearm();
        var applied = 0;
        try
        {
            while (!budget.Spent && _results.TryTake(out var read))
            {
                budget.Take();
                if (Apply(read))
                {
                    applied++;
                }
            }

            if (_batch.Applied.Count > 0)
            {
                RaiseBatchApplied();
                RaiseChanged();
            }
        }
        finally
        {
            // Told even when a listener threw: a load awaited must never hang.
            foreach (var waiter in _completed)
            {
                waiter.Registration.Unregister();
                waiter.TrySetResult();
            }

            _completed.Clear();
        }

        return applied;
    }

    /// <summary>
    /// The reading side of the constructor: the inbox the workers post to, the
    /// driver it wakes, and what happens to waiting reads when the tree goes.
    /// </summary>
    private void InitialiseReading()
    {
        Driver = new FrameDriverSlot(DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => DrainResults(ref budget), () => HasWork));
        _results = new FrameInbox<ReadResult>(Driver);
        _runReads = RunReads;
        _lifetimeToken.UnsafeRegister(static tree => ((NestedTree)tree!).OnLifetimeEnded(), this);
    }

    /// <summary>
    /// A drive or extra root's folder, made the first time <see cref="SetRoots"/>
    /// is given it, with the watch on its volume: every folder below it takes
    /// that watch from its parent.
    /// </summary>
    private NestedFolder CreateRoot(NestedRoot root) =>
        new(root.FullPath, root.Name, root.Kind, Root, secondaryText: root.SecondaryText)
        {
            Watch = WatchRootFor?.Invoke(root.FullPath)
        };

    /// <summary>Raises the batch of the folders just applied, if anyone listens, and empties it for the next.</summary>
    private void RaiseBatchApplied()
    {
        try
        {
            BatchApplied?.Invoke(_batch);
        }
        finally
        {
            _batch.Clear();
        }
    }

    /// <summary>The drives and roots were replaced: a batch that says so.</summary>
    private void RaiseRootsApplied()
    {
        _batch.RootsChanged = true;
        RaiseBatchApplied();
    }

    // ---- hooks -----------------------------------------------------------------
    //
    // Points in reading and applying that other parts of the tree hang work on:
    // partial methods, written in those parts' own files, and nothing at all
    // where nobody has written one.

    /// <summary>
    /// A successful read is about to be applied to <paramref name="folder"/>:
    /// its children, grids and files are still what the last picture showed.
    /// Not called for a failed read, or for a folder dropped meanwhile.
    /// </summary>
    partial void BeforeApply(NestedFolder folder, ReadResult result);

    /// <summary>
    /// A successful read was applied to <paramref name="folder"/> and it is
    /// placed for the current order, before <see cref="FolderLoaded"/> is raised.
    /// </summary>
    partial void AfterApply(NestedFolder folder, ReadResult result);

    /// <summary>A folder was dropped from the tree: gone from a refreshed listing, replaced, or its root removed.</summary>
    partial void OnForgotten(NestedFolder folder);

    /// <summary>
    /// A folder's first read was queued, on the tree's own thread: what is
    /// heard in it from now on reaches it, even before the read is applied.
    /// </summary>
    partial void OnFirstReadQueued(NestedFolder folder);

    /// <summary>The drives and roots are about to be replaced by <paramref name="roots"/>; nothing has changed yet.</summary>
    partial void OnRootsChanging(IReadOnlyList<NestedRoot> roots);

    /// <summary>The drives and roots were replaced and placed, before the batch and <see cref="Changed"/> say so.</summary>
    partial void OnRootsChanged();

    // ---- asking --------------------------------------------------------------

    /// <summary>
    /// Asks for a folder to be read because the canvas is drawing it,
    /// <paramref name="priority"/> pixels wide.  Cheap to call every frame for
    /// every folder on screen: it notes that the folder was drawn, and a folder
    /// read and up to date, or already waiting or being read, only has its
    /// priority refreshed - no lock.  A folder never read is queued for its
    /// first read; one read before whose contents changed since
    /// (<see cref="NestedFolder.NeedsRefresh"/>) is queued for a refresh, and
    /// keeps being drawn as it was until the refresh is applied.  A link is
    /// never read on its own, but one read by name (<see cref="LoadAsync"/>)
    /// is kept up to date like any folder read.
    /// </summary>
    public void Request(NestedFolder folder, double priority)
    {
        var frame = Frame;
        folder.LastDrawnFrame = frame;
        folder.LastDrawnWidth = (float)priority;
        if (folder.IsComputer || folder.IsReparsePoint && !folder.IsLoaded || !IsReadingOnDemand || _disposed
            || folder.HasPartialListing && PartialListingReadAllowed?.Invoke(folder) == false)
        {
            return;
        }

        folder.RequestedFrame = frame;
        folder.Priority = priority;
        if (folder.QueuedRead != ReadKind.None)
        {
            return;
        }

        switch (folder.LoadState)
        {
            case NestedLoadState.Loaded when folder.NeedsRefresh:
                Enqueue(folder, ReadKind.Refresh);
                break;

            case NestedLoadState.NotLoaded:
                Enqueue(folder, ReadKind.Load);
                OnFirstReadQueued(folder);
                break;

            // A share that did not answer, a drive that was not ready: worth
            // another try once in a while.  Access denied is not.
            // A real change received after the failure is new evidence: a
            // removed folder may have been recreated, or a device returned.
            // Without one, transient failures retain the normal retry delay.
            case NestedLoadState.Failed when folder.NeedsRefresh
                || folder.IsRetryable && Stopwatch.GetElapsedTime(folder.FailedAt).TotalSeconds > 20:
                Enqueue(folder, ReadKind.Load);
                break;
        }
    }

    /// <summary>
    /// Reads every folder on the way to <paramref name="path"/> and returns the
    /// folder itself, or the deepest one that could be reached.  A folder asked
    /// for by name is shown even if it is hidden: naming it is the request.
    /// The reads go through the queue ahead of anything the canvas wants, and
    /// are taken in by the canvas's frames or, while no canvas draws the tree
    /// - the graph is showing, the window is minimised - by its fallback driver.
    /// </summary>
    public async Task<NestedFolder?> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var target = Key(path);
        var current = OwnerRoot(target);
        if (current is null)
        {
            return null;
        }

        ForceVisible([target]);
        if (target.Length > current.FullPath.Length)
        {
            var refreshed = false;
            foreach (var segment in target[current.FullPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = FindChild(current, segment);
                // A named path can already exist as a sparse physical chain.
                // Resolving beacons/selection must not enumerate its ancestors.
                if (next is null)
                {
                    await LoadAsync(current, cancellationToken);
                    next = FindChild(current, segment);
                }
                if (next is null && !refreshed && current.IsLoaded)
                {
                    // Asked for a folder the listing does not have: it may
                    // have been made since the parent was read.  Read it once
                    // more - but only for a folder that really is there, or
                    // every file mark would re-read its folder on every look.
                    var childPath = Path.Combine(current.FullPath, segment);
                    if (await Task.Run(() => Directory.Exists(childPath), cancellationToken))
                    {
                        refreshed = true;
                        await RefreshAsync(current, cancellationToken);
                        next = FindChild(current, segment);
                    }
                }

                if (next is null || !IsOnCanvas(next))
                {
                    break;
                }

                current = next;
            }
        }

        return current;
    }

    /// <summary>
    /// Reads one folder now, unless it already has been, and completes when
    /// the read is applied: queued ahead of whatever the canvas asks for, or
    /// joined if a read of it is already on its way.  A folder whose read
    /// failed is read again only once something changed in it or around it
    /// (<see cref="NestedFolder.NeedsRefresh"/>), as when it is drawn.
    /// Cancelled, a read that has not started is taken off the queue, and one
    /// under way is left to finish unapplied - the folder is unread again at
    /// once, as before it was asked for.
    /// </summary>
    public Task LoadAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        // Asked for by name, a junction is read like any folder: it is only
        // the canvas wandering into one on its own that is refused.
        if (folder.IsComputer || _disposed || IsDetached(folder))
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ReadWaiter waiter;
        lock (_gate)
        {
            if (folder.LoadState is NestedLoadState.Loaded
                || folder.LoadState is NestedLoadState.Failed && !folder.NeedsRefresh)
            {
                return Task.CompletedTask;
            }

            // Any read of it answers a load, even one that began before it was asked.
            waiter = new ReadWaiter(this, folder, needsFreshRead: false, cancellationToken);
            ExplicitFor(folder).Waiters.Add(waiter);
            EnqueueLocked(folder, ReadKind.Load, sticky: true);
        }

        waiter.Listen();
        OnFirstReadQueued(folder);
        return waiter.Task;
    }

    /// <summary>
    /// Reads a folder again and keeps every child that is still there - with
    /// everything already read below it - so a refresh moves nothing that did
    /// not change.  Completes when a read that began after this was asked for
    /// is applied: one already under way is let finish, and the folder is
    /// read once more after it.  A folder never read has nothing to refresh.
    /// </summary>
    public Task RefreshAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        if (folder.IsComputer || _disposed || IsDetached(folder))
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ReadWaiter waiter;
        lock (_gate)
        {
            if (folder.LoadState is NestedLoadState.NotLoaded or NestedLoadState.Queued)
            {
                return Task.CompletedTask;
            }

            waiter = new ReadWaiter(this, folder, needsFreshRead: false, cancellationToken);
            RefreshLocked(folder, waiter);
        }

        waiter.Listen();
        return waiter.Task;
    }

    /// <summary>
    /// <see cref="RefreshAsync"/> for whoever does not wait for it: F5, a
    /// change on disk the user is looking at.  A read already under way is let
    /// finish, and the folder is read once more after it.
    /// </summary>
    public void Refresh(NestedFolder folder)
    {
        if (folder.IsComputer || _disposed || IsDetached(folder))
        {
            return;
        }

        lock (_gate)
        {
            if (folder.LoadState is not (NestedLoadState.NotLoaded or NestedLoadState.Queued))
            {
                RefreshLocked(folder, null);
            }
        }
    }

    /// <summary>Forgets what was read below <paramref name="folder"/> so the canvas reads it afresh.</summary>
    public void Invalidate(NestedFolder folder)
    {
        foreach (var child in folder.AllChildren)
        {
            Forget(child);
        }

        folder.AllChildren = [];
        folder.Children = [];
        folder.Grid = NestedGrid.Empty;
        folder.AllFiles = [];
        folder.Files = [];
        folder.FileGrid = NestedFileGrid.Empty;

        // Nothing left to order: like a folder never read, until it is read again.
        folder.LayoutSortGeneration = -1;
        folder.LoadState = folder.IsComputer ? NestedLoadState.Loaded : NestedLoadState.NotLoaded;
        folder.ErrorMessage = string.Empty;
        RaiseChanged();
    }

    // ---- the queue -------------------------------------------------------------

    /// <summary>Queues a read the canvas asked for, or starts it at once if a slot is free.</summary>
    private void Enqueue(NestedFolder folder, ReadKind kind)
    {
        lock (_gate)
        {
            EnqueueLocked(folder, kind, sticky: false);
        }
    }

    /// <summary>
    /// Queues a read of <paramref name="folder"/> unless one is queued or under
    /// way already, and starts it at once if its lane has a free slot.  A free
    /// slot means nothing of that lane is waiting - a worker that frees one
    /// takes the next waiting folder itself - so the newcomer goes straight in
    /// without a look at the rest of the queue.
    /// </summary>
    private void EnqueueLocked(NestedFolder folder, ReadKind kind, bool sticky)
    {
        if (sticky)
        {
            folder.IsSticky = true;
        }

        if (folder.QueuedRead != ReadKind.None)
        {
            return;
        }

        folder.QueuedRead = kind;
        if (kind == ReadKind.Load)
        {
            folder.LoadState = NestedLoadState.Queued;
        }

        if (TryBeginLocked(folder, out var pick))
        {
            ThreadPool.UnsafeQueueUserWorkItem(_runReads, pick, preferLocal: false);
            return;
        }

        folder.QueueIndex = _pending.Count;
        _pending.Add(folder);
    }

    /// <summary>
    /// A refresh someone asked for by name: marks the folder stale, so it stays
    /// wanted whatever happens to this request, and makes sure a read that
    /// begins from now on is on its way - the one already waiting, now ahead of
    /// the canvas's, or a new one.  One under way began before the ask, so it
    /// is followed by another.
    /// </summary>
    private void RefreshLocked(NestedFolder folder, ReadWaiter? waiter)
    {
        folder.IsStale = true;
        var request = ExplicitFor(folder);
        if (folder.QueueIndex >= 0)
        {
            folder.IsSticky = true;
        }
        else if (folder.QueuedRead != ReadKind.None)
        {
            if (waiter is null)
            {
                request.Again = true;
            }
            else
            {
                waiter.NeedsFreshRead = true;
            }
        }
        else
        {
            EnqueueLocked(folder, ReadKind.Refresh, sticky: true);
        }

        if (waiter is not null)
        {
            request.Waiters.Add(waiter);
        }
    }

    private ExplicitRead ExplicitFor(NestedFolder folder)
    {
        if (!_explicit.TryGetValue(folder, out var request))
        {
            request = new ExplicitRead();
            _explicit[folder] = request;
        }

        return request;
    }

    /// <summary>
    /// Takes a slot in the folder's lane and stamps the read as begun, or says
    /// the lane is full.  The stamps are what a change arriving during the
    /// read is measured against: it sets the folder stale again, or moves the
    /// epoch on, and the folder is read once more.
    /// </summary>
    private bool TryBeginLocked(NestedFolder folder, out ReadPick pick)
    {
        var share = ShareOf(folder);
        if (share is null)
        {
            if (_localRunning >= LocalReadSlots)
            {
                pick = default;
                return false;
            }

            _localRunning++;
        }
        else
        {
            _shareRunning.TryGetValue(share, out var running);
            if (running >= NetworkSlotsPerShare)
            {
                pick = default;
                return false;
            }

            _shareRunning[share] = running + 1;
            _networkRunning++;
        }

        folder.ReadTicket++;
        folder.ReadEpoch = folder.Watch is { } watch ? Volatile.Read(ref watch.Epoch) : 0;
        folder.IsStale = false;
        if (folder.QueuedRead == ReadKind.Load)
        {
            folder.LoadState = NestedLoadState.Loading;
        }

        pick = new ReadPick(folder, folder.QueuedRead, folder.ReadTicket, share);
        return true;
    }

    /// <summary>Gives back the slot a finished read held.</summary>
    private void EndLocked(in ReadPick pick)
    {
        if (pick.Share is null)
        {
            _localRunning--;
            return;
        }

        _networkRunning--;
        if (_shareRunning[pick.Share] <= 1)
        {
            _shareRunning.Remove(pick.Share);
        }
        else
        {
            _shareRunning[pick.Share]--;
        }
    }

    /// <summary>
    /// The folder a freed slot reads next: of those whose lane has room, one
    /// something is waiting for, then the largest on screen.  Folders not asked
    /// for in the last <see cref="ExpireAfterFrames"/> pictures have left the
    /// screen and are dropped on the way: a first read goes back to waiting to
    /// be drawn, a refresh stays stale and is read when the folder is drawn
    /// again.  Nothing here walks up the tree - whether a folder was cut off
    /// meanwhile is seen when its read is applied - so a pick costs one pass
    /// over the queue.
    /// </summary>
    private bool TryPickLocked(out ReadPick pick)
    {
        var started = Stopwatch.GetTimestamp();
        var picked = TryPickBestLocked(out pick);
        var spent = Stopwatch.GetTimestamp() - started;
        _picks++;
        _pickTicks += spent;
        _pickTicksMax = Math.Max(_pickTicksMax, spent);
        return picked;
    }

    private bool TryPickBestLocked(out ReadPick pick)
    {
        var oldest = Frame - ExpireAfterFrames;
        var localFree = _localRunning < LocalReadSlots;
        NestedFolder? best = null;
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            var candidate = _pending[index];
            if (!candidate.IsSticky && candidate.RequestedFrame < oldest)
            {
                RemoveLocked(candidate);
                if (candidate.QueuedRead == ReadKind.Load)
                {
                    candidate.LoadState = NestedLoadState.NotLoaded;
                }

                candidate.QueuedRead = ReadKind.None;
                continue;
            }

            // Only a folder that would be the best so far has its lane looked at.
            var better = best is null
                || candidate.IsSticky && !best.IsSticky
                || candidate.IsSticky == best.IsSticky && candidate.Priority > best.Priority;
            if (better && (ShareOf(candidate) is { } share
                    ? _shareRunning.GetValueOrDefault(share) < NetworkSlotsPerShare
                    : localFree))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            pick = default;
            return false;
        }

        RemoveLocked(best);
        return TryBeginLocked(best, out pick);
    }

    /// <summary>
    /// Takes a folder out of the waiting list by moving the last one into its
    /// place: the list has no order to keep, and a pick walking it from the end
    /// has already looked at the one it moves.
    /// </summary>
    private void RemoveLocked(NestedFolder folder)
    {
        var index = folder.QueueIndex;
        var last = _pending.Count - 1;
        var moved = _pending[last];
        _pending[index] = moved;
        moved.QueueIndex = index;
        _pending.RemoveAt(last);
        folder.QueueIndex = -1;
    }

    /// <summary>
    /// The share whose lane a folder's read goes in, or null for the local
    /// lane: the watch's own key when the folder has one, otherwise the share
    /// its path is on - a UNC path's, or a mapped drive letter's.
    /// </summary>
    private static string? ShareOf(NestedFolder folder) =>
        folder.Watch is { } watch
            ? watch.IsNetwork ? watch.Key : null
            : VolumeKinds.ShareKey(folder.FullPath);

    // ---- the workers -----------------------------------------------------------

    /// <summary>
    /// One slot's worker, on the thread pool: reads the folder it was given,
    /// posts the result, and takes the next folder waiting for its lane itself,
    /// until none is left.  Nothing here waits for the UI thread.
    /// </summary>
    private void RunReads(ReadPick pick)
    {
        while (true)
        {
            _results.Post(Read(pick));
            lock (_gate)
            {
                EndLocked(pick);
                if (_lifetimeToken.IsCancellationRequested || !TryPickLocked(out pick))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// What a read brings back: the listing, and the child folders already
    /// built from it - for the hooks, which folders came, stayed and went.
    /// </summary>
    /// <param name="Folder">The folder read.</param>
    /// <param name="Kind">Its first read, or a refresh.</param>
    /// <param name="Listing">What the reader found; an error message when the read failed.</param>
    /// <param name="Children">The sub-folders the listing names, in its order: the ones already there carried over as they were.</param>
    /// <param name="Removed">Sub-folders that were there and are not any more, or became something else.</param>
    /// <param name="Basis">The sub-folders the listing was matched against: the folder's as the read began.</param>
    /// <param name="ReadMilliseconds">How long the read and building the children took, on the worker.</param>
    private sealed record ReadResult(
        NestedFolder Folder,
        ReadKind Kind,
        NestedListing Listing,
        NestedFolder[] Children,
        NestedFolder[] Removed,
        NestedFolder[] Basis,
        double ReadMilliseconds)
    {
        /// <summary>The folder's <see cref="NestedFolder.ReadTicket"/> when the read began: a different one now means it was given up on.</summary>
        public int Ticket { get; init; }

        /// <summary>The directory's own last-write time taken before the read, or zero (<see cref="NestedFolder.DirWriteTicks"/>).</summary>
        public long DirectoryWriteTicks { get; init; }

        /// <summary>The read was cancelled: the tree is going away, or the reader gave up.</summary>
        public bool IsCancelled { get; init; }
    }

    /// <summary>
    /// Reads a folder and, still off the UI thread, builds its child objects:
    /// a folder of twenty-five thousand sub-folders is twenty-five thousand
    /// objects to make, and doing that in the dispatcher was a visible hitch in
    /// the middle of a zoom.  Children that were there before are carried over
    /// as they are, with everything read below them.  Whatever goes wrong,
    /// the folder gets a result: a read left "reading" is never read again.
    /// </summary>
    private ReadResult Read(in ReadPick pick)
    {
        var folder = pick.Folder;
        var started = Stopwatch.GetTimestamp();
        var basis = folder.AllChildren;
        var directoryTicks = 0L;
        NestedListing listing;
        NestedFolder[] children = basis;
        NestedFolder[] removed = [];
        try
        {
            // Picked just before the tree went away: nobody will look.
            _lifetimeToken.ThrowIfCancellationRequested();
            var path = folder.FullPath;

            // Where changes are not watched as they happen, the directory's own
            // time is what polling compares: a volume that refuses a watch, a
            // share - whose watch is let go when nothing shows it - and any
            // volume whose watch is down, which is polled until it is back.
            if (folder.Watch is { } watch
                && (watch.Kind == WatchKind.Polling || watch.IsNetwork || watch.Kind != WatchKind.None && watch.State != WatchState.Armed))
            {
                directoryTicks = NestedDirectoryReader.DirectoryWriteTicks(path);
            }

            listing = _reader(path, _lifetimeToken);
            if (string.IsNullOrEmpty(listing.ErrorMessage))
            {
                // Ordered by type, placing the folder asks the Shell for the
                // name of every kind of file in it.  A kind seen for the first
                // time is asked about here, off the UI thread, so placing it
                // there finds every answer waiting.
                if (_warmTypeNames)
                {
                    FileTypeNames.Warm(listing.Files);
                }

                (children, removed) = Prepare(folder, basis, listing);
            }
        }
        catch (OperationCanceledException)
        {
            return new ReadResult(folder, pick.Kind, NestedListing.Failed("Cancelled"), basis, [], basis, 0)
            {
                Ticket = pick.Ticket,
                IsCancelled = true
            };
        }
        catch (Exception ex)
        {
            listing = NestedListing.Failed(ex.Message) with { IsRetryable = true };
        }

        return new ReadResult(folder, pick.Kind, listing, children, removed, basis, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        {
            Ticket = pick.Ticket,
            DirectoryWriteTicks = directoryTicks
        };
    }

    /// <summary>
    /// Matches a listing against the sub-folders the folder had: a name still
    /// there keeps its folder, with everything read below it, unless it became
    /// a link or hidden; every other name is a new folder.  Off the UI thread.
    /// </summary>
    private static (NestedFolder[] Children, NestedFolder[] Removed) Prepare(NestedFolder folder, NestedFolder[] basis, NestedListing listing)
    {
        Dictionary<string, NestedFolder>? existing = null;
        if (basis.Length > 0)
        {
            existing = new Dictionary<string, NestedFolder>(basis.Length, StringComparer.Ordinal);
            foreach (var child in basis)
            {
                existing[child.Name] = child;
            }
        }

        var children = new NestedFolder[listing.Folders.Count];
        List<NestedFolder>? replaced = null;
        for (var index = 0; index < children.Length; index++)
        {
            var entry = listing.Folders[index];
            if (existing is not null && existing.Remove(entry.Name, out var kept))
            {
                if (kept.IsReparsePoint == entry.IsReparsePoint && kept.IsHidden == entry.IsHidden)
                {
                    children[index] = kept;
                    continue;
                }

                // Became a link, or hidden: a different cell now, and the old
                // one - with what was read below it - has to go.
                replaced ??= [];
                replaced.Add(kept);
            }

            children[index] = NestedFolder.ChildOf(folder, entry.Name, entry.IsHidden, entry.IsReparsePoint, entry.ModifiedTicks);
        }

        NestedFolder[] removed = existing is null ? [] : [.. existing.Values, .. replaced ?? []];
        return (children, removed);
    }

    /// <summary>
    /// Of the sub-folders a listing matched again does not name, the ones
    /// named after its read began - a navigation to a folder just made,
    /// described by name while its parent was being read.  The listing was
    /// taken before they were there, and cannot say they have gone: they are
    /// kept, with whatever was found below them, until a read that began
    /// after them says otherwise.
    /// </summary>
    /// <param name="basis">The sub-folders as the read began.</param>
    /// <param name="children">The sub-folders the listing names.</param>
    /// <param name="removed">The sub-folders the listing dropped or replaced.</param>
    private static NestedFolder[] NamedSince(NestedFolder[] basis, NestedFolder[] children, NestedFolder[] removed)
    {
        List<NestedFolder>? named = null;
        HashSet<NestedFolder>? before = null;
        HashSet<string>? listed = null;
        foreach (var child in removed)
        {
            before ??= [.. basis];
            if (before.Contains(child))
            {
                continue;
            }

            // One the listing names as something else - a link now, or
            // hidden - is replaced by what the listing says, as ever.
            listed ??= new HashSet<string>(children.Select(listedChild => listedChild.Name), StringComparer.Ordinal);
            if (!listed.Contains(child.Name))
            {
                (named ??= []).Add(child);
            }
        }

        return named is null ? [] : [.. named];
    }

    /// <summary>The sub-folders a listing names with the ones named since its read began, in the listing's order.</summary>
    private static NestedFolder[] WithNamedSince(NestedFolder[] children, NestedFolder[] namedSince)
    {
        NestedFolder[] all = [.. children, .. namedSince];
        Array.Sort(all, static (left, right) =>
        {
            var order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        return all;
    }

    // ---- applying --------------------------------------------------------------

    /// <summary>
    /// Applies one finished read, on the tree's own thread; false when there
    /// was nothing to apply - the read was given up on, cancelled, or its
    /// folder was cut off meanwhile.  Whoever waited for it is told after the
    /// drain's events; whoever asked for a read that began after this one
    /// did gets it queued now.
    /// </summary>
    private bool Apply(ReadResult read)
    {
        var folder = read.Folder;
        ExplicitRead? request;
        lock (_gate)
        {
            if (read.Ticket != folder.ReadTicket || folder.QueuedRead == ReadKind.None)
            {
                return false;
            }

            folder.QueuedRead = ReadKind.None;
            folder.IsSticky = false;
            _explicit.Remove(folder, out request);
        }

        try
        {
            return Apply(folder, read, request);
        }
        catch (Exception ex)
        {
            // Whatever went wrong - a listener that threw - the folder must
            // not stay "reading" for ever, and whoever waits hears why.
            folder.LoadState = NestedLoadState.Failed;
            folder.ErrorMessage = ex.Message;
            folder.FailedAt = Stopwatch.GetTimestamp();
            folder.IsRetryable = true;
            _batch.Applied.Add(folder);
            if (request is not null)
            {
                foreach (var waiter in request.Waiters)
                {
                    waiter.Registration.Unregister();
                    waiter.TrySetException(ex);
                }
            }

            return true;
        }
    }

    private bool Apply(NestedFolder folder, ReadResult read, ExplicitRead? request)
    {
        if (read.IsCancelled || _disposed)
        {
            // As if it had never been asked for: unread, or stale.
            if (folder.LoadState == NestedLoadState.Loading)
            {
                folder.LoadState = NestedLoadState.NotLoaded;
            }
            else
            {
                folder.IsStale = true;
            }

            if (request is not null)
            {
                foreach (var waiter in request.Waiters)
                {
                    waiter.Registration.Unregister();
                    waiter.TrySetCanceled();
                }
            }

            return false;
        }

        // Its parent was read again while this was in flight and it is gone:
        // nothing may be hung on a folder nobody can reach.
        if (IsDetached(folder))
        {
            // Nothing reads it now; if it is ever reached again, read afresh.
            folder.LoadState = NestedLoadState.NotLoaded;
            if (request is not null)
            {
                _completed.AddRange(request.Waiters);
            }

            return false;
        }

        folder.DirWriteTicks = read.DirectoryWriteTicks;
        var listing = read.Listing;
        if (!string.IsNullOrEmpty(listing.ErrorMessage))
        {
            folder.LoadState = NestedLoadState.Failed;
            folder.ErrorMessage = listing.ErrorMessage;
            folder.FailedAt = Stopwatch.GetTimestamp();
            folder.IsRetryable = listing.IsRetryable;
            _batch.Applied.Add(folder);
            Answer(folder, request);
            return true;
        }

        // The listing was matched against the children as they were when the
        // read started.  If something replaced them meanwhile, match again
        // against what is there now; it is rare, and correctness beats speed.
        NestedFolder[] namedSince = [];
        if (!ReferenceEquals(read.Basis, folder.AllChildren))
        {
            var (children, removed) = Prepare(folder, folder.AllChildren, listing);
            namedSince = NamedSince(read.Basis, children, removed);
            if (namedSince.Length > 0)
            {
                removed = [.. removed.Where(child => Array.IndexOf(namedSince, child) < 0)];
            }

            read = read with { Children = children, Removed = removed, Basis = folder.AllChildren };
        }

        BeforeApply(folder, read);
        foreach (var removed in read.Removed)
        {
            Forget(removed);
        }

        _knownCount += read.Children.Length + namedSince.Length - read.Basis.Length;

        // A folder still there keeps its object, with everything read below
        // it, but its date is whatever the new listing says.  Set here on the
        // UI thread, which owns the folders, rather than while preparing.
        // One whose own read failed - gone when it was read, as a build tool
        // deletes and makes again its output - is named by the listing again:
        // whatever made it fail may have passed, and it is read again when
        // it is next drawn or asked for, as after any change seen in it.
        var entries = listing.Folders;
        for (var index = 0; index < read.Children.Length; index++)
        {
            var child = read.Children[index];
            child.ModifiedTicks = entries[index].ModifiedTicks;
            if (child.LoadState == NestedLoadState.Failed)
            {
                child.IsStale = true;
            }
        }

        folder.AllChildren = namedSince.Length == 0 ? read.Children : WithNamedSince(read.Children, namedSince);
        folder.AllFiles = listing.Files as NestedFile[] ?? [.. listing.Files];
        folder.FileCount = listing.FileCount;
        folder.HiddenFileCount = listing.HiddenFileCount;
        folder.IsTruncated = listing.IsTruncated;
        folder.ErrorMessage = string.Empty;
        folder.LoadState = NestedLoadState.Loaded;
        folder.HasPartialListing = false;
        ApplyVisibleChildren(folder);
        AfterApply(folder, read);

        FolderLoaded?.Invoke(folder);
        _batch.Applied.Add(folder);
        Answer(folder, request);
        return true;
    }

    /// <summary>
    /// Tells the waiters a read of <paramref name="folder"/> answers - after
    /// the drain's events - and queues one more read for those it does not: a
    /// refresh asked for while this read was already under way.
    /// </summary>
    private void Answer(NestedFolder folder, ExplicitRead? request)
    {
        if (request is null)
        {
            return;
        }

        var again = request.Again;
        request.Again = false;
        for (var index = request.Waiters.Count - 1; index >= 0; index--)
        {
            var waiter = request.Waiters[index];
            if (waiter.NeedsFreshRead)
            {
                // The next read is picked after now: it answers this one.
                waiter.NeedsFreshRead = false;
                again = true;
                continue;
            }

            _completed.Add(waiter);
            request.Waiters.RemoveAt(index);
        }

        if (!again)
        {
            return;
        }

        lock (_gate)
        {
            if (_explicit.TryGetValue(folder, out var joined))
            {
                // Asked for again while this one was being applied.
                joined.Waiters.AddRange(request.Waiters);
            }
            else if (request.Waiters.Count > 0)
            {
                _explicit[folder] = request;
            }

            folder.IsStale = true;
            EnqueueLocked(folder, ReadKind.Refresh, sticky: true);
        }
    }

    /// <summary>
    /// Drops a folder that is no longer there.  Only the folder itself is
    /// marked: everything below it is unreachable through it anyway, and
    /// walking a subtree of thousands to say so again was the slow part of a
    /// refresh.  <see cref="IsDetached"/> is how the rest find out.  A read of
    /// the folder waiting in the queue is taken off it, and one under way is
    /// dropped when it finishes; whoever waited for either is let go.
    /// </summary>
    private void Forget(NestedFolder folder)
    {
        folder.Index = -1;
        folder.IsForgotten = true;
        ExplicitRead? request;
        lock (_gate)
        {
            if (folder.QueueIndex >= 0)
            {
                RemoveLocked(folder);
            }

            if (folder.QueuedRead != ReadKind.None)
            {
                folder.QueuedRead = ReadKind.None;
                folder.ReadTicket++;
            }

            if (folder.LoadState is NestedLoadState.Queued or NestedLoadState.Loading)
            {
                folder.LoadState = NestedLoadState.NotLoaded;
            }

            folder.IsSticky = false;
            _explicit.Remove(folder, out request);
        }

        if (request is not null)
        {
            foreach (var waiter in request.Waiters)
            {
                waiter.Registration.Unregister();
                waiter.TrySetResult();
            }
        }

        OnForgotten(folder);
    }

    // ---- waiting ---------------------------------------------------------------

    /// <summary>
    /// A waiter whose token was cancelled: it stops waiting, and if nobody else
    /// waits for the folder, its read goes too - taken off the queue if it has
    /// not started, and a first read under way is left to finish unapplied, so
    /// the folder is unread again at once.  A refresh under way is let finish:
    /// what it brings is newer than what is shown.  Any thread.
    /// </summary>
    private void Cancel(ReadWaiter waiter)
    {
        var folder = waiter.Folder;
        lock (_gate)
        {
            if (!_explicit.TryGetValue(folder, out var request) || !request.Waiters.Remove(waiter))
            {
                // Already answered.
                return;
            }

            if (request.Waiters.Count == 0 && !request.Again)
            {
                _explicit.Remove(folder);
                folder.IsSticky = false;
                if (folder.QueueIndex >= 0)
                {
                    RemoveLocked(folder);
                    if (folder.QueuedRead == ReadKind.Load)
                    {
                        folder.LoadState = NestedLoadState.NotLoaded;
                    }

                    folder.QueuedRead = ReadKind.None;
                }
                else if (folder.QueuedRead == ReadKind.Load)
                {
                    folder.QueuedRead = ReadKind.None;
                    folder.ReadTicket++;
                    folder.LoadState = NestedLoadState.NotLoaded;
                }
            }
        }

        waiter.TrySetCanceled(waiter.Token);
    }

    /// <summary>
    /// The tree is going away: nothing waiting is read, and whoever awaits a
    /// read hears it was cancelled.  Reads under way finish on their own and
    /// are dropped.
    /// </summary>
    private void OnLifetimeEnded()
    {
        List<ReadWaiter> waiters = [];
        lock (_gate)
        {
            foreach (var request in _explicit.Values)
            {
                waiters.AddRange(request.Waiters);
            }

            _explicit.Clear();
            foreach (var folder in _pending)
            {
                folder.QueueIndex = -1;
                if (folder.QueuedRead == ReadKind.Load)
                {
                    folder.LoadState = NestedLoadState.NotLoaded;
                }

                folder.QueuedRead = ReadKind.None;
                folder.IsSticky = false;
            }

            _pending.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled();
        }
    }

    /// <summary>A read a slot has begun: the folder, which read, its ticket, and the share whose lane it holds (null: local).</summary>
    private readonly record struct ReadPick(NestedFolder Folder, ReadKind Kind, int Ticket, string? Share);

    /// <summary>Who is waiting on one folder's read, and whether a read already under way will not do.</summary>
    private sealed class ExplicitRead
    {
        public List<ReadWaiter> Waiters { get; } = [];

        /// <summary>A refresh nobody awaits was asked for while a read was under way: read once more after it.</summary>
        public bool Again { get; set; }
    }

    /// <summary>
    /// Someone awaiting a load or a refresh.  Made only by
    /// <see cref="LoadAsync"/> and <see cref="RefreshAsync"/>: the reads the
    /// canvas asks for have nobody waiting and cost no task.  Continuations run
    /// on their own, never inside the drain that answers them.
    /// </summary>
    private sealed class ReadWaiter(NestedTree tree, NestedFolder folder, bool needsFreshRead, CancellationToken token)
        : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public NestedFolder Folder { get; } = folder;

        /// <summary>Asked for while a read was already under way, which began too early to answer it.</summary>
        public bool NeedsFreshRead { get; set; } = needsFreshRead;

        public CancellationToken Token { get; } = token;

        public CancellationTokenRegistration Registration { get; private set; }

        /// <summary>Starts listening to the token, once the waiter is where a cancellation can find it.</summary>
        public void Listen()
        {
            if (Token.CanBeCanceled)
            {
                Registration = Token.UnsafeRegister(static waiter => ((ReadWaiter)waiter!).OnCancelled(), this);
            }
        }

        private void OnCancelled() => tree.Cancel(this);
    }
}
