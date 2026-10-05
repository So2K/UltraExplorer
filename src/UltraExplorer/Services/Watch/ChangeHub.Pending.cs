using System.Runtime.InteropServices;
using UltraExplorer.Controls;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// The hub's pending changes: merging what the watchers hear per folder,
/// deciding when each folder falls due, the one timer that moves due folders
/// to the inbox, and the drain that hands them on.
/// </summary>
public sealed partial class ChangeHub
{
    /// <summary>What a timestamp is before it is set; far enough from both ends that adding a delay to it cannot overflow.</summary>
    private const long Unset = long.MaxValue / 4;

    private static readonly IComparer<PendingChange> ShallowestFirst = Comparer<PendingChange>.Create(
        static (left, right) => left.Depth != right.Depth ? left.Depth.CompareTo(right.Depth) : left.Due.CompareTo(right.Due));

    /// <summary>Guards the pending changes, the refresh timings and the timer's due time.  Held for microseconds, never while calling out.</summary>
    private readonly Lock _gate = new();

    /// <summary>One timer pass at a time.</summary>
    private readonly Lock _timerGate = new();

    /// <summary>Every folder with changes not yet handed on, by its registered path: waiting for its due time, or due and in the inbox.</summary>
    private readonly Dictionary<string, PendingChange> _changes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each recently refreshed folder may be refreshed again, and where its recheck of a share stands.</summary>
    private readonly Dictionary<string, FolderTiming> _timings = new(StringComparer.OrdinalIgnoreCase);

    private readonly Stack<PendingChange> _spare = new();
    private readonly List<PendingChange> _ripe = [];
    private long _timerDue = long.MaxValue;
    private int _timingsPruneAt = 256;
    private long _localQuiet;
    private long _localLatest;
    private long _networkQuiet;
    private long _networkLatest;
    private long _contentQuiet;
    private long _contentLatest;
    private long _pollTicks;
    private long _keepTicks;
    private long _overflowGapTicks;
    private long _lastRecheckTicks;
    private long _overflows;

    private enum HubItemKind : byte
    {
        Change,
        Bump,
        Poll
    }

    /// <summary>What waits in the inbox: a folder's changes, or a root's epoch bump or poll.</summary>
    private readonly record struct HubItem(HubItemKind Kind, PendingChange? Change, WatchRoot? Root);

    /// <summary>
    /// The changes heard in one folder since it was last handed on.  Reused:
    /// only the arrays of renames and file changes go with the handed-on
    /// change, and a fresh pair is made for the next.
    /// </summary>
    private sealed class PendingChange
    {
        public string Key = string.Empty;
        public int Depth;
        public bool IsNetwork;
        public ChangeKinds Kinds;

        /// <summary>Moved to the inbox: later events join it without delaying it.</summary>
        public bool IsDue;

        public long First = Unset;
        public long FirstStructural = Unset;
        public long LastStructural = Unset;
        public long FirstContent = Unset;
        public long LastContent = Unset;

        /// <summary>When <see cref="Touch"/> asked for it at once.</summary>
        public long ImmediateAt = Unset;

        /// <summary>When a share's folder is to be read again after a refresh that found nothing new.</summary>
        public long RecheckAt = Unset;

        /// <summary>Made for a recheck alone, with no change heard.</summary>
        public bool IsRecheck;

        public long Due;
        public RenamePair[]? Renames;
        public int RenameCount;
        public FileDelta[]? Files;
        public int FileCount;
        public bool FilesIncomplete;
        public FileDelta[]? Folders;
        public int FolderCount;
        public bool FoldersIncomplete;

        public void Reset()
        {
            Key = string.Empty;
            Kinds = 0;
            IsDue = false;
            First = FirstStructural = LastStructural = FirstContent = LastContent = ImmediateAt = RecheckAt = Unset;
            IsRecheck = false;
            Renames = null;
            RenameCount = 0;
            Files = null;
            FileCount = 0;
            FilesIncomplete = false;
            Folders = null;
            FolderCount = 0;
            FoldersIncomplete = false;
        }
    }

    /// <summary>How soon a folder may be refreshed again, and the rechecks of a share's folder.</summary>
    private sealed class FolderTiming
    {
        public long NextAllowed;

        /// <summary>A change was handed on for a share's folder and its refresh has not been reported yet.</summary>
        public bool AwaitingReport;

        /// <summary>0: no recheck under way; 1: the two-second one is due; 2: the last one is.</summary>
        public byte Recheck;

        public long RecheckFrom;

        /// <summary>
        /// When it last began to wait for a report or a recheck.  A folder
        /// handed on and never read again - off screen, and never drawn since -
        /// never reports; once the last recheck would long have come, the
        /// timing is let go (<see cref="PruneTimingsLocked"/>).
        /// </summary>
        public long WaitingSince;
    }

    /// <summary>Folders with changes not yet handed on - waiting or due.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _changes.Count;
            }
        }
    }

    /// <summary>Overflows heard across every root, for tests and the bench.</summary>
    internal long Overflows => Interlocked.Read(ref _overflows);

    /// <summary>
    /// Asks for <paramref name="fullPath"/>'s registrations to be told it
    /// changed: F5, or a change the app made itself.  At once when
    /// <paramref name="immediate"/> - by the next frame, ahead of the quiet
    /// spell and of the least gap between refreshes - otherwise like a change
    /// the watch heard.  Merged with anything already pending for the folder.
    /// Nothing happens when no one is registered for it.
    /// </summary>
    public void Touch(string fullPath, bool immediate)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (_disposed || !Registry.TryFind(WatchAlias.KeyOf(fullPath), out var key))
        {
            return;
        }

        var network = FindRoot(key, create: false, depth: 0)?.IsNetwork ?? false;
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            var change = PendingFor(key, network);
            Merge(change, ChangeKinds.Structural, now);
            if (immediate)
            {
                change.ImmediateAt = Math.Min(change.ImmediateAt, now);
            }

            Settle(change);
        }
    }

    /// <summary>
    /// Tells the hub a folder was just read again: how long the read took on
    /// its worker and the apply on the UI thread, and whether the listing
    /// differed from the last.  Sets how soon a change heard from now on may
    /// fall due - <c>max(250 ms, 10 × read + 20 × apply)</c>, so a folder that
    /// is slow to read or apply is refreshed less often; a change already
    /// pending keeps its time - and, on a share,
    /// asks for the folder to be read again after 2 and 10.5 seconds when a
    /// refresh the hub asked for found nothing new: the share's client may
    /// have answered from its cache.
    /// </summary>
    public void ReportRefresh(string fullPath, double readMs, double applyMs, bool listingChanged)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var key = WatchAlias.KeyOf(fullPath);
        var network = FindRoot(key, create: false, depth: 0)?.IsNetwork ?? false;
        var now = _time.GetTimestamp();
        var gap = Math.Max(MinimumRefreshGapMilliseconds, 10 * readMs + 20 * applyMs);
        lock (_gate)
        {
            ref var timing = ref CollectionsMarshal.GetValueRefOrAddDefault(_timings, key, out _);
            timing ??= new FolderTiming();
            timing.NextAllowed = now + Ticks(gap);
            if (network && timing.AwaitingReport)
            {
                timing.AwaitingReport = false;
                if (listingChanged)
                {
                    timing.Recheck = 0;
                }
                else if (timing.Recheck == 0)
                {
                    timing.Recheck = 1;
                    timing.RecheckFrom = now;
                    timing.WaitingSince = now;
                    RecheckLocked(key, now, now + Ticks(NetworkRecheckMilliseconds));
                }
                else if (timing.Recheck == 1)
                {
                    timing.Recheck = 2;
                    timing.WaitingSince = now;
                    RecheckLocked(key, now, timing.RecheckFrom + _lastRecheckTicks);
                }
                else
                {
                    timing.Recheck = 0;
                }
            }

            // A change already pending keeps the due time it has: it was heard
            // before this refresh was reported, and the refresh may not have
            // been one the hub asked for - the tree reads a folder again when
            // a sub-folder of it on screen goes, or when it is drawn out of
            // date - so it may never have reached the folder list or the tree
            // canvas.  Held back to the gap, a folder renamed inside the list's
            // folder reached the list a quarter of a second after the canvas.
            // Only what is heard from now on waits for the gap (Note, Touch).
            PruneTimingsLocked(now);
        }
    }

    /// <summary>
    /// Hands on what has fallen due, shallowest folders first, until
    /// <paramref name="budget"/> is spent; returns how many items it took.
    /// A folder's change goes to every target registered for it whose folder
    /// is still in the tree; epoch bumps and polls go to the sink as they are.
    /// What the budget leaves stays for the next drain (<see cref="HasWork"/>).
    /// </summary>
    internal int Drain(ref FrameBudget budget, IChangeSink sink)
    {
        _inbox.Rearm();
        var taken = 0;
        while (!budget.Spent && _inbox.TryTake(out var item))
        {
            budget.Take();
            taken++;
            switch (item.Kind)
            {
                case HubItemKind.Bump:
                    Volatile.Write(ref item.Root!.BumpQueued, 0);
                    sink.EpochBumped(item.Root);
                    break;
                case HubItemKind.Poll:
                    Volatile.Write(ref item.Root!.PollQueued, 0);
                    sink.PollDue(item.Root);
                    break;
                default:
                    Deliver(item.Change!, sink);
                    break;
            }
        }

        return taken;
    }

    /// <summary>
    /// Merges one record's change into its folder's pending change: on a
    /// watcher's thread, for a registered folder.  Allocates only for what a
    /// change carries - a rename's names, a changed file's or sub-folder's
    /// name - and only up to <see cref="MaximumDetails"/> of each.
    /// </summary>
    internal void Note(string key, ChangeKinds kinds, WatchRoot root, in ChangeRecord record, ReadOnlySpan<char> leaf, ReadOnlySpan<char> renamedFrom)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            var change = PendingFor(key, root.IsNetwork);
            Merge(change, kinds, now);
            if (kinds == ChangeKinds.Content)
            {
                const uint HiddenOrSystem = WatchNative.FileAttributeHidden | WatchNative.FileAttributeSystem;
                if (record.HasDetails && (record.Attributes & HiddenOrSystem) == 0 && !change.FilesIncomplete)
                {
                    AddFile(change, leaf, in record);
                }
                else
                {
                    change.FilesIncomplete = true;
                }
            }
            else if (kinds == ChangeKinds.DirDate && !change.FoldersIncomplete)
            {
                AddFolder(change, leaf, in record);
            }

            if (!renamedFrom.IsEmpty && change.RenameCount < MaximumDetails)
            {
                change.Renames ??= new RenamePair[MaximumDetails];
                change.Renames[change.RenameCount++] = new RenamePair(renamedFrom.ToString(), leaf.ToString());
            }

            Settle(change);
        }
    }

    /// <summary>
    /// Changes under <paramref name="root"/> outgrew its buffer and were lost:
    /// the epoch moves on, once - at once, or, within the root's gap of the
    /// last overflow that moved it, when that gap is up.  Put off, never
    /// dropped: a change lost after the last move may have been missed by a
    /// read begun since, and only another move has that folder read again.
    ///
    /// The gap starts at <see cref="OverflowGapMilliseconds"/> and doubles
    /// every time overflows keep coming inside it, up to
    /// <see cref="PollMilliseconds"/>: a share that answers every read with
    /// an overflow would otherwise have every folder of it on screen read
    /// again over the network twice a second for as long as it is shown.
    /// An overflow that comes after the gap is up - the storm is over - moves
    /// the epoch at once and puts the gap back to its least.
    /// </summary>
    internal void OnOverflow(WatchRoot root, DirectoryChangeWatcher? watcher)
    {
        if (!ReferenceEquals(root.Watcher, watcher))
        {
            return;
        }

        Interlocked.Increment(ref _overflows);
        var now = _time.GetTimestamp();
        var last = Volatile.Read(ref root.LastOverflowBump);
        var gap = OverflowGapOf(root);
        if (last != 0 && now - last < gap)
        {
            Volatile.Write(ref root.OverflowBumpOwed, 1);
            lock (_gate)
            {
                ScheduleLocked(last + gap);
            }

            return;
        }

        Volatile.Write(ref root.OverflowGap, 0);
        BumpForOverflow(root, now);
    }

    /// <summary>The least time, in hub ticks, between two epochs an overflow of <paramref name="root"/> moves on now.</summary>
    private long OverflowGapOf(WatchRoot root) => Volatile.Read(ref root.OverflowGap) is > 0 and var gap ? gap : _overflowGapTicks;

    /// <summary>For tests: the root's overflow gap now, in milliseconds.</summary>
    internal double OverflowGapMillisecondsOf(WatchRoot root) => OverflowGapOf(root) * 1000.0 / _time.TimestampFrequency;

    /// <summary>Moves the epoch on for an overflow - and for any put off before it - and hands the bump on when anything registered could be out of date.</summary>
    private void BumpForOverflow(WatchRoot root, long now)
    {
        Volatile.Write(ref root.OverflowBumpOwed, 0);
        Volatile.Write(ref root.LastOverflowBump, now);
        Interlocked.Increment(ref root.Epoch);
        if (HasInterest(root))
        {
            PostBump(root);
        }
    }

    private void InitialiseTiming()
    {
        _localQuiet = Ticks(LocalQuietMilliseconds);
        _localLatest = Ticks(LocalLatestMilliseconds);
        _networkQuiet = Ticks(NetworkQuietMilliseconds);
        _networkLatest = Ticks(NetworkLatestMilliseconds);
        _contentQuiet = Ticks(ContentQuietMilliseconds);
        _contentLatest = Ticks(ContentLatestMilliseconds);
        _pollTicks = Ticks(PollMilliseconds);
        _keepTicks = Ticks(NetworkKeepMilliseconds);
        _overflowGapTicks = Ticks(OverflowGapMilliseconds);
        _lastRecheckTicks = Ticks(NetworkLastRecheckMilliseconds);
    }

    /// <summary><paramref name="milliseconds"/> in the time provider's timestamp ticks.</summary>
    private long Ticks(double milliseconds) => (long)(milliseconds * _time.TimestampFrequency / 1000.0);

    /// <summary>The folder's pending change, made when it has none.  Under the gate.</summary>
    private PendingChange PendingFor(string key, bool network)
    {
        ref var change = ref CollectionsMarshal.GetValueRefOrAddDefault(_changes, key, out var exists);
        if (!exists || change is null)
        {
            change = _spare.Count > 0 ? _spare.Pop() : new PendingChange();
            change.Key = key;
            change.IsNetwork = network;
            change.Depth = DepthOf(key);
        }

        return change;
    }

    private static int DepthOf(string key)
    {
        var separators = key.AsSpan().Count('\\');
        return key.EndsWith('\\') ? separators - 1 : separators;
    }

    private static void Merge(PendingChange change, ChangeKinds kinds, long now)
    {
        change.Kinds |= kinds;
        if (change.First == Unset)
        {
            change.First = now;
        }

        if ((kinds & (ChangeKinds.Structural | ChangeKinds.Gone)) != 0)
        {
            if (change.FirstStructural == Unset)
            {
                change.FirstStructural = now;
            }

            change.LastStructural = now;
        }

        if ((kinds & (ChangeKinds.Content | ChangeKinds.DirDate)) != 0)
        {
            if (change.FirstContent == Unset)
            {
                change.FirstContent = now;
            }

            change.LastContent = now;
        }
    }

    /// <summary>Lists a changed file with its new size and date, once however often it changed; past the limit the list is given up.</summary>
    private static void AddFile(PendingChange change, ReadOnlySpan<char> leaf, in ChangeRecord record)
    {
        var files = change.Files ??= new FileDelta[MaximumDetails];
        var delta = new FileDelta(string.Empty, record.Size, record.LastWriteTicks) { Attributes = (FileAttributes)record.Attributes };
        for (var index = 0; index < change.FileCount; index++)
        {
            if (leaf.Equals(files[index].Name, StringComparison.OrdinalIgnoreCase))
            {
                files[index] = delta with { Name = files[index].Name };
                return;
            }
        }

        if (change.FileCount == MaximumDetails)
        {
            change.FilesIncomplete = true;
            return;
        }

        files[change.FileCount++] = delta with { Name = leaf.ToString() };
    }

    /// <summary>
    /// Lists a sub-folder whose own entry changed, with its attributes now,
    /// once however often it changed - what tells one hidden or shown from
    /// one only written in; past the limit the list is given up.
    /// </summary>
    private static void AddFolder(PendingChange change, ReadOnlySpan<char> leaf, in ChangeRecord record)
    {
        var folders = change.Folders ??= new FileDelta[MaximumDetails];
        var delta = new FileDelta(string.Empty, 0, record.LastWriteTicks) { Attributes = (FileAttributes)record.Attributes };
        for (var index = 0; index < change.FolderCount; index++)
        {
            if (leaf.Equals(folders[index].Name, StringComparison.OrdinalIgnoreCase))
            {
                folders[index] = delta with { Name = folders[index].Name };
                return;
            }
        }

        if (change.FolderCount == MaximumDetails)
        {
            change.FoldersIncomplete = true;
            return;
        }

        folders[change.FolderCount++] = delta with { Name = leaf.ToString() };
    }

    /// <summary>Sets when the change falls due and has the timer come for it.  Under the gate.</summary>
    private void Settle(PendingChange change)
    {
        if (change.IsDue)
        {
            return;
        }

        change.Due = DueOf(change);
        ScheduleLocked(change.Due);
    }

    /// <summary>
    /// When a pending change falls due: at once when touched so; otherwise the
    /// earlier of its lanes' quiet spell after the last event and longest wait
    /// after the first, or its recheck - but never before the folder may be
    /// refreshed again, as it stood when the change was last heard or asked
    /// for.  Under the gate.
    /// </summary>
    private long DueOf(PendingChange change)
    {
        if (change.ImmediateAt != Unset)
        {
            return change.ImmediateAt;
        }

        var due = Unset;
        if ((change.Kinds & (ChangeKinds.Structural | ChangeKinds.Gone)) != 0 && change.FirstStructural != Unset)
        {
            var (quiet, latest) = change.IsNetwork ? (_networkQuiet, _networkLatest) : (_localQuiet, _localLatest);
            due = Math.Min(change.LastStructural + quiet, change.FirstStructural + latest);
        }

        if ((change.Kinds & (ChangeKinds.Content | ChangeKinds.DirDate)) != 0 && change.FirstContent != Unset)
        {
            due = Math.Min(due, Math.Min(change.LastContent + _contentQuiet, change.FirstContent + _contentLatest));
        }

        due = Math.Min(due, change.RecheckAt);
        if (_timings.TryGetValue(change.Key, out var timing))
        {
            due = Math.Max(due, timing.NextAllowed);
        }

        return due;
    }

    /// <summary>Has the timer fire by <paramref name="due"/>.  Under the gate.</summary>
    private void ScheduleLocked(long due)
    {
        if (_disposed || due >= _timerDue || due >= Unset)
        {
            return;
        }

        _timerDue = due;
        var delay = due - _time.GetTimestamp();
        _timer.Change(delay <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)(delay * (double)TimeSpan.TicksPerSecond / _time.TimestampFrequency)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Has the timer come for whatever <paramref name="root"/> waits on: a retry, a poll, letting a share go.</summary>
    private void ScheduleRoot(WatchRoot root)
    {
        var due = NextRootDue(root);
        if (due < Unset)
        {
            lock (_gate)
            {
                ScheduleLocked(due);
            }
        }
    }

    /// <summary>
    /// The timer's pass: moves every change that has fallen due to the inbox,
    /// shallowest first, then sees to the roots - retries, polls, shares no
    /// longer drawn - and sets itself for the next thing due.
    /// </summary>
    private void OnTimer()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetTimestamp();
            var next = Unset;
            lock (_gate)
            {
                _timerDue = long.MaxValue;
                foreach (var change in _changes.Values)
                {
                    if (change.IsDue)
                    {
                        continue;
                    }

                    if (change.Due <= now)
                    {
                        _ripe.Add(change);
                    }
                    else
                    {
                        next = Math.Min(next, change.Due);
                    }
                }

                if (_ripe.Count > 1)
                {
                    _ripe.Sort(ShallowestFirst);
                }

                foreach (var change in _ripe)
                {
                    change.IsDue = true;
                }
            }

            foreach (var change in _ripe)
            {
                _inbox.Post(new HubItem(HubItemKind.Change, change, null));
            }

            _ripe.Clear();
            next = Math.Min(next, TendRoots(now));
            lock (_gate)
            {
                ScheduleLocked(next);
            }
        }
    }

    /// <summary>Hands one due folder's change to its registrations, then puts the change back for reuse.</summary>
    private void Deliver(PendingChange change, IChangeSink sink)
    {
        FolderChange handed;

        // Looked up before the timing is made: a folder no one is registered
        // for any more is told nothing, and would never report a refresh.
        Registry.TryGet(change.Key, out var interest);
        lock (_gate)
        {
            if (_changes.TryGetValue(change.Key, out var current) && ReferenceEquals(current, change))
            {
                _changes.Remove(change.Key);
            }

            handed = new FolderChange(
                change.Key,
                change.Kinds,
                change.First,
                change.Renames is { } renames ? renames.AsMemory(0, change.RenameCount) : default,
                change.Files is { } files && !change.FilesIncomplete ? files.AsMemory(0, change.FileCount) : default)
            {
                Folders = change.Folders is { } folders && !change.FoldersIncomplete ? folders.AsMemory(0, change.FolderCount) : default
            };
            if (interest is not null && change.IsNetwork && (change.Kinds & ChangeKinds.Gone) == 0)
            {
                ref var timing = ref CollectionsMarshal.GetValueRefOrAddDefault(_timings, change.Key, out _);
                timing ??= new FolderTiming();
                timing.AwaitingReport = true;
                timing.WaitingSince = _time.GetTimestamp();
                if (!change.IsRecheck)
                {
                    timing.Recheck = 0;
                }
            }
        }

        try
        {
            if (interest is not null)
            {
                var gone = Tell(ChangeConsumer.Nested, interest.Nested, in handed, sink)
                    | Tell(ChangeConsumer.List, interest.List, in handed, sink)
                    | Tell(ChangeConsumer.Graph, interest.Graph, in handed, sink);
                if (gone)
                {
                    var removed = Registry.RemoveGone(change.Key, Gone, out var root);
                    if (removed > 0 && root is not null)
                    {
                        Release(ref root.NestedInterest, removed);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                change.Reset();
                if (_spare.Count < 256)
                {
                    _spare.Push(change);
                }
            }
        }
    }

    /// <summary>Tells each target in a slot, skipping any that has gone; true when one had.</summary>
    private static bool Tell(ChangeConsumer consumer, object? slot, in FolderChange change, IChangeSink sink)
    {
        var gone = false;
        foreach (var target in ChangeRegistry.Items(slot))
        {
            if (IsGone(target))
            {
                gone = true;
                continue;
            }

            sink.FolderChanged(consumer, target, in change);
        }

        return gone;
    }

    /// <summary>
    /// Asks for a share's folder to be read again at <paramref name="at"/>,
    /// as a change of its own.  A change already pending - heard while the
    /// folder was being read - keeps the time it has, as <see cref="ReportRefresh"/>
    /// leaves it, and the recheck rides on it: settled again, it would be
    /// held to the gap the report just set, and a rename on a share would
    /// reach the folder list a quarter of a second after the canvas.  Under the gate.
    /// </summary>
    private void RecheckLocked(string key, long now, long at)
    {
        if (!Registry.TryFind(key, out var registered))
        {
            return;
        }

        var change = PendingFor(registered, network: true);
        var heard = change.Kinds != 0;
        if (!heard)
        {
            change.IsRecheck = true;
            change.First = now;
        }

        change.Kinds |= ChangeKinds.Structural;
        change.RecheckAt = Math.Min(change.RecheckAt, at);
        if (heard)
        {
            if (!change.IsDue && at < change.Due)
            {
                change.Due = at;
                ScheduleLocked(at);
            }

            return;
        }

        Settle(change);
    }

    /// <summary>
    /// Forgets the timings of folders that may be refreshed again already and
    /// wait for nothing, once they have piled up - or wait for a report or a
    /// recheck that would long have come if it were coming: a share's folder
    /// handed on while off screen is read only when it is next drawn, maybe
    /// never, and by then the share's cache has caught up anyway.  Under the gate.
    /// </summary>
    private void PruneTimingsLocked(long now)
    {
        if (_timings.Count < _timingsPruneAt)
        {
            return;
        }

        foreach (var (key, timing) in _timings)
        {
            var waiting = timing.Recheck != 0 || timing.AwaitingReport;
            if (timing.NextAllowed <= now && (!waiting || now - timing.WaitingSince >= _lastRecheckTicks))
            {
                _timings.Remove(key);
            }
        }

        _timingsPruneAt = Math.Max(256, _timings.Count * 2);
    }

    private void PostBump(WatchRoot root)
    {
        if (Interlocked.Exchange(ref root.BumpQueued, 1) == 0)
        {
            _inbox.Post(new HubItem(HubItemKind.Bump, null, root));
        }
    }

    private void PostPoll(WatchRoot root)
    {
        if (Interlocked.Exchange(ref root.PollQueued, 1) == 0)
        {
            _inbox.Post(new HubItem(HubItemKind.Poll, null, root));
        }
    }
}
