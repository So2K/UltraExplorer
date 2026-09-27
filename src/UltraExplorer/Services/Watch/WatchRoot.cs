using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace UltraExplorer.Services.Watch;

/// <summary>How changes under a watch root are found out.</summary>
public enum WatchKind
{
    /// <summary>A fixed, removable, RAM or virtual disk: one recursive watch on the drive's root.</summary>
    Local,

    /// <summary>A share: one watch per share, armed off the UI thread.</summary>
    Network,

    /// <summary>A volume that refuses a watch (WSL's, say): its drawn folders are looked at every few seconds.</summary>
    Polling,

    /// <summary>Not watched at all: a CD or DVD.</summary>
    None
}

/// <summary>What a root's watch is doing right now.</summary>
public enum WatchState
{
    /// <summary>A watch is open, and every change under the root is heard.</summary>
    Armed,

    /// <summary>
    /// Its handle was closed so the volume could be taken out - Windows asked
    /// whether it may remove the device - and nothing is heard until
    /// <see cref="ChangeHub.Rearm"/>.
    /// </summary>
    Suspended,

    /// <summary>The volume refuses a watch: the folders drawn on it are looked at every few seconds instead.</summary>
    Polling,

    /// <summary>
    /// No watch: not armed yet, being armed, let go because nothing showed
    /// the share for a while, dropped, or failed - in which case it is tried
    /// again after 1, 2, 4 … 60 seconds and its drawn folders are polled meanwhile.
    /// </summary>
    Off
}

/// <summary>
/// One watched volume or share, which every folder under it shares: what
/// kind of watch it has, and <see cref="Epoch"/>, which moves on whenever
/// changes under it may have been missed - the watch overflowed, or was
/// armed again - so every folder read before that knows to be read again
/// the next time it is drawn, without anything walking the folders to say so.
///
/// <para>A root is known by more than one spelling when more than one leads to
/// it: a mapped drive and the share's own UNC name, or a <c>subst</c> letter
/// and the folder it stands for.  One watch serves them all.  The folders
/// registered under a root are kept by their place under the watched
/// directory, whatever spelling they were registered with, so a record - which
/// names its folder that way - is looked up once, as it comes, and told to
/// every spelling registered for that folder (<see cref="Prefixes"/>).</para>
///
/// <para>The public members are what the folders, the read queue and the
/// window need; the rest is the <see cref="ChangeHub"/>'s bookkeeping, changed
/// under <see cref="Gate"/> or with interlocked operations, since watchers,
/// timers and the UI thread all reach it.</para>
/// </summary>
public sealed class WatchRoot
{
    private volatile WatchKind _kind;
    private volatile WatchState _state;
    private volatile SafeFileHandle? _handle;
    private volatile WatchAlias[] _aliases;
    private volatile string[] _prefixes;

    /// <summary>The registered folders under the root, by their place under the watched directory: a registered path, or several spellings' as a string array.</summary>
    private readonly ConcurrentDictionary<InnerPath, object> _interests = new(InnerPath.Comparer.Instance);
    private readonly ConcurrentDictionary<InnerPath, object>.AlternateLookup<ReadOnlySpan<char>> _interestLookup;

    public WatchRoot(string key, WatchKind kind, bool isNetwork)
    {
        Key = key;
        _kind = kind;
        _state = WatchState.Off;
        IsNetwork = isNetwork;
        WatchedPath = WatchAlias.WithSeparator(key);
        var own = new WatchAlias(WatchedPath, string.Empty);
        _aliases = [own];
        _prefixes = [own.Spelling];
        _interestLookup = _interests.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// The root's path, as the change hub keys it: a drive's root (C:\) or a
    /// share's (\\server\share).
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// What kind of watch the root has.  Becomes <see cref="WatchKind.Polling"/>
    /// when the volume turns out to refuse a watch, which the first attempt to
    /// arm one finds out.
    /// </summary>
    public WatchKind Kind
    {
        get => _kind;
        internal set => _kind = value;
    }

    public WatchState State
    {
        get => _state;
        internal set => _state = value;
    }

    /// <summary>
    /// On a share: reads under it go one at a time, so a share that hangs
    /// cannot hold up the local drives.  Kept apart from <see cref="Kind"/>:
    /// a share whose watch failed is polled, and is still a share.
    /// </summary>
    public bool IsNetwork { get; }

    /// <summary>
    /// Every spelling the root is known by, each ending in a separator: its own
    /// (C:\, \\server\share\) first, then a mapped letter's or a <c>subst</c>
    /// letter's.  A new array whenever a spelling is added.
    /// </summary>
    public string[] Prefixes => _prefixes;

    /// <summary>
    /// The open watch's directory handle while <see cref="State"/> is
    /// <see cref="WatchState.Armed"/>, and null otherwise: what the window
    /// registers for device notifications, so it hears before a volume is
    /// taken out and can have the handle closed (<see cref="ChangeHub.Suspend"/>).
    /// </summary>
    public SafeFileHandle? Handle
    {
        get => _handle;
        internal set => _handle = value;
    }

    /// <summary>
    /// Moves on, from any thread (<see cref="Interlocked.Increment(ref int)"/>),
    /// whenever changes under the root may have been missed.  A field, so it
    /// can be read and bumped atomically.
    /// </summary>
    public int Epoch;

    public override string ToString() => $"{Key} ({Kind}, {State})";

    // ---- the change hub's bookkeeping --------------------------------------------------

    /// <summary>The directory the watch is opened on: the key with a separator after it.</summary>
    internal string WatchedPath { get; }

    /// <summary>The spellings, for the watcher's threads: replaced whole, never changed in place.</summary>
    internal WatchAlias[] Aliases => _aliases;

    /// <summary>Guards the watch's state: arming, closing, retrying.</summary>
    internal readonly Lock Gate = new();

    /// <summary>The open watch, under <see cref="Gate"/>.</summary>
    internal DirectoryChangeWatcher? Watcher { get; set; }

    /// <summary>
    /// Counts the times the root's watch was closed or given up on, under
    /// <see cref="Gate"/>: an arm that began before the last of them is out of
    /// date when it finishes, and its watch is closed again at once.
    /// </summary>
    internal int Generation { get; set; }

    /// <summary>The <see cref="Generation"/> an arm is under way for, or -1 when none is.</summary>
    internal int ArmingGeneration { get; set; } = -1;

    /// <summary>Arms finished, well or not: bumped once everything an arm sets is set.</summary>
    internal int ArmAttempts;

    /// <summary>Set by the hub's Drop: the volume went, and the root is no longer handed out.</summary>
    internal bool IsDropped { get; set; }

    /// <summary>After a failure: when to try again, as a hub timestamp; zero when nothing failed.</summary>
    internal long RetryAt;

    /// <summary>The wait before the next retry after this one, in milliseconds: 1, 2, 4 … 60 seconds.</summary>
    internal double RetryDelayMilliseconds;

    /// <summary>When the drawn folders are next looked at, while the root polls.</summary>
    internal long NextPoll;

    /// <summary>When a folder of the root was last drawn (<see cref="ChangeHub.NoteDrawn"/>), as a hub timestamp.</summary>
    internal long LastDrawn;

    /// <summary>Folders of the nested tree registered under the root.</summary>
    internal int NestedInterest;

    /// <summary>Registrations from the folder list and the tree canvas: what keeps a share's watch armed while nothing draws it.</summary>
    internal int OtherInterest;

    /// <summary>Set while an <see cref="IChangeSink.EpochBumped"/> for the root waits to be handed on, so a burst of overflows is one.</summary>
    internal int BumpQueued;

    /// <summary>When an overflow last moved the epoch on, as a hub timestamp; zero before the first.</summary>
    internal long LastOverflowBump;

    /// <summary>
    /// Set when an overflow came too soon after the last one to move the
    /// epoch on at once: the timer moves it on once the least gap has passed
    /// (<see cref="ChangeHub.OverflowGapMilliseconds"/>).
    /// </summary>
    internal int OverflowBumpOwed;

    /// <summary>Set while an <see cref="IChangeSink.PollDue"/> for the root waits to be handed on.</summary>
    internal int PollQueued;

    /// <summary>What turns the watch's records into pending changes; made by the hub with the root.</summary>
    internal RootRecords? Records { get; set; }

    /// <summary>The watch's buffer in bytes; zero for the default of its kind.</summary>
    internal int BufferBytes { get; init; }

    /// <summary>How long the last successful arm took: opening the handle and asking for the first changes.</summary>
    internal double ArmMilliseconds { get; set; }

    /// <summary>The Windows error the last arm or the last read failed with; zero when none did.</summary>
    internal int LastError { get; set; }

    /// <summary>Folders registered under the root.</summary>
    internal int InterestCount => _interests.Count;

    /// <summary>
    /// Whether a folder is registered under the root, given as a record names
    /// it - relative to the watched directory, empty for the directory itself;
    /// and if so, its registered path, or a string array of several spellings'.
    /// Any thread, no lock, no allocation.
    /// </summary>
    internal bool TryFindInterest(ReadOnlySpan<char> inner, [NotNullWhen(true)] out object? keys) =>
        _interestLookup.TryGetValue(inner, out keys);

    /// <summary>Adds a registered path to the lookup; the registry calls it, under its lock, when the path's first target registers.</summary>
    internal void AddInterest(string key)
    {
        if (InnerOf(key) is not { } inner)
        {
            return;
        }

        // Spellings are told apart ignoring case, as the registry tells its
        // paths apart: two that differ in case alone are one registered path,
        // and would otherwise be told every change twice, or one of them kept
        // here after the registry let the path go.
        if (!_interests.TryGetValue(inner, out var known))
        {
            _interests[inner] = key;
        }
        else if (known is string single)
        {
            if (!single.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _interests[inner] = new[] { single, key };
            }
        }
        else if (!HasSpelling((string[])known, key))
        {
            _interests[inner] = (string[])[.. (string[])known, key];
        }
    }

    /// <summary>Takes a registered path out of the lookup; the registry calls it, under its lock, when the path's last target goes.</summary>
    internal void RemoveInterest(string key)
    {
        if (InnerOf(key) is not { } inner || !_interests.TryGetValue(inner, out var known))
        {
            return;
        }

        if (known is string single)
        {
            if (single.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _interests.TryRemove(inner, out _);
            }

            return;
        }

        var kept = ((string[])known).Where(other => !other.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (kept.Length == 0)
        {
            _interests.TryRemove(inner, out _);
        }
        else
        {
            _interests[inner] = kept.Length == 1 ? kept[0] : kept;
        }
    }

    /// <summary>Whether <paramref name="spellings"/> has <paramref name="key"/>, ignoring case.</summary>
    private static bool HasSpelling(string[] spellings, string key)
    {
        foreach (var spelling in spellings)
        {
            if (spelling.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a registered path is somewhere inside <paramref name="inner"/> -
    /// a folder as a record names it, relative to the watched directory - and
    /// not that folder itself.  No string is made unless the path is spelt
    /// with a <c>subst</c> letter.
    /// </summary>
    internal bool IsInside(string key, ReadOnlySpan<char> inner)
    {
        if (inner.IsEmpty || InnerOf(key) is not { } place)
        {
            return false;
        }

        var span = place.Span;
        return span.Length > inner.Length && span[inner.Length] == '\\' && span.StartsWith(inner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where a registered path is under the watched directory, through the
    /// spelling it is written with; null when none leads to it.  No string is
    /// made unless the spelling is a <c>subst</c> letter's.
    /// </summary>
    private InnerPath? InnerOf(string key)
    {
        foreach (var alias in _aliases)
        {
            if (key.Equals(alias.TopKey, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Inner.Length == 0 ? new InnerPath(key, key.Length) : new InnerPath(alias.Inner, 0);
            }

            if (key.StartsWith(alias.Spelling, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Inner.Length == 0
                    ? new InnerPath(key, alias.Spelling.Length)
                    : new InnerPath(string.Concat(alias.Inner, "\\", key.AsSpan(alias.Spelling.Length)), 0);
            }
        }

        return null;
    }

    /// <summary>Adds a spelling the root is known by, unless it has it already.</summary>
    internal void AddAlias(WatchAlias alias)
    {
        lock (Gate)
        {
            foreach (var known in _aliases)
            {
                if (known.Spelling.Equals(alias.Spelling, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _aliases = [.. _aliases, alias];
            _prefixes = [.. _prefixes, alias.Spelling];
        }
    }

    /// <summary>
    /// Where <paramref name="path"/> - spelt under any of the root's spellings -
    /// is under the watched directory, without separators at either end: ""
    /// for the watched directory itself, null when no spelling leads to it.
    /// </summary>
    internal string? InnerPathOf(string path)
    {
        foreach (var alias in _aliases)
        {
            if (alias.TryInnerPathOf(path, out var inner))
            {
                return inner;
            }
        }

        return null;
    }
}

/// <summary>
/// One spelling of a watch root: the path its top is written with, and where
/// that top is under the watched directory.  A drive's own spelling is C:\
/// with nothing under it; a <c>subst</c> letter S: standing for
/// C:\Work\Site is S:\ with Work\Site under it, on C:'s watch.  Turns a
/// record's folder, relative to the watched directory, into that folder's
/// path as the registrations spell it - into a buffer, without making a
/// string, since every record of every watch passes through here.
/// </summary>
internal sealed class WatchAlias
{
    public WatchAlias(string spelling, string inner)
    {
        Spelling = WithSeparator(spelling);
        TopKey = KeyOf(Spelling);
        Inner = inner.Trim('\\', '/');
    }

    /// <summary>The spelling of the alias's top, ending in a separator: C:\, S:\, \\server\share\.</summary>
    public string Spelling { get; }

    /// <summary>How the folder at the top is registered: C:\ for a drive, \\server\share for a share or a folder.</summary>
    public string TopKey { get; }

    /// <summary>Where the top is under the watched directory, without separators at either end; empty for the directory itself.</summary>
    public string Inner { get; }

    /// <summary>
    /// Writes into <paramref name="buffer"/> the path of <paramref name="relative"/>
    /// - a folder given relative to the watched directory, empty for the
    /// directory itself - as spelt through this alias.  False when the folder
    /// is not under the alias's top.  The buffer grows when a path needs it to.
    /// </summary>
    public bool Compose(ReadOnlySpan<char> relative, ref char[] buffer, out int length)
    {
        if (Inner.Length > 0)
        {
            if (!relative.StartsWith(Inner, StringComparison.OrdinalIgnoreCase))
            {
                length = 0;
                return false;
            }

            if (relative.Length > Inner.Length)
            {
                if (relative[Inner.Length] != '\\')
                {
                    length = 0;
                    return false;
                }

                relative = relative[(Inner.Length + 1)..];
            }
            else
            {
                relative = default;
            }
        }

        if (relative.IsEmpty)
        {
            length = TopKey.Length;
            Ensure(ref buffer, length + 1);
            TopKey.CopyTo(buffer);
            return true;
        }

        length = Spelling.Length + relative.Length;
        Ensure(ref buffer, length + 1);
        Spelling.CopyTo(buffer);
        relative.CopyTo(buffer.AsSpan(Spelling.Length));
        return true;
    }

    /// <summary>Where <paramref name="path"/> is under the watched directory, when it is spelt with this alias.</summary>
    public bool TryInnerPathOf(string path, out string inner)
    {
        var span = path.AsSpan().TrimEnd('\\');
        var top = Spelling.AsSpan().TrimEnd('\\');
        if (span.Equals(top, StringComparison.OrdinalIgnoreCase))
        {
            inner = Inner;
            return true;
        }

        if (path.StartsWith(Spelling, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path.AsSpan(Spelling.Length).Trim('\\');
            inner = Inner.Length == 0 ? rest.ToString() : string.Concat(Inner, "\\", rest);
            return true;
        }

        inner = string.Empty;
        return false;
    }

    /// <summary>The path with one separator at its end.</summary>
    public static string WithSeparator(string path) =>
        path.EndsWith('\\') ? path : path.EndsWith('/') ? string.Concat(path.AsSpan(0, path.Length - 1), "\\") : path + "\\";

    /// <summary>
    /// A folder's path as registrations spell it: no separator at the end,
    /// except a drive's root, which keeps its one (C:\) - the same as
    /// <c>NestedFolder.FullPath</c>.  Returns the same string when it is
    /// already in that form.
    /// </summary>
    public static string KeyOf(string path)
    {
        if (path.Length == 2 && path[1] == ':')
        {
            return path + "\\";
        }

        var end = path.Length;
        while (end > 0 && (path[end - 1] == '\\' || path[end - 1] == '/'))
        {
            end--;
        }

        if (end == path.Length)
        {
            return path;
        }

        if (end == 2 && path[1] == ':')
        {
            return path.Length == 3 && path[2] == '\\' ? path : string.Concat(path.AsSpan(0, 2), "\\");
        }

        return end == 0 ? path : path[..end];
    }

    private static void Ensure(ref char[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
        }
    }
}
