using UltraExplorer.Controls;
using UltraExplorer.Models;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// Every change on disk the app shows, from one place.  One recursive watch
/// per volume or share (<see cref="WatchRoot"/>) hears everything under it;
/// the folders someone shows - the nested canvas's loaded folders, the folder
/// list's folder, the tree canvas's expanded nodes - are registered by path;
/// and a change in a registered folder is merged with the others in that
/// folder and handed on once it has settled, a frame at a time, on the UI
/// thread (<see cref="Drain"/>).  What nobody shows costs one lookup on the
/// watcher's thread and nothing else.
///
/// <para><b>Roots.</b>  A local volume - fixed, removable, RAM disk, VHD - is
/// armed as soon as anything on it is asked for (<see cref="RootFor"/>,
/// <see cref="Register"/>), off the calling thread; it takes under a
/// millisecond.  A share is armed on its own thread, since opening a share
/// that has gone away can block for twenty seconds, and only while it is
/// drawn (<see cref="NoteDrawn"/>) or the list or graph shows a folder of it;
/// thirty seconds after the last of that it is let go.  A mapped letter and
/// the share's UNC name are one root; so are a <c>subst</c> letter and the
/// volume it points into.  A CD is not watched.  A volume that refuses a
/// watch is polled: every five seconds, and on <see cref="PollNow"/>, the sink
/// is asked to look at what it draws there (<see cref="IChangeSink.PollDue"/>).
/// A watch that fails - the share dropped, the device not ready - is tried
/// again after 1, 2, 4 … 60 seconds, and polled meanwhile; a share stops
/// being tried once nothing shows it, until it is drawn again.</para>
///
/// <para><b>Timing.</b>  A folder's changes fall due at the earlier of a quiet
/// spell after the last one and a longest wait after the first - 150 and
/// 500 ms for names on a local volume, 300 and 1,000 on a share, 1 and 3 s for
/// sizes and dates only - so a single change shows in about 150 ms and a
/// steady stream of them still refreshes every half second.  A change heard
/// in a folder after it was refreshed is not due before
/// <c>max(250 ms, 10 × read + 20 × apply)</c> has passed
/// (<see cref="ReportRefresh"/>), which spaces the refreshes of a big folder
/// being extracted into; one already pending keeps its time.  One timer, set
/// for the earliest due time, moves due folders to an inbox and wakes the
/// <see cref="Driver"/> once.</para>
///
/// <para><b>Missed changes.</b>  An overflow - more changed than the buffer
/// held - moves the root's <see cref="WatchRoot.Epoch"/> on, once; so does
/// every arm and re-arm.  Every folder read under an older epoch is then
/// read again when next drawn, and nothing walks the folders to make it so.</para>
///
/// <para><b>Threads.</b>  Every public member is safe on any thread;
/// <see cref="Drain"/> is meant for the UI thread and calls the sink there.
/// The watchers' threads, the timer's and the arming threads never call the
/// sink.</para>
/// </summary>
public sealed partial class ChangeHub : IDisposable
{
    /// <summary>At most this many renames and file changes are listed with a change (<see cref="FolderChange"/>).</summary>
    public const int MaximumDetails = 8;

    /// <summary>The watch buffer of a local volume, and of a share (which SMB caps at 64 KB).</summary>
    internal const int LocalBufferBytes = 256 * 1024;

    internal const int NetworkBufferBytes = 64 * 1024;

    /// <summary>Names changed on a local volume: due after this quiet spell…</summary>
    internal const double LocalQuietMilliseconds = 150;

    /// <summary>…or this long after the first change, whichever is sooner.</summary>
    internal const double LocalLatestMilliseconds = 500;

    internal const double NetworkQuietMilliseconds = 300;
    internal const double NetworkLatestMilliseconds = 1_000;

    /// <summary>Only sizes and dates changed: nothing moves on the canvas, so these wait longer.</summary>
    internal const double ContentQuietMilliseconds = 1_000;

    internal const double ContentLatestMilliseconds = 3_000;

    /// <summary>The least time between two refreshes of one folder, however quick its reads.</summary>
    internal const double MinimumRefreshGapMilliseconds = 250;

    /// <summary>
    /// A share's client serves a folder's listing from a cache for up to ten
    /// seconds; a refresh that found nothing new is read again after these.
    /// </summary>
    internal const double NetworkRecheckMilliseconds = 2_000;

    internal const double NetworkLastRecheckMilliseconds = 10_500;

    /// <summary>How often a polled root's drawn folders are looked at.</summary>
    internal const double PollMilliseconds = 5_000;

    /// <summary>How long a share's watch is kept after it was last drawn, when the list and graph do not show it.</summary>
    internal const double NetworkKeepMilliseconds = 30_000;

    internal const double FirstRetryMilliseconds = 1_000;
    internal const double LongestRetryMilliseconds = 60_000;

    /// <summary>Stale nested registrations are swept once there are at least this many, and twice as many as after the last sweep.</summary>
    internal const int SweepFloor = 4_096;

    /// <summary>Overrides every watch's buffer size, in bytes: for forcing overflows.</summary>
    internal const string BufferVariable = "ULTRAEXPLORER_WATCH_BUFFER";

    private static readonly Func<object, bool> Gone = IsGone;

    private readonly TimeProvider _time;
    private readonly VolumeResolver _volumes;
    private readonly FrameInbox<HubItem> _inbox;
    private readonly ITimer _timer;
    private readonly Lock _rootsGate = new();
    private readonly Dictionary<string, WatchRoot> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatchRoot>.AlternateLookup<ReadOnlySpan<char>> _rootLookup;
    private volatile WatchRoot?[] _letters = new WatchRoot?[26];
    private volatile WatchRoot[] _rootList = [];
    private volatile WatchRoot[] _testRoots = [];
    private volatile bool _disposed;
    private int _sweeping;
    private int _nestedAfterSweep;

    public ChangeHub(TimeProvider time)
        : this(time, new VolumeResolver())
    {
    }

    internal ChangeHub(TimeProvider time, VolumeResolver volumes)
    {
        _time = time;
        _volumes = volumes;
        _rootLookup = _roots.GetAlternateLookup<ReadOnlySpan<char>>();
        Driver = new FrameDriverSlot(NoDriver.Instance);
        _inbox = new FrameInbox<HubItem>(Driver);
        InitialiseTiming();
        _timer = time.CreateTimer(static state => ((ChangeHub)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        BufferOverride = int.TryParse(Environment.GetEnvironmentVariable(BufferVariable), out var bytes) && bytes > 0
            ? Math.Clamp(bytes, 1024, 16 << 20)
            : 0;
    }

    /// <summary>
    /// What the hub wakes when changes fall due: the canvas while it draws
    /// (<c>Active</c>), otherwise <c>Fallback</c> - which does nothing until
    /// the owner sets it, typically to a <see cref="DispatcherFrameDriver"/>
    /// draining into the same sink, so changes are handed on while no canvas
    /// is showing.  Until someone drains them, due changes wait, merged per folder.
    /// </summary>
    internal FrameDriverSlot Driver { get; }

    /// <summary>Whether changes, epoch bumps or polls are waiting to be drained.</summary>
    public bool HasWork => !_inbox.IsEmpty;

    /// <summary>
    /// A root was given a new watch handle: armed, or armed again after a
    /// suspension or a failure.  A handler that registered for device
    /// notifications on the previous handle should register on this one
    /// (<see cref="WatchRoot.Handle"/>) and let the old registration go.
    /// Raised on whatever thread armed it.
    /// </summary>
    public event Action<WatchRoot>? RootArmed;

    /// <summary>
    /// A root's watch handle was closed for good or until something asks for
    /// it again - let go, failed, dropped, or the hub disposed; not when
    /// <see cref="Suspend"/> closes it, since the device notifications on it
    /// must still arrive.  Raised on whatever thread closed it.
    /// </summary>
    public event Action<WatchRoot>? RootClosed;

    /// <summary>The watch buffer, in bytes, for every root that does not set its own; zero for the defaults.  From <see cref="BufferVariable"/>.</summary>
    internal int BufferOverride { get; set; }

    /// <summary>Who is registered for what.</summary>
    internal ChangeRegistry Registry { get; } = new();

    /// <summary>
    /// The root <paramref name="fullPath"/> is under - its volume's or its
    /// share's - made on first asking, and armed then if it is a local
    /// volume.  Null for a path that is not on a volume (This PC, a Shell
    /// namespace).  What the nested tree gives each of its roots
    /// (<c>NestedTree.WatchRootFor</c>); every folder under them shares it.
    /// </summary>
    public WatchRoot? RootFor(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return _disposed ? null : FindRoot(fullPath, create: true, depth: 0);
    }

    /// <summary>
    /// Asks for changes in <paramref name="fullPath"/> to be handed to
    /// <paramref name="target"/> through <paramref name="consumer"/>.  The path
    /// is compared ignoring case, and a separator at its end is ignored.
    /// Registering the same target twice is one registration.  A registration
    /// from the list or the graph keeps a share's watch armed while it lasts.
    /// </summary>
    public void Register(ChangeConsumer consumer, string fullPath, object target)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(target);
        var key = WatchAlias.KeyOf(fullPath);
        if (_disposed || key.Length == 0)
        {
            return;
        }

        var root = FindRoot(key, create: true, depth: 0);
        if (!Registry.Add(consumer, key, target, root))
        {
            return;
        }

        if (consumer == ChangeConsumer.Nested)
        {
            if (root is not null)
            {
                Interlocked.Increment(ref root.NestedInterest);
            }

            SweepWhenStale();
            return;
        }

        if (root is not null)
        {
            Interlocked.Increment(ref root.OtherInterest);
            ArmWhenIdle(root);
        }
    }

    /// <summary>Takes back one <see cref="Register"/>; nothing when there was none.</summary>
    public void Unregister(ChangeConsumer consumer, string fullPath, object target)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(target);
        var key = WatchAlias.KeyOf(fullPath);
        if (key.Length == 0 || !Registry.Remove(consumer, key, target, out var root) || root is null)
        {
            return;
        }

        if (consumer == ChangeConsumer.Nested)
        {
            Release(ref root.NestedInterest, 1);
        }
        else
        {
            // A share the list stopped showing is kept as long as one that
            // stopped being drawn: it counts as shown until now.
            Volatile.Write(ref root.LastDrawn, _time.GetTimestamp());
            Release(ref root.OtherInterest, 1);
            ScheduleRoot(root);
        }
    }

    /// <summary>
    /// Records that a folder of <paramref name="root"/> was drawn: what keeps a
    /// share's watch armed, and arms one that was let go.  Meant to be called
    /// once a frame for each share drawn; costs a clock read when the watch
    /// is up.
    /// </summary>
    public void NoteDrawn(WatchRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Volatile.Write(ref root.LastDrawn, _time.GetTimestamp());
        if (root.State == WatchState.Off)
        {
            ArmWhenIdle(root);
        }
    }

    /// <summary>
    /// Closes <paramref name="root"/>'s watch handle before returning, so its
    /// volume can be taken out: what to do when Windows asks whether a device
    /// may be removed.  Nothing under it is heard until <see cref="Rearm"/> or
    /// <see cref="Drop"/>.
    /// </summary>
    public void Suspend(WatchRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (root.Gate)
        {
            if (root.IsDropped)
            {
                return;
            }

            var watcher = root.Watcher;
            root.Watcher = null;
            root.Handle = null;
            root.Generation++;
            root.State = WatchState.Suspended;
            root.RetryAt = 0;
            root.RetryDelayMilliseconds = 0;
            watcher?.Stop();
        }
    }

    /// <summary>
    /// Arms <paramref name="root"/> again after <see cref="Suspend"/> - the
    /// removal was refused - or after it failed, without waiting for the next
    /// retry.  Off the calling thread; the epoch moves on once it is armed,
    /// since changes in between were not heard.
    /// </summary>
    public void Rearm(WatchRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (root.Gate)
        {
            if (root.IsDropped || root.Watcher is not null || root.Kind == WatchKind.None)
            {
                return;
            }

            root.Generation++;
            root.State = WatchState.Off;
            root.RetryAt = 0;
            root.RetryDelayMilliseconds = 0;
            if (root.Kind == WatchKind.Polling)
            {
                root.Kind = root.IsNetwork ? WatchKind.Network : WatchKind.Local;
            }
        }

        Arm(root);
    }

    /// <summary>
    /// Forgets <paramref name="root"/>: its volume is gone.  The handle is
    /// closed, and the next <see cref="RootFor"/> for a path on it makes a new
    /// root, which is how a drive that comes back at the same letter is
    /// watched afresh.
    /// </summary>
    public void Drop(WatchRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (root.Gate)
        {
            if (root.IsDropped)
            {
                return;
            }

            var watcher = root.Watcher;
            root.IsDropped = true;
            root.Watcher = null;
            root.Handle = null;
            root.Generation++;
            root.State = WatchState.Off;
            root.RetryAt = 0;
            watcher?.Stop();
        }

        lock (_rootsGate)
        {
            if (_roots.TryGetValue(root.Key, out var known) && ReferenceEquals(known, root))
            {
                _roots.Remove(root.Key);
            }

            var letters = _letters;
            for (var index = 0; index < letters.Length; index++)
            {
                if (ReferenceEquals(letters[index], root))
                {
                    letters[index] = null;
                }
            }

            _testRoots = [.. _testRoots.Where(test => !ReferenceEquals(test, root))];
            _rootList = [.. _roots.Values, .. _testRoots];
        }

        RootClosed?.Invoke(root);
    }

    /// <summary>
    /// Asks every polled root with something to show to be looked at now,
    /// rather than at its next five-second turn: for when the window is
    /// activated, which is when a change made elsewhere is most likely to be
    /// looked for.
    /// </summary>
    public void PollNow()
    {
        var now = _time.GetTimestamp();
        foreach (var root in _rootList)
        {
            if (Polls(root) && HasPollInterest(root, now))
            {
                PostPoll(root);
            }
        }
    }

    /// <summary>
    /// Forgets what each drive letter was found to be, so the next path on it
    /// asks again: drives came or went (WM_DEVICECHANGE), or a share was
    /// mapped.  Roots already made stay, with their watches; a letter that
    /// now stands for something else gets the right root when next asked.
    /// </summary>
    public void InvalidateVolumes()
    {
        lock (_rootsGate)
        {
            _letters = new WatchRoot?[26];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Under the gate, so a change being scheduled at this moment either
        // sets the timer before it goes or sees the hub disposed.
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }

        foreach (var root in _rootList)
        {
            bool closed;
            lock (root.Gate)
            {
                var watcher = root.Watcher;
                closed = watcher is not null;
                root.Watcher = null;
                root.Handle = null;
                root.Generation++;
                root.State = WatchState.Off;
                root.RetryAt = 0;
                watcher?.Stop();
            }

            if (closed)
            {
                RootClosed?.Invoke(root);
            }
        }
    }

    // ---- roots -------------------------------------------------------------------------

    /// <summary>
    /// The root a path is under: a test root it is inside, its share's, or its
    /// drive letter's - made when <paramref name="create"/> and not known yet.
    /// </summary>
    private WatchRoot? FindRoot(string path, bool create, int depth)
    {
        var span = path.AsSpan();
        if (span.StartsWith(@"\\?\", StringComparison.Ordinal) || span.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = span[4..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return ShareRoot(rest[4..], create);
            }

            span = rest;
        }

        foreach (var test in _testRoots)
        {
            if (IsInside(span, test.Key))
            {
                return test;
            }
        }

        if (span.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ShareRoot(span[2..], create);
        }

        return span.Length >= 2 && span[1] == ':' && char.IsAsciiLetter(span[0])
            ? LetterRoot(char.ToUpperInvariant(span[0]), create, depth)
            : null;
    }

    /// <summary>The root of the share a UNC path - given without its two leading separators - is on.</summary>
    private WatchRoot? ShareRoot(ReadOnlySpan<char> unc, bool create)
    {
        var server = unc.IndexOfAny('\\', '/');
        if (server <= 0)
        {
            return null;
        }

        var share = unc[(server + 1)..].IndexOfAny('\\', '/');
        var length = share < 0 ? unc.Length : server + 1 + share;
        if (length == server + 1)
        {
            return null;
        }

        Span<char> key = length + 2 <= 512 ? stackalloc char[length + 2] : new char[length + 2];
        key[0] = '\\';
        key[1] = '\\';
        unc[..length].CopyTo(key[2..]);
        key.Replace('/', '\\');
        lock (_rootsGate)
        {
            if (_rootLookup.TryGetValue(key, out var known))
            {
                return known;
            }

            return create ? AddRootLocked(key.ToString(), WatchKind.Network, isNetwork: true) : null;
        }
    }

    /// <summary>
    /// The root a drive letter is on, found out on first asking: a volume of
    /// its own, the share it is mapped to, or - for a <c>subst</c> letter - the
    /// root of the folder it stands for, with the letter added as a spelling.
    /// </summary>
    private WatchRoot? LetterRoot(char letter, bool create, int depth)
    {
        var index = letter - 'A';
        if (_letters[index] is { IsDropped: false } known)
        {
            return known;
        }

        if (!create || depth > 4)
        {
            return null;
        }

        var found = _volumes.Resolve(letter);
        var spelling = $@"{letter}:\";

        // A subst letter's folder may itself be on another letter or a share:
        // its root is found first, outside the lock.
        WatchRoot? target = null;
        string? inner = null;
        if (found.SubstTarget is { } substituted && FindRoot(substituted, create: true, depth + 1) is { } substitutedRoot)
        {
            target = substitutedRoot;
            inner = substitutedRoot.InnerPathOf(substituted);
        }

        WatchRoot root;
        lock (_rootsGate)
        {
            if (_letters[index] is { IsDropped: false } raced)
            {
                return raced;
            }

            if (target is not null && inner is not null)
            {
                root = target;
                root.AddAlias(new WatchAlias(spelling, inner));
            }
            else if (found.DriveType == WatchNative.DriveRemote)
            {
                if (found.Share is { } share && share.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    root = _rootLookup.TryGetValue(share, out var known2) ? known2 : AddRootLocked(share, WatchKind.Network, isNetwork: true);
                    root.AddAlias(new WatchAlias(spelling, string.Empty));
                }
                else
                {
                    root = ReuseOrAddLocked(spelling, WatchKind.Network, isNetwork: true);
                }
            }
            else if (found.DriveType is WatchNative.DriveCdRom or WatchNative.DriveNoRootDirectory or WatchNative.DriveUnknown)
            {
                root = ReuseOrAddLocked(spelling, WatchKind.None, isNetwork: false);
            }
            else
            {
                root = ReuseOrAddLocked(spelling, WatchKind.Local, isNetwork: false);
            }

            _letters[index] = root;
        }

        if (root.Kind == WatchKind.Local)
        {
            ArmWhenIdle(root);
        }

        return root;
    }

    /// <summary>
    /// The root keyed <paramref name="key"/> if there is one of the same kind,
    /// otherwise a new one in its place - a letter that had no drive when first
    /// asked about and has one now.
    /// </summary>
    private WatchRoot ReuseOrAddLocked(string key, WatchKind kind, bool isNetwork)
    {
        if (_roots.TryGetValue(key, out var known) && !known.IsDropped
            && (known.Kind == WatchKind.None) == (kind == WatchKind.None) && known.IsNetwork == isNetwork)
        {
            return known;
        }

        if (known is not null)
        {
            lock (known.Gate)
            {
                known.IsDropped = true;
            }
        }

        return AddRootLocked(key, kind, isNetwork);
    }

    private WatchRoot AddRootLocked(string key, WatchKind kind, bool isNetwork, int bufferBytes = 0)
    {
        var root = new WatchRoot(key, kind, isNetwork) { BufferBytes = bufferBytes };
        root.Records = new RootRecords(this, root);
        _roots[key] = root;
        _rootList = [.. _roots.Values, .. _testRoots];
        return root;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool IsInside(ReadOnlySpan<char> path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || folder.EndsWith('\\') || path[folder.Length] == '\\');

    /// <summary>Takes <paramref name="count"/> off an interest count, never below zero - a root dropped and made again starts from zero.</summary>
    private static void Release(ref int interest, int count)
    {
        var seen = Volatile.Read(ref interest);
        while (seen > 0)
        {
            var was = Interlocked.CompareExchange(ref interest, Math.Max(0, seen - count), seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// Whether a registered target has gone for good: a nested folder that a
    /// refresh dropped, or that is under one.  Such registrations are dropped
    /// when met, and swept when they pile up.
    /// </summary>
    internal static bool IsGone(object target) => target is NestedFolder folder && NestedTree.IsDetached(folder);

    /// <summary>
    /// Sweeps the registrations of forgotten folders, off the UI thread, once
    /// there are twice as many nested registrations as the last sweep left.
    /// A refresh that drops a folder unregisters only that folder; everything
    /// read below it stays registered until swept here or met in a drain.
    /// </summary>
    private void SweepWhenStale()
    {
        var targets = Registry.NestedTargets;
        if (targets < SweepFloor || targets <= 2 * Volatile.Read(ref _nestedAfterSweep) || Interlocked.Exchange(ref _sweeping, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static hub => hub.Sweep(), this, preferLocal: false);
    }

    /// <summary>Drops every registration of a forgotten nested folder; returns how many.  Any thread.</summary>
    internal int Sweep()
    {
        var removed = 0;
        try
        {
            foreach (var key in Registry.Keys)
            {
                var count = Registry.RemoveGone(key, Gone, out var root);
                if (count > 0)
                {
                    removed += count;
                    if (root is not null)
                    {
                        Release(ref root.NestedInterest, count);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _nestedAfterSweep, Registry.NestedTargets);
            Volatile.Write(ref _sweeping, 0);
        }

        return removed;
    }

    /// <summary>What a slot starts with: a driver that does nothing, until the owner gives it one.</summary>
    private sealed class NoDriver : IFrameDriver
    {
        public static readonly NoDriver Instance = new();

        public void Wake()
        {
        }
    }
}
