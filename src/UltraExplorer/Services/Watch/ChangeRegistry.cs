using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// The folders someone wants to hear about, by path: which targets each
/// consumer registered for a folder - the nested folder, the list, the graph
/// node - so a change can be handed to them all.  Paths are compared ignoring
/// case, as Windows does, and keyed exactly as registered - for a nested
/// folder its <c>FullPath</c>, so the key a change is handed on with is that
/// very string.
///
/// <para>What a watcher looks each record up in is not this dictionary but
/// its root's own (<see cref="WatchRoot.TryFindInterest"/>), keyed by where
/// each registered folder is under the watched directory: the record's folder
/// as the record spells it, with no path built.  The registry keeps that one
/// up to date - a path gets its entry there when its first target registers
/// and loses it with its last - under the same lock as its own changes.</para>
///
/// <para>Lookups take no lock and may run on any thread; changes are made
/// under one lock, and a path's targets are replaced whole rather than changed
/// in place, so a reader always sees a consistent set.</para>
/// </summary>
internal sealed class ChangeRegistry
{
    private readonly ConcurrentDictionary<string, Interest> _paths = new(PathComparer.Instance);
    private readonly ConcurrentDictionary<string, Interest>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly Lock _gate = new();
    private volatile string[] _listKeys = [];
    private int _nestedTargets;

    public ChangeRegistry() => _lookup = _paths.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Who is registered for one path - one target per consumer, or a
    /// <see cref="TargetList"/> when several are - and the root whose
    /// watcher looks the path up.
    /// </summary>
    internal sealed class Interest
    {
        public object? Nested;
        public object? List;
        public object? Graph;
        public WatchRoot? Root;

        public bool IsEmpty => Nested is null && List is null && Graph is null;

        public object? this[ChangeConsumer consumer]
        {
            get => consumer switch
            {
                ChangeConsumer.Nested => Nested,
                ChangeConsumer.List => List,
                _ => Graph
            };
            set
            {
                switch (consumer)
                {
                    case ChangeConsumer.Nested:
                        Nested = value;
                        break;
                    case ChangeConsumer.List:
                        List = value;
                        break;
                    default:
                        Graph = value;
                        break;
                }
            }
        }
    }

    /// <summary>Several targets of one consumer for one path; never changed once made.</summary>
    internal sealed class TargetList(object[] items)
    {
        public object[] Items { get; } = items;
    }

    /// <summary>Paths registered.</summary>
    public int Count => _paths.Count;

    /// <summary>Nested targets registered, for the hub to tell when stale ones have piled up.</summary>
    public int NestedTargets => Volatile.Read(ref _nestedTargets);

    /// <summary>
    /// The paths the folder list has registered - one per list, as a rule -
    /// for a watcher's thread to look through when a folder is renamed or
    /// moved away: the list keeps nothing above its own folder, so a folder
    /// above it going is told to it by walking these, not every path
    /// registered.  Replaced whole, never changed in place.
    /// </summary>
    public string[] ListKeys => _listKeys;

    /// <summary>
    /// Whether anyone is registered for <paramref name="path"/>, and the path
    /// as it was registered.  Any thread, no lock, no allocation.
    /// </summary>
    public bool TryFind(ReadOnlySpan<char> path, [NotNullWhen(true)] out string? key) =>
        _lookup.TryGetValue(path, out key, out _);

    /// <summary>Who is registered for <paramref name="key"/>; any thread.</summary>
    public bool TryGet(string key, [NotNullWhen(true)] out Interest? interest) =>
        _paths.TryGetValue(key, out interest);

    /// <summary>
    /// A path's registrations went from one root to another - a volume taken
    /// out and put back at its letter - and how many of them, nested and
    /// other, so the roots' counts can go with them: none when nothing moved.
    /// </summary>
    internal readonly record struct RootMove(WatchRoot? From, WatchRoot? To, int Nested, int Other);

    /// <summary>
    /// Registers <paramref name="target"/> for <paramref name="key"/>, which is
    /// under <paramref name="root"/> (null for a path no volume holds); false
    /// when it already was.  A path first registered under a root since
    /// dropped moves to the new one - even when this target already was
    /// registered for it - and <paramref name="moved"/> says how many
    /// registrations it took along, not counting this one.
    /// </summary>
    public bool Add(ChangeConsumer consumer, string key, object target, WatchRoot? root, out RootMove moved)
    {
        moved = default;
        lock (_gate)
        {
            if (!_paths.TryGetValue(key, out var interest))
            {
                interest = new Interest { Root = root };
                interest[consumer] = target;
                _paths[key] = interest;
                root?.AddInterest(key);
                if (consumer == ChangeConsumer.List)
                {
                    _listKeys = [.. _listKeys, key];
                }
            }
            else
            {
                var slot = interest[consumer];
                var known = Contains(slot, target);

                // A drive taken out and put back at the same letter is a new
                // root, and the dropped one's watch hears nothing any more: a
                // path still under the dropped one moves, whoever registers
                // it again.  Otherwise the root a path was first registered
                // under is the one it stays under while this target is known.
                if (!ReferenceEquals(interest.Root, root) && (!known || interest.Root is { IsDropped: true }))
                {
                    moved = new RootMove(
                        interest.Root,
                        root,
                        Items(interest.Nested).Count,
                        Items(interest.List).Count + Items(interest.Graph).Count);
                    interest.Root?.RemoveInterest(key);
                    interest.Root = root;
                    root?.AddInterest(key);
                }

                if (known)
                {
                    return false;
                }

                interest[consumer] = slot switch
                {
                    null => target,
                    TargetList list => new TargetList([.. list.Items, target]),
                    _ => new TargetList([slot, target])
                };
                if (consumer == ChangeConsumer.List && slot is null)
                {
                    _listKeys = [.. _listKeys, key];
                }
            }

            if (consumer == ChangeConsumer.Nested)
            {
                _nestedTargets++;
            }

            return true;
        }
    }

    /// <summary>Takes <paramref name="target"/>'s registration away; false when it had none.  Says which root the path was under.</summary>
    public bool Remove(ChangeConsumer consumer, string key, object target, out WatchRoot? root)
    {
        lock (_gate)
        {
            root = null;
            if (!_paths.TryGetValue(key, out var interest) || !Contains(interest[consumer], target))
            {
                return false;
            }

            root = interest.Root;
            interest[consumer] = Without(interest[consumer], target);
            if (consumer == ChangeConsumer.Nested)
            {
                _nestedTargets--;
            }
            else if (consumer == ChangeConsumer.List && interest.List is null)
            {
                _listKeys = [.. _listKeys.Where(listed => !listed.Equals(key, StringComparison.OrdinalIgnoreCase))];
            }

            Forget(key, interest);
            return true;
        }
    }

    /// <summary>
    /// Takes away every nested target of <paramref name="key"/> that
    /// <paramref name="isGone"/> says has gone, and returns how many, with the
    /// root the path was under.
    /// </summary>
    public int RemoveGone(string key, Func<object, bool> isGone, out WatchRoot? root)
    {
        root = null;
        if (!_paths.TryGetValue(key, out var seen) || seen.Nested is not { } nested || !AnyGone(nested, isGone))
        {
            return 0;
        }

        lock (_gate)
        {
            if (!_paths.TryGetValue(key, out var interest) || interest.Nested is not { } slot)
            {
                return 0;
            }

            var removed = 0;
            var kept = slot;
            foreach (var target in Items(slot))
            {
                if (isGone(target))
                {
                    kept = Without(kept, target);
                    removed++;
                }
            }

            root = interest.Root;
            interest.Nested = kept;
            _nestedTargets -= removed;
            Forget(key, interest);
            return removed;
        }
    }

    /// <summary>Every registered path; safe to walk while others register and unregister.</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var pair in _paths)
            {
                yield return pair.Key;
            }
        }
    }

    /// <summary>The targets in a slot, one or several, to walk with foreach without allocating.</summary>
    public static Targets Items(object? slot) => new(slot);

    /// <summary>A slot's targets: nothing, one target, or a <see cref="TargetList"/>'s.</summary>
    internal readonly struct Targets(object? slot)
    {
        public int Count => slot switch
        {
            null => 0,
            TargetList list => list.Items.Length,
            _ => 1
        };

        public object this[int index] => slot is TargetList list ? list.Items[index] : slot!;

        public Enumerator GetEnumerator() => new(this);

        internal struct Enumerator(Targets targets)
        {
            private int _index = -1;

            public readonly object Current => targets[_index];

            public bool MoveNext() => ++_index < targets.Count;
        }
    }

    /// <summary>Drops a path whose last target went, from here and from its root's lookup.  Under the gate.</summary>
    private void Forget(string key, Interest interest)
    {
        if (interest.IsEmpty && _paths.TryRemove(new KeyValuePair<string, Interest>(key, interest)))
        {
            interest.Root?.RemoveInterest(key);
        }
    }

    private static bool AnyGone(object slot, Func<object, bool> isGone)
    {
        foreach (var target in Items(slot))
        {
            if (isGone(target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(object? slot, object target)
    {
        foreach (var item in Items(slot))
        {
            if (ReferenceEquals(item, target))
            {
                return true;
            }
        }

        return false;
    }

    private static object? Without(object? slot, object target)
    {
        switch (slot)
        {
            case null:
                return null;
            case TargetList list:
                var kept = list.Items.Where(item => !ReferenceEquals(item, target)).ToArray();
                return kept.Length switch
                {
                    0 => null,
                    1 => kept[0],
                    _ => new TargetList(kept)
                };
            default:
                return ReferenceEquals(slot, target) ? null : slot;
        }
    }
}

/// <summary>
/// Compares paths as Windows does - ordinally, ignoring case - but hashes only
/// a path's length and its last <see cref="TailLength"/> characters, for a
/// string and for a span alike, so a watcher can look a span of its buffer up
/// without making a string.  Paths on one volume share long beginnings -
/// C:\Users\User\AppData\Local\… - and differ at their ends, so the tail tells
/// them apart as well as the whole would, and a lookup costs the same however
/// deep the folder.  Two paths equal ignoring case have the same length and
/// equal tails, so they hash the same.
/// </summary>
internal sealed class PathComparer : IEqualityComparer<string>, IAlternateEqualityComparer<ReadOnlySpan<char>, string>
{
    public const int TailLength = 32;

    public static readonly PathComparer Instance = new();

    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string path) => Hash(path);

    public bool Equals(ReadOnlySpan<char> alternate, string other) => alternate.Equals(other, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(ReadOnlySpan<char> alternate) => Hash(alternate);

    public string Create(ReadOnlySpan<char> alternate) => alternate.ToString();

    public static int Hash(ReadOnlySpan<char> path) =>
        HashCode.Combine(path.Length, string.GetHashCode(path.Length > TailLength ? path[^TailLength..] : path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A registered folder's place under its root's watched directory, as the
/// records spell it - Users\User\Documents, or empty for the directory
/// itself - without a string of its own: for a folder spelt with the root's
/// own spelling or a mapped letter it is the end of the registered path,
/// <see cref="Text"/> from <see cref="Start"/>.  Only a <c>subst</c> letter's
/// folders, whose place does not end their path, get a string made.
/// </summary>
internal readonly struct InnerPath(string text, int start)
{
    public string Text { get; } = text;

    public int Start { get; } = start;

    public ReadOnlySpan<char> Span => Text.AsSpan(Start);

    public override string ToString() => Span.ToString();

    /// <summary>Compares places ignoring case, hashed like <see cref="PathComparer"/>, and looks one up by a span of a record.</summary>
    internal sealed class Comparer : IEqualityComparer<InnerPath>, IAlternateEqualityComparer<ReadOnlySpan<char>, InnerPath>
    {
        public static readonly Comparer Instance = new();

        public bool Equals(InnerPath x, InnerPath y) => x.Span.Equals(y.Span, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(InnerPath path) => PathComparer.Hash(path.Span);

        public bool Equals(ReadOnlySpan<char> alternate, InnerPath other) => alternate.Equals(other.Span, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOnlySpan<char> alternate) => PathComparer.Hash(alternate);

        public InnerPath Create(ReadOnlySpan<char> alternate) => new(alternate.ToString(), 0);
    }
}
