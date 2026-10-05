using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed partial class NestedTree
{
    private readonly ViewAllFileSystemService _pathFileSystem = new();

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
                    var children = current.AllChildren.Append(child).ToArray();
                    Array.Sort(children, static (left, right) =>
                    {
                        var order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                        return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
                    });
                    current.AllChildren = children;
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
}
