using System.Globalization;
using System.Runtime.CompilerServices;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed partial class NestedTree
{
    private readonly ViewAllFileSystemService _pathFileSystem = new();

    /// <summary>Owned metadata seam for checks; production describes one exact entry off the UI thread.</summary>
    internal Func<string, CancellationToken, Task<ViewAllEntryDescriptor>>? NamedFileDescribeForChecks { get; set; }

    internal Task<ViewAllEntryDescriptor> DescribeNamedEntryAsync(string path, CancellationToken cancellationToken)
        => NamedFileDescribeForChecks?.Invoke(path, cancellationToken) ?? _pathFileSystem.DescribeEntryAsync(path, cancellationToken);

    private sealed record NamedFileSlot(string Name, int ReadTicket, WeakReference<NestedFile[]> Listing);
    private readonly ConditionalWeakTable<NestedFolder, NamedFileSlot> _namedFileSlots = new();

    /// <summary>Ensures one specifically named file has a tile even outside a capped listing. No ancestor or directory enumeration is added.</summary>
    internal async Task<bool> EnsureNamedFileAsync(NestedFolder folder, string path,
        CancellationToken cancellationToken = default, Func<bool>? requestCurrent = null)
    {
        bool Current() => !_disposed && !IsDetached(folder) && !cancellationToken.IsCancellationRequested
            && requestCurrent?.Invoke() != false;
        if (!Current() || !folder.IsLoaded || !ViewAllPath.Equals(Path.GetDirectoryName(path) ?? "", folder.FullPath)) return false;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // A load of an already loaded folder does not await its refresh.
            // Let that read finish before adding a leaf it could overwrite.
            if (folder.QueuedRead != ReadKind.None || folder.NeedsRefresh)
                await RefreshAsync(folder, cancellationToken);
            if (!Current() || !folder.IsLoaded) return false;
            if (HoldsFile(folder, name))
            {
                var wasShown = FileIndexAsPlaced(folder, name) >= 0;
                // An ordinary visible F target needs no permanent visibility
                // override. Judge that independently of the Files layer: F
                // resolves the leaf before it locally turns that layer on, so
                // an ordinary file has no placed index at that moment. Hidden
                // and file-type-filtered targets still get the explicit reveal.
                var key = Key(path);
                var file = folder.AllFiles[SearchFiles(folder.AllFiles, name)];
                var needsOverride = (_fileNameFilter?.Invoke(file.Name) ?? true) == false
                    || !_includeHidden && file.IsHidden;
                if (needsOverride && !_forcedVisible.Contains(key)) ForceVisible([path]);
                if (!Current()) return false;
                if (!wasShown)
                {
                    ApplyVisibleChildren(folder);
                    VisibilityVersion++;
                    RaiseChanged();
                }
                return Current();
            }
            var ticket = folder.ReadTicket;
            var before = folder.AllFiles;
            var entry = await DescribeNamedEntryAsync(path, cancellationToken).WaitAsync(cancellationToken);
            if (!Current() || entry.Kind != ViewAllEntryKind.File || !ViewAllPath.Equals(entry.FullPath, path)) return false;
            if (ticket != folder.ReadTicket || !ReferenceEquals(before, folder.AllFiles)
                || folder.QueuedRead != ReadKind.None || folder.NeedsRefresh) continue;

            // One additional selected leaf, not an ever-growing bypass of the
            // safety cap. A fresh real listing is never stripped of an entry.
            var remove = -1;
            if (_namedFileSlots.TryGetValue(folder, out var previous)
                && (previous.ReadTicket == folder.ReadTicket
                    || previous.Listing.TryGetTarget(out var prior) && ReferenceEquals(prior, before)))
                remove = Array.FindIndex(before, file => file.Name == previous.Name);
            var required = new NestedFile(entry.DisplayName, entry.IsHidden, entry.SizeBytes ?? 0, entry.ModifiedUtc.Ticks);
            var culture = CultureInfo.CurrentCulture.CompareInfo;
            var at = 0;
            var end = before.Length;
            while (at < end)
            {
                var middle = (at + end) / 2;
                if (NameOrder(culture, before[middle].Name, required.Name) < 0) at = middle + 1;
                else end = middle;
            }
            if (remove >= 0 && remove < at) at--;
            var named = new NestedFile[before.Length + 1 - (remove >= 0 ? 1 : 0)];
            var wrote = 0;
            for (var index = 0; index < before.Length; index++)
            {
                if (index == remove) continue;
                if (wrote == at) named[wrote++] = required;
                named[wrote++] = before[index];
            }
            if (wrote < named.Length) named[wrote] = required;
            var capped = folder.FileCount > before.Length || before.Length >= MaximumFiles || remove >= 0;
            folder.AllFiles = named;
            folder.FileCount = Math.Max(folder.FileCount, named.Length);
            if (!capped && entry.IsHidden) folder.HiddenFileCount++;
            _namedFileSlots.Remove(folder);
            if (capped) _namedFileSlots.Add(folder, new(required.Name, folder.ReadTicket, new(named)));
            ForceVisible([path]);
            ApplyVisibleChildren(folder);
            VisibilityVersion++;
            RaiseChanged();
            return Current();
        }
        return false;
    }

    /// <summary>A partial ancestor is listed only when the camera enters it,
    /// rather than because it was needed to reach a deeper folder.</summary>
    internal Func<NestedFolder, bool>? PartialListingReadAllowed { get; set; }

    /// <summary>Finds a directory through its real parent chain, describing
    /// only the missing names. No parent contents are enumerated. Later normal
    /// listings reconcile these nodes and keep everything already read below.
    /// Going there is asked for by name, so a folder on the way that the user
    /// hid from the canvas is shown again for it (see <see cref="ShowOpenedByName"/>).</summary>
    public Task<NestedFolder?> MaterializePathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(path) && !_disposed)
        {
            ShowOpenedByName(path);
        }

        return MaterializeNamedPathAsync(path, allowFile: false, cancellationToken);
    }

    /// <summary>A beacon may name a file. Resolve its directory without
    /// enumerating the ancestors, just as a directory navigation does.</summary>
    internal Task<NestedFolder?> MaterializeContainerAsync(string path, CancellationToken cancellationToken = default)
        => MaterializeNamedPathAsync(path, allowFile: true, cancellationToken);

    private async Task<NestedFolder?> MaterializeNamedPathAsync(string path, bool allowFile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed) return null;
        var target = Key(path);
        ForceVisible([target]);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chain = Chain(target);
            if (chain.Count == 0) return null;
            var current = OwnerRoot(target)!;
            var next = 1;
            while (next < chain.Count && FindChild(current, Path.GetFileName(chain[next])) is { } known)
            {
                current = known;
                next++;
            }
            if (next == chain.Count) return IsOnCanvas(current) && !IsDetached(current) ? current : null;

            // Through an archive there is nothing on disk to describe: the
            // rest of the way is read, a folder at a time, as the canvas reads
            // them - the reader opens archives (see Archives.ArchiveService).
            if (Archives.ArchiveService.MayInvolveArchive(target))
            {
                while (next < chain.Count)
                {
                    if (!current.IsLoaded)
                    {
                        await LoadAsync(current, cancellationToken);
                    }

                    if (_disposed || IsDetached(current) || FindChild(current, Path.GetFileName(chain[next])) is not { } child)
                    {
                        break;
                    }

                    current = child;
                    next++;
                }

                if (next == chain.Count)
                {
                    return IsOnCanvas(current) && !IsDetached(current) ? current : null;
                }

                if (current.IsInArchive)
                {
                    return null;
                }
            }

            var steps = chain.Skip(next).ToArray();
            var described = await _pathFileSystem.DescribeChainAsync(current.FullPath, steps, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return null;
            if (IsDetached(current)) continue;
            var changed = false;
            var namedFile = false;
            foreach (var entry in described)
            {
                if (entry.Kind == ViewAllEntryKind.File)
                {
                    namedFile = ViewAllPath.Equals(entry.FullPath, target);
                    break;
                }
                if (FindChild(current, entry.DisplayName) is not { } child)
                {
                    child = NestedFolder.ChildOf(current, entry.DisplayName, entry.IsHidden,
                        entry.IsReparsePoint, entry.ModifiedUtc.Ticks);
                    current.AllChildren = WithChildInOrder(current.AllChildren, child);
                    if (!current.IsLoaded) current.HasPartialListing = true;
                    ApplyVisibleChildren(current);
                    _knownCount++;
                    changed = true;
                }
                current = child;
            }
            if (changed) RaiseChanged();
            return (ViewAllPath.Equals(current.FullPath, target) || allowFile && namedFile) && IsOnCanvas(current) ? current : null;
        }
        return null;
    }

    /// <summary>
    /// The sub-folders in <paramref name="children"/>, already in the order
    /// a reader lists them (<see cref="NameOrder"/>), with <paramref name="child"/>
    /// put in its place among them: a binary search for where it goes, not a
    /// sort of them all again - a folder named in a parent of tens of
    /// thousands of sub-folders made that sort, a comparer for every
    /// comparison, on the UI thread.
    /// </summary>
    private static NestedFolder[] WithChildInOrder(NestedFolder[] children, NestedFolder child)
    {
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        var at = 0;
        var end = children.Length;
        while (at < end)
        {
            var middle = (at + end) / 2;
            if (NameOrder(culture, children[middle].Name, child.Name) < 0)
            {
                at = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        var placed = new NestedFolder[children.Length + 1];
        Array.Copy(children, placed, at);
        placed[at] = child;
        Array.Copy(children, at, placed, at + 1, children.Length - at);
        return placed;
    }
}
