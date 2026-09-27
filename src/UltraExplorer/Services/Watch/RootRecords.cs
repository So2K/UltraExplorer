using System.Diagnostics.CodeAnalysis;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// Turns one root's records into pending changes, on the watcher's thread:
/// the folder each record happened in is looked up, as the record names it -
/// a span of the watcher's buffer, relative to the watched directory - among
/// the folders registered under the root (<see cref="WatchRoot.TryFindInterest"/>).
/// A miss - nearly every record of a whole volume's noise: temp files, caches,
/// the Prefetch folder - is dropped there and then, having cost a split at
/// the last separator and one hash of the folder's tail; no path is built,
/// however many spellings the root has.  A hit is merged into the pending
/// change of every registered spelling of that folder.
///
/// <para>A record whose own path is registered - a folder removed or renamed
/// away - also marks that folder <see cref="ChangeKinds.Gone"/>, and a folder
/// renamed away marks the folder list's folder too when it is inside
/// (<see cref="ChangeKinds.AncestorGone"/>).  A rename's
/// two halves in the same folder become one <see cref="RenamePair"/>; across
/// folders they are a removal from one and an addition to the other.  A folder
/// spelt with a short 8.3 name (PROGRA~1), which Windows sometimes reports
/// changes under, is looked up again under its long name.</para>
///
/// <para>One buffer of records at a time - a root has one watcher, and a
/// watcher parses one buffer at a time - so the scratch buffers and the
/// half-seen rename need no lock.  Nothing here allocates on a miss.</para>
/// </summary>
internal sealed unsafe class RootRecords(ChangeHub hub, WatchRoot root) : IChangeRecordSink
{
    private char[] _path = new char[512];
    private char[] _longPath = new char[512];
    private char[] _oldParent = new char[256];
    private char[] _oldLeaf = new char[128];
    private int _oldParentLength;
    private int _oldLeafLength;
    private bool _haveOld;

    /// <summary>Records seen, and of those how many were for a registered folder: for tests and the bench.</summary>
    public long Records;

    public long Hits;

    /// <summary>Records whose folder was found only under its long name.</summary>
    public long LongNameHits;

    public void Record(in ChangeRecord record)
    {
        Records++;
        var name = record.Name;
        var slash = name.LastIndexOf('\\');
        var parent = slash < 0 ? ReadOnlySpan<char>.Empty : name[..slash];
        var leaf = name[(slash + 1)..];
        var action = record.Action;
        var kinds = action == WatchNative.ActionModified
            ? record.IsDirectory ? ChangeKinds.DirDate : ChangeKinds.Content
            : ChangeKinds.Structural;

        // The second half of a rename within one folder names the first.
        var renamedFrom = ReadOnlySpan<char>.Empty;
        if (action == WatchNative.ActionRenamedNew && _haveOld
            && parent.Equals(_oldParent.AsSpan(0, _oldParentLength), StringComparison.OrdinalIgnoreCase))
        {
            renamedFrom = _oldLeaf.AsSpan(0, _oldLeafLength);
        }

        if (action == WatchNative.ActionRenamedOld)
        {
            Remember(parent, leaf);
        }
        else
        {
            _haveOld = false;
        }

        var hit = false;
        if (root.TryFindInterest(parent, out var keys) || TryLongName(parent, out keys))
        {
            if (keys is string key)
            {
                hub.Note(key, kinds, root, in record, leaf, renamedFrom);
            }
            else
            {
                foreach (var spelling in (string[])keys)
                {
                    hub.Note(spelling, kinds, root, in record, leaf, renamedFrom);
                }
            }

            hit = true;
        }

        // A folder removed or renamed away, told by its own path.  Without
        // details any entry might be a folder, so each is looked up.
        if ((action == WatchNative.ActionRemoved || action == WatchNative.ActionRenamedOld)
            && (!record.HasDetails || record.IsDirectory)
            && root.TryFindInterest(name, out var own))
        {
            if (own is string key)
            {
                hub.Note(key, ChangeKinds.Gone, root, in record, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            }
            else
            {
                foreach (var spelling in (string[])own)
                {
                    hub.Note(spelling, ChangeKinds.Gone, root, in record, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
                }
            }

            hit = true;
        }

        // A folder renamed or moved away takes everything inside it along,
        // and the watch tells only the folder's own path.  The folder list
        // keeps nothing above the folder it shows, so a list showing one
        // somewhere inside is told its folder went too; the nested tree and
        // the tree canvas keep the folders above theirs, and hear of the one
        // that went through their own registrations of it.  A handful of
        // list paths are looked through, never every path registered.
        if (action == WatchNative.ActionRenamedOld && (!record.HasDetails || record.IsDirectory))
        {
            foreach (var listed in hub.Registry.ListKeys)
            {
                if (root.IsInside(listed, name))
                {
                    hub.Note(listed, ChangeKinds.Gone | ChangeKinds.AncestorGone, root, in record, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
                    hit = true;
                }
            }
        }

        if (hit)
        {
            Hits++;
        }
    }

    public void EndOfBuffer() => _haveOld = false;

    public void Overflowed(DirectoryChangeWatcher watcher) => hub.OnOverflow(root, watcher);

    public void Failed(DirectoryChangeWatcher watcher, int error) => hub.OnWatchFailed(root, watcher, error);

    /// <summary>Keeps a rename's first half until its second arrives - usually the very next record.</summary>
    private void Remember(ReadOnlySpan<char> parent, ReadOnlySpan<char> leaf)
    {
        if (_oldParent.Length < parent.Length)
        {
            _oldParent = new char[Math.Max(parent.Length, _oldParent.Length * 2)];
        }

        if (_oldLeaf.Length < leaf.Length)
        {
            _oldLeaf = new char[Math.Max(leaf.Length, _oldLeaf.Length * 2)];
        }

        parent.CopyTo(_oldParent);
        leaf.CopyTo(_oldLeaf);
        _oldParentLength = parent.Length;
        _oldLeafLength = leaf.Length;
        _haveOld = true;
    }

    /// <summary>
    /// Looks <paramref name="inner"/> - a folder relative to the watched
    /// directory - up again under its long name, when some part of it looks
    /// like a short 8.3 name: a tilde and a digit in a part of at most twelve
    /// characters.  Asks the file system, so only then.
    /// </summary>
    private bool TryLongName(ReadOnlySpan<char> inner, [NotNullWhen(true)] out object? keys)
    {
        keys = null;
        if (!HasShortName(inner) || !root.Aliases[0].Compose(inner, ref _path, out var length))
        {
            return false;
        }

        _path[length] = '\0';
        uint found;
        fixed (char* shortPath = _path)
        fixed (char* longPath = _longPath)
        {
            found = WatchNative.GetLongPathNameW(shortPath, longPath, (uint)_longPath.Length);
        }

        if (found >= _longPath.Length)
        {
            _longPath = new char[found + 1];
            fixed (char* shortPath = _path)
            fixed (char* longPath = _longPath)
            {
                found = WatchNative.GetLongPathNameW(shortPath, longPath, (uint)_longPath.Length);
            }
        }

        var watched = root.WatchedPath;
        if (found == 0 || found >= _longPath.Length || found <= watched.Length)
        {
            return false;
        }

        var longName = _longPath.AsSpan(0, (int)found);
        if (!longName.StartsWith(watched, StringComparison.OrdinalIgnoreCase)
            || !root.TryFindInterest(longName[watched.Length..].TrimEnd('\\'), out keys))
        {
            return false;
        }

        LongNameHits++;
        return true;
    }

    /// <summary>Whether some part of <paramref name="path"/> looks like a short 8.3 name: NAME~1, PROGRA~2.TXT.</summary>
    internal static bool HasShortName(ReadOnlySpan<char> path)
    {
        var tilde = path.IndexOf('~');
        while (tilde >= 0)
        {
            if (tilde + 1 < path.Length && char.IsAsciiDigit(path[tilde + 1]))
            {
                var start = path[..tilde].LastIndexOf('\\') + 1;
                var end = path[tilde..].IndexOf('\\');
                var partLength = (end < 0 ? path.Length : tilde + end) - start;
                if (partLength <= 12)
                {
                    return true;
                }
            }

            var next = path[(tilde + 1)..].IndexOf('~');
            tilde = next < 0 ? -1 : tilde + 1 + next;
        }

        return false;
    }
}
