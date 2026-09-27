using System.Diagnostics;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// The roots' watches: arming them off the calling thread, what a failure
/// does, retrying, polling a root that has no watch, and letting a share go
/// once nothing shows it.
/// </summary>
public sealed partial class ChangeHub
{
    /// <summary>
    /// A test's stand-in for the system while arming: given the root, the
    /// Windows error its arm should fail with, or zero to arm for real.  May
    /// block, to stand for a share that hangs.
    /// </summary>
    internal Func<WatchRoot, int>? ArmFailureForTests { get; set; }

    /// <summary>
    /// Adds a root watching <paramref name="directory"/> rather than a whole
    /// volume, for tests: paths inside it resolve to it before anything else,
    /// so a test hears only its own folder's changes.  Armed unless
    /// <paramref name="arm"/> is false or it is not <see cref="WatchKind.Local"/>
    /// - a <see cref="WatchKind.Network"/> one arms like a share, when drawn.
    /// </summary>
    internal WatchRoot AddRootForTests(string directory, WatchKind kind = WatchKind.Local, int bufferBytes = 0, bool arm = true)
    {
        var root = new WatchRoot(WatchAlias.KeyOf(Path.GetFullPath(directory)), kind, isNetwork: kind == WatchKind.Network) { BufferBytes = bufferBytes };
        root.Records = new RootRecords(this, root);
        lock (_rootsGate)
        {
            _testRoots = [.. _testRoots, root];
            _rootList = [.. _roots.Values, .. _testRoots];
        }

        if (arm && kind == WatchKind.Local)
        {
            Arm(root);
        }

        return root;
    }

    /// <summary>Runs a buffer of records, laid out as Windows would, through a root as if its watch had heard them; for tests, on a root with no watch running.</summary>
    internal int FeedForTests(WatchRoot root, ReadOnlySpan<byte> records, bool details)
    {
        var count = DirectoryChangeWatcher.Parse(records, details, root.Records!);
        root.Records!.EndOfBuffer();
        return count;
    }

    /// <summary>The buffer a root's watch gets: its own size, or the override, or 256 KB locally and 64 KB - SMB's most - on a share.</summary>
    internal int BufferFor(WatchRoot root)
    {
        var bytes = root.BufferBytes > 0 ? root.BufferBytes
            : BufferOverride > 0 ? BufferOverride
            : root.IsNetwork ? NetworkBufferBytes : LocalBufferBytes;
        return root.IsNetwork ? Math.Min(bytes, NetworkBufferBytes) : bytes;
    }

    /// <summary>Arms a root that has no watch and is not waiting to retry one; nothing otherwise.</summary>
    private void ArmWhenIdle(WatchRoot root)
    {
        if (root.State == WatchState.Off && Volatile.Read(ref root.RetryAt) == 0 && root.Kind is WatchKind.Local or WatchKind.Network)
        {
            Arm(root);
        }
    }

    /// <summary>
    /// Starts arming <paramref name="root"/> off the calling thread - a local
    /// volume's on the thread pool, a share's on a thread of its own, since
    /// opening a share that went away blocks for as long as the network
    /// takes to say so.  One arm at a time per root.
    /// </summary>
    private void Arm(WatchRoot root)
    {
        int generation;
        lock (root.Gate)
        {
            if (_disposed || root.IsDropped || root.Kind == WatchKind.None || root.Watcher is not null
                || root.State == WatchState.Suspended || root.ArmingGeneration == root.Generation)
            {
                return;
            }

            generation = root.Generation;
            root.ArmingGeneration = generation;
        }

        var work = new ArmWork(this, root, generation);
        if (root.IsNetwork)
        {
            Task.Factory.StartNew(static state => ((ArmWork)state!).Run(), work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        else
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), work, preferLocal: false);
        }
    }

    private sealed record ArmWork(ChangeHub Hub, WatchRoot Root, int Generation)
    {
        public void Run() => Hub.ArmNow(Root, Generation);
    }

    /// <summary>
    /// Opens the root's watch.  On success the epoch moves on - anything read
    /// before now may have missed changes - and the root is armed; a volume
    /// that refuses a watch is polled from now on; any other failure is tried
    /// again later.  An arm overtaken by a suspension, a drop or another arm
    /// closes what it opened.
    /// </summary>
    private void ArmNow(WatchRoot root, int generation)
    {
        var started = Stopwatch.GetTimestamp();
        DirectoryChangeWatcher? watcher = null;
        var error = ArmFailureForTests?.Invoke(root) ?? 0;
        if (error == 0)
        {
            error = DirectoryChangeWatcher.Open(root.WatchedPath, BufferFor(root), allowDetails: !root.IsNetwork, root.Records!, out watcher);
        }

        var armed = false;
        var stale = false;
        lock (root.Gate)
        {
            // The first read can fail between the watch being opened and this
            // lock being taken, and the failure, finding no watcher here yet,
            // is dropped (OnWatchFailed): an arm that found its watch already
            // ended failed, and is retried and polled like any failed arm,
            // rather than counted armed with nothing listening.
            if (error == 0 && watcher!.HasEnded)
            {
                error = watcher.EndedWith is not 0 and var ended ? ended : WatchNative.ErrorInvalidFunction;
                watcher = null;
            }

            if (generation != root.Generation || root.IsDropped || _disposed || root.Watcher is not null)
            {
                stale = true;
            }
            else if (error == 0)
            {
                root.Watcher = watcher;
                root.Handle = watcher!.Handle;
                root.State = WatchState.Armed;
                root.RetryAt = 0;
                root.RetryDelayMilliseconds = 0;
                root.LastError = 0;
                root.ArmMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Interlocked.Increment(ref root.Epoch);
                armed = true;
            }
            else
            {
                FailLocked(root, error, _time.GetTimestamp());
            }

            if (root.ArmingGeneration == generation)
            {
                root.ArmingGeneration = -1;
            }

            Interlocked.Increment(ref root.ArmAttempts);
        }

        if (stale)
        {
            watcher?.Stop();
            return;
        }

        if (armed)
        {
            RootArmed?.Invoke(root);
            if (HasInterest(root))
            {
                PostBump(root);
            }
        }

        ScheduleRoot(root);
    }

    /// <summary>
    /// What a failure to arm or keep a watch leaves: a volume that refuses
    /// watches is polled for good; anything else is retried after 1, 2, 4 …
    /// 60 seconds, and polled meanwhile.  Under the root's gate.
    /// </summary>
    private void FailLocked(WatchRoot root, int error, long now)
    {
        root.LastError = error;
        if (WatchNative.IsRefusal(error))
        {
            root.Kind = WatchKind.Polling;
            root.State = WatchState.Polling;
            Volatile.Write(ref root.RetryAt, 0);
        }
        else
        {
            root.State = WatchState.Off;
            var delay = root.RetryDelayMilliseconds <= 0 ? FirstRetryMilliseconds : Math.Min(root.RetryDelayMilliseconds * 2, LongestRetryMilliseconds);
            root.RetryDelayMilliseconds = delay;
            Volatile.Write(ref root.RetryAt, now + Ticks(delay));
        }

        if (Volatile.Read(ref root.NextPoll) <= now)
        {
            Volatile.Write(ref root.NextPoll, now + _pollTicks);
        }
    }

    /// <summary>A running watch ended with an error, on its own thread: its handle is already closed.</summary>
    internal void OnWatchFailed(WatchRoot root, DirectoryChangeWatcher watcher, int error)
    {
        lock (root.Gate)
        {
            if (!ReferenceEquals(root.Watcher, watcher))
            {
                return;
            }

            root.Watcher = null;
            root.Handle = null;
            root.Generation++;
            FailLocked(root, error, _time.GetTimestamp());
        }

        RootClosed?.Invoke(root);
        ScheduleRoot(root);
    }

    /// <summary>Lets a share's watch go: nothing has drawn it for <see cref="NetworkKeepMilliseconds"/> and no list or graph shows it.</summary>
    private void Disarm(WatchRoot root)
    {
        lock (root.Gate)
        {
            if (root.Watcher is not { } watcher)
            {
                return;
            }

            root.Watcher = null;
            root.Handle = null;
            root.Generation++;
            root.State = WatchState.Off;
            watcher.Stop();
        }

        RootClosed?.Invoke(root);
    }

    /// <summary>
    /// The timer's pass over the roots: moves on the epochs overflows put
    /// off, starts the retries that are due, posts the polls that are, lets
    /// go the shares nothing has drawn for long enough, and returns when it
    /// next needs to look.
    /// </summary>
    private long TendRoots(long now)
    {
        var next = Unset;
        foreach (var root in _rootList)
        {
            if (root.IsDropped)
            {
                continue;
            }

            if (Volatile.Read(ref root.OverflowBumpOwed) != 0)
            {
                var bumpAt = Volatile.Read(ref root.LastOverflowBump) + _overflowGapTicks;
                if (bumpAt > now)
                {
                    next = Math.Min(next, bumpAt);
                }
                else if (Interlocked.Exchange(ref root.OverflowBumpOwed, 0) != 0)
                {
                    BumpForOverflow(root, now);
                }
            }

            var retryAt = Volatile.Read(ref root.RetryAt);
            if (root.State == WatchState.Off && retryAt != 0)
            {
                if (retryAt <= now && root.IsNetwork && !HasPollInterest(root, now))
                {
                    // A share nothing shows any more is not tried again - an
                    // attempt on one that went away holds a thread for as long
                    // as the network takes to say so - until it is drawn.
                    Interlocked.CompareExchange(ref root.RetryAt, 0, retryAt);
                }
                else if (retryAt <= now)
                {
                    // Held off while the retry runs; the arm sets the next.
                    Interlocked.CompareExchange(ref root.RetryAt, now + Ticks(LongestRetryMilliseconds), retryAt);
                    Arm(root);
                }

                if (Volatile.Read(ref root.RetryAt) is var pending and not 0)
                {
                    next = Math.Min(next, pending);
                }
            }

            if (Polls(root))
            {
                if (Volatile.Read(ref root.NextPoll) <= now)
                {
                    Volatile.Write(ref root.NextPoll, now + _pollTicks);
                    if (HasPollInterest(root, now))
                    {
                        PostPoll(root);
                    }
                }

                next = Math.Min(next, Volatile.Read(ref root.NextPoll));
            }

            if (root.State == WatchState.Armed && root.IsNetwork && Volatile.Read(ref root.OtherInterest) <= 0)
            {
                var idleAt = Volatile.Read(ref root.LastDrawn) + _keepTicks;
                if (idleAt <= now)
                {
                    Disarm(root);
                }
                else
                {
                    next = Math.Min(next, idleAt);
                }
            }
        }

        return next;
    }

    /// <summary>When the timer next needs to look at <paramref name="root"/>, or <see cref="Unset"/>.</summary>
    private long NextRootDue(WatchRoot root)
    {
        if (root.IsDropped)
        {
            return Unset;
        }

        var due = Unset;
        if (Volatile.Read(ref root.OverflowBumpOwed) != 0)
        {
            due = Volatile.Read(ref root.LastOverflowBump) + _overflowGapTicks;
        }

        var retryAt = Volatile.Read(ref root.RetryAt);
        if (root.State == WatchState.Off && retryAt != 0)
        {
            due = Math.Min(due, retryAt);
        }

        if (Polls(root))
        {
            due = Math.Min(due, Volatile.Read(ref root.NextPoll));
        }

        if (root.State == WatchState.Armed && root.IsNetwork && Volatile.Read(ref root.OtherInterest) <= 0)
        {
            due = Math.Min(due, Volatile.Read(ref root.LastDrawn) + _keepTicks);
        }

        return due;
    }

    /// <summary>Whether a root is polled: its volume refuses a watch, or its watch failed and waits to be tried again.</summary>
    private static bool Polls(WatchRoot root) =>
        root.State == WatchState.Polling || (root.State == WatchState.Off && Volatile.Read(ref root.RetryAt) != 0);

    /// <summary>
    /// Whether polling a root could find anything: a local one while any of
    /// its folders is registered, a share while it was drawn in the last
    /// thirty seconds or the list or graph shows it.
    /// </summary>
    private bool HasPollInterest(WatchRoot root, long now) => root.IsNetwork
        ? Volatile.Read(ref root.OtherInterest) > 0 || now - Volatile.Read(ref root.LastDrawn) < _keepTicks
        : Volatile.Read(ref root.NestedInterest) > 0 || Volatile.Read(ref root.OtherInterest) > 0;

    /// <summary>Whether anything registered under the root could be out of date after an epoch bump.</summary>
    private static bool HasInterest(WatchRoot root) =>
        Volatile.Read(ref root.NestedInterest) > 0 || Volatile.Read(ref root.OtherInterest) > 0;
}
