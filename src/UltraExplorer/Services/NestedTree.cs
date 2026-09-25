using System.IO.Enumeration;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>What one read of one directory found.</summary>
public sealed record NestedListing(
    IReadOnlyList<NestedEntry> Folders,
    int FileCount,
    int HiddenFileCount,
    bool IsTruncated,
    string ErrorMessage = "")
{
    /// <summary>The files by name, up to <see cref="NestedTree.MaximumFiles"/>; the counts cover all of them.</summary>
    public IReadOnlyList<NestedFile> Files { get; init; } = [];

    public static NestedListing Failed(string message) => new([], 0, 0, false, message);
}

public readonly record struct NestedEntry(string Name, bool IsHidden, bool IsReparsePoint);

/// <summary>A drive or extra root, as the canvas should name it.</summary>
public sealed record NestedRoot(string FullPath, string Name, NestedFolderKind Kind, string SecondaryText = "");

/// <summary>
/// The folder tree behind the nested canvas.
///
/// Nothing is read up front.  The canvas asks for a folder the first time it is
/// drawn large enough for its contents to matter, and the reads that are still
/// wanted run a few at a time in the background, biggest on screen first.  A
/// request that stops being wanted - the view moved on before its turn came -
/// is dropped rather than read.  Because a folder's place depends only on its
/// parent's listing, this is invisible from the outside: every folder is
/// always "on the canvas", some are just too small to have been looked at yet.
///
/// The tree belongs to the thread that created it.  Only the directory reads
/// leave it; their results come back through the synchronisation context and
/// are applied there.
/// </summary>
public sealed class NestedTree : IDisposable
{
    /// <summary>Sub-folders held for one folder.  WinSxS has tens of thousands; this holds all of them.</summary>
    public const int MaximumChildren = 100_000;

    /// <summary>Files listed by name for one folder; past this they are only counted.</summary>
    public const int MaximumFiles = 50_000;

    /// <summary>Reads in flight at once.  More than this only makes a spinning disk seek.</summary>
    public const int MaximumConcurrentReads = 3;

    private readonly Dictionary<string, NestedFolder> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userHidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forcedVisible = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NestedFolder> _queue = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, CancellationToken, NestedListing> _reader;
    private int _running;
    private bool _includeHidden;

    public NestedTree(Func<string, CancellationToken, NestedListing>? reader = null)
    {
        _reader = reader ?? NestedDirectoryReader.Read;
        Root = new NestedFolder(string.Empty, "This PC", NestedFolderKind.Computer, null)
        {
            LoadState = NestedLoadState.Loaded
        };
    }

    /// <summary>The cell every drive is inside.</summary>
    public NestedFolder Root { get; }

    /// <summary>
    /// Advanced by the canvas once per picture.  A queued read whose folder was
    /// not asked for again in the last two pictures is no longer on screen.
    /// </summary>
    public long Frame { get; private set; }

    /// <summary>Folders loaded so far.</summary>
    public int LoadedCount => _byPath.Count;

    /// <summary>Reads waiting or running.</summary>
    public int PendingCount => _queue.Count + _running;

    /// <summary>Raised on the owning thread whenever the shape of the tree changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a folder's listing was applied, including a refresh.</summary>
    public event Action<NestedFolder>? FolderLoaded;

    public bool IncludeHidden
    {
        get => _includeHidden;
        set
        {
            if (_includeHidden == value)
            {
                return;
            }

            _includeHidden = value;
            RefilterAll();
        }
    }

    /// <summary>
    /// Whether drawing a folder may start reading it.  Off only where the tree
    /// is filled explicitly - a test rendering a canvas on a thread with no
    /// dispatcher to bring the results back to.
    /// </summary>
    public bool IsReadingOnDemand { get; set; } = true;

    public void BeginFrame() => Frame++;

    /// <summary>Replaces the drives and extra roots.  Folders already read under a surviving root are kept.</summary>
    public void SetRoots(IReadOnlyList<NestedRoot> roots)
    {
        var existing = Root.AllChildren.ToDictionary(child => child.FullPath, StringComparer.OrdinalIgnoreCase);
        var children = new List<NestedFolder>(roots.Count);
        foreach (var root in roots)
        {
            if (existing.Remove(root.FullPath, out var kept) && kept.Name == root.Name && kept.Kind == root.Kind)
            {
                kept.SecondaryText = root.SecondaryText;
                children.Add(kept);
                continue;
            }

            if (kept is not null)
            {
                Forget(kept);
            }

            var folder = new NestedFolder(root.FullPath, root.Name, root.Kind, Root, secondaryText: root.SecondaryText);
            _byPath[folder.FullPath] = folder;
            children.Add(folder);
        }

        foreach (var removed in existing.Values)
        {
            Forget(removed);
        }

        Root.AllChildren = [.. children];
        ApplyVisibleChildren(Root);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The folder with this path, if it has been read into the tree.</summary>
    public NestedFolder? Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return _byPath.TryGetValue(Key(path), out var folder) ? folder : null;
    }

    /// <summary>
    /// The deepest folder already in the tree on the way to <paramref name="path"/>:
    /// the folder itself once everything above it has been read, otherwise the
    /// ancestor the reading has got to.
    /// </summary>
    public NestedFolder? FindNearest(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var chain = Chain(path);
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            if (_byPath.TryGetValue(chain[index], out var folder) && IsOnCanvas(folder))
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>Whether the folder is still one of the cells, rather than filtered out or forgotten.</summary>
    public static bool IsOnCanvas(NestedFolder folder)
    {
        for (var current = folder; current.Parent is not null; current = current.Parent)
        {
            if (current.Index < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Asks for a folder to be read because the canvas is drawing it.  Cheap to
    /// call every frame for every folder on screen: a folder already read,
    /// reading or queued only has its priority refreshed.
    /// </summary>
    public void Request(NestedFolder folder, double priority)
    {
        if (!folder.CanLoad || !IsReadingOnDemand)
        {
            return;
        }

        folder.RequestedFrame = Frame;
        folder.Priority = priority;
        if (folder.LoadState != NestedLoadState.NotLoaded)
        {
            return;
        }

        folder.LoadState = NestedLoadState.Queued;
        _queue.Add(folder);
        Pump();
    }

    /// <summary>
    /// Reads every folder on the way to <paramref name="path"/> and returns the
    /// folder itself, or the deepest one that could be reached.  A folder asked
    /// for by name is shown even if it is hidden: naming it is the request.
    /// </summary>
    public async Task<NestedFolder?> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var chain = Chain(path);
        ForceVisible(chain);

        NestedFolder? current = null;
        foreach (var step in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is not null)
            {
                await LoadAsync(current, cancellationToken);
            }

            if (!_byPath.TryGetValue(step, out var next) || !IsOnCanvas(next))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Reads one folder now, unless it already has been.</summary>
    public async Task LoadAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        // Asked for by name, a junction is read like any folder: it is only
        // the canvas wandering into one on its own that is refused.
        if (folder.IsComputer || folder.IsForgotten)
        {
            return;
        }

        while (folder.LoadState is NestedLoadState.Loading)
        {
            // Somebody else's read is in flight; its result is ours too.
            await Task.Delay(10, cancellationToken);
        }

        if (folder.LoadState is NestedLoadState.Loaded or NestedLoadState.Failed)
        {
            return;
        }

        _queue.Remove(folder);
        folder.LoadState = NestedLoadState.Loading;
        var listing = await ReadAsync(folder.FullPath, cancellationToken);
        Apply(folder, listing);
    }

    /// <summary>
    /// Reads a folder again and keeps every child that is still there - with
    /// everything already read below it - so a refresh moves nothing that did
    /// not change.
    /// </summary>
    public async Task RefreshAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        if (folder.IsComputer || folder.IsForgotten || folder.LoadState is NestedLoadState.NotLoaded or NestedLoadState.Queued)
        {
            return;
        }

        while (folder.LoadState is NestedLoadState.Loading)
        {
            await Task.Delay(10, cancellationToken);
        }

        folder.LoadState = NestedLoadState.Loading;
        var listing = await ReadAsync(folder.FullPath, cancellationToken);
        Apply(folder, listing);
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
        folder.LoadState = folder.IsComputer ? NestedLoadState.Loaded : NestedLoadState.NotLoaded;
        folder.ErrorMessage = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folders the user hid from the canvas, with everything under them.</summary>
    public void SetUserHidden(IEnumerable<string> paths)
    {
        _userHidden.Clear();
        foreach (var path in paths)
        {
            _userHidden.Add(Key(path));
        }

        RefilterAll();
    }

    public bool IsUserHidden(string path) => _userHidden.Contains(Key(path));

    /// <summary>
    /// Keeps every folder on the way to these paths on the canvas even when it
    /// is hidden - a marked folder inside AppData still needs a place to be.
    /// </summary>
    public void ForceVisible(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            foreach (var step in Chain(path))
            {
                changed |= _forcedVisible.Add(step);
            }
        }

        if (changed)
        {
            RefilterAll();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- reading -----------------------------------------------------------

    private void Pump()
    {
        while (_running < MaximumConcurrentReads && _queue.Count > 0)
        {
            NestedFolder? best = null;
            for (var index = _queue.Count - 1; index >= 0; index--)
            {
                var candidate = _queue[index];
                if (candidate.LoadState != NestedLoadState.Queued)
                {
                    _queue.RemoveAt(index);
                    continue;
                }

                // Two pictures without being asked for: it has left the screen.
                if (!candidate.IsSticky && candidate.RequestedFrame < Frame - 2)
                {
                    candidate.LoadState = NestedLoadState.NotLoaded;
                    _queue.RemoveAt(index);
                    continue;
                }

                if (best is null
                    || candidate.IsSticky && !best.IsSticky
                    || candidate.IsSticky == best.IsSticky && candidate.Priority > best.Priority)
                {
                    best = candidate;
                }
            }

            if (best is null)
            {
                return;
            }

            _queue.Remove(best);
            best.LoadState = NestedLoadState.Loading;
            _running++;
            _ = RunQueuedAsync(best);
        }
    }

    private async Task RunQueuedAsync(NestedFolder folder)
    {
        try
        {
            var listing = await ReadAsync(folder.FullPath, _lifetime.Token);
            Apply(folder, listing);
        }
        catch (OperationCanceledException)
        {
            folder.LoadState = NestedLoadState.NotLoaded;
        }
        finally
        {
            _running--;
            if (!_lifetime.IsCancellationRequested)
            {
                Pump();
            }
        }
    }

    private async Task<NestedListing> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        return await Task.Run(() => _reader(path, token), token).ConfigureAwait(true);
    }

    private void Apply(NestedFolder folder, NestedListing listing)
    {
        folder.IsSticky = false;

        // Its parent was read again while this was in flight and it is gone:
        // its children must not be registered under a folder nobody can reach.
        if (folder.IsForgotten)
        {
            return;
        }

        if (!string.IsNullOrEmpty(listing.ErrorMessage))
        {
            folder.LoadState = NestedLoadState.Failed;
            folder.ErrorMessage = listing.ErrorMessage;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var existing = new Dictionary<string, NestedFolder>(folder.AllChildren.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var child in folder.AllChildren)
        {
            existing[child.FullPath] = child;
        }

        var children = new NestedFolder[listing.Folders.Count];
        for (var index = 0; index < children.Length; index++)
        {
            var entry = listing.Folders[index];
            var path = Path.Combine(folder.FullPath, entry.Name);
            if (existing.Remove(path, out var kept) && kept.IsReparsePoint == entry.IsReparsePoint && kept.IsHidden == entry.IsHidden)
            {
                children[index] = kept;
                continue;
            }

            if (kept is not null)
            {
                Forget(kept);
            }

            var child = new NestedFolder(path, entry.Name, NestedFolderKind.Folder, folder, entry.IsHidden, entry.IsReparsePoint);
            _byPath[path] = child;
            children[index] = child;
        }

        foreach (var removed in existing.Values)
        {
            Forget(removed);
        }

        folder.AllChildren = children;
        folder.AllFiles = [.. listing.Files];
        folder.FileCount = listing.FileCount;
        folder.HiddenFileCount = listing.HiddenFileCount;
        folder.IsTruncated = listing.IsTruncated;
        folder.ErrorMessage = string.Empty;
        folder.LoadState = NestedLoadState.Loaded;
        ApplyVisibleChildren(folder);

        FolderLoaded?.Invoke(folder);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Forget(NestedFolder folder)
    {
        folder.Index = -1;
        folder.IsForgotten = true;
        if (folder.LoadState == NestedLoadState.Queued)
        {
            folder.LoadState = NestedLoadState.NotLoaded;
        }

        if (_byPath.TryGetValue(folder.FullPath, out var registered) && ReferenceEquals(registered, folder))
        {
            _byPath.Remove(folder.FullPath);
        }

        foreach (var child in folder.AllChildren)
        {
            Forget(child);
        }
    }

    // ---- filtering and placing ---------------------------------------------

    private void RefilterAll()
    {
        var stack = new Stack<NestedFolder>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            if (folder.LoadState != NestedLoadState.Loaded && !folder.IsComputer)
            {
                continue;
            }

            ApplyVisibleChildren(folder);
            foreach (var child in folder.AllChildren)
            {
                stack.Push(child);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsVisible(NestedFolder folder)
    {
        if (_forcedVisible.Contains(folder.FullPath))
        {
            return true;
        }

        if (_userHidden.Contains(folder.FullPath))
        {
            return false;
        }

        return _includeHidden || !folder.IsHidden;
    }

    /// <summary>Decides which children are cells and where each one goes.</summary>
    private void ApplyVisibleChildren(NestedFolder folder)
    {
        var visible = new List<NestedFolder>(folder.AllChildren.Length);
        foreach (var child in folder.AllChildren)
        {
            if (IsVisible(child))
            {
                visible.Add(child);
            }
            else
            {
                child.Index = -1;
            }
        }

        NestedFile[] files = _includeHidden
            ? folder.AllFiles
            : [.. folder.AllFiles.Where(file => !file.IsHidden)];

        // Sub-folders take the top of the cell and files what is left under
        // them: the folder grid is fitted into its share first, then the files
        // get every bit of height the folders did not actually use.
        var (folderHeight, _) = NestedLayout.Split(visible.Count, files.Length);
        var grid = NestedLayout.GridFor(visible.Count, folderHeight);
        for (var index = 0; index < visible.Count; index++)
        {
            var child = visible[index];
            var (x, y) = grid.Origin(index);
            child.Index = index;
            child.OffsetX = x;
            child.OffsetY = y;
            child.Scale = grid.Scale;
        }

        var used = grid.IsEmpty ? 0 : grid.Height + NestedLayout.ZoneGap;
        folder.Grid = grid;
        folder.Children = visible;
        folder.Files = files;
        folder.FileGrid = NestedLayout.FileGridFor(
            files.Length,
            NestedLayout.HeaderHeight + used,
            NestedLayout.ContentHeight - used);
        folder.UnlistedFileCount = Math.Max(0, folder.FileCount - folder.AllFiles.Length);
    }

    // ---- paths ---------------------------------------------------------------

    /// <summary>
    /// The root-first chain of paths from the root that holds <paramref name="path"/>
    /// down to it.  A root can be a drive or a deeper extra root such as a UNC
    /// share, so the chain starts at the longest root that is a prefix.
    /// </summary>
    internal List<string> Chain(string path)
    {
        var target = Key(path);
        NestedFolder? owner = null;
        foreach (var root in Root.AllChildren)
        {
            if (IsSameOrInside(target, root.FullPath)
                && (owner is null || root.FullPath.Length > owner.FullPath.Length))
            {
                owner = root;
            }
        }

        var chain = new List<string>();
        if (owner is null)
        {
            return chain;
        }

        chain.Add(owner.FullPath);
        if (target.Length <= owner.FullPath.Length)
        {
            return chain;
        }

        var start = owner.FullPath.Length;
        var remainder = target[start..].TrimStart(Path.DirectorySeparatorChar);
        var current = owner.FullPath;
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            chain.Add(current);
        }

        return chain;
    }

    private static bool IsSameOrInside(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return root.EndsWith(Path.DirectorySeparatorChar) || path[root.Length] == Path.DirectorySeparatorChar;
    }

    private static string Key(string path)
    {
        try
        {
            return ViewAllPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>
/// Reads the sub-folders of one directory and counts its files, in one pass
/// over the raw directory entries.  File names are never turned into strings:
/// only folders become cells, and a file only needs to be counted.
/// </summary>
public static class NestedDirectoryReader
{
    public static NestedListing Read(string path, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var folders = new List<NestedEntry>();
        var listed = new List<NestedFile>();
        var files = 0;
        var hiddenFiles = 0;
        var truncated = false;

        try
        {
            var entries = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length, bool IsDirectory)>(
                path,
                static (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.Attributes, entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory),
                options);

            foreach (var (name, attributes, length, isDirectory) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (!isDirectory)
                {
                    files++;
                    if (isHidden)
                    {
                        hiddenFiles++;
                    }

                    if (listed.Count < NestedTree.MaximumFiles)
                    {
                        listed.Add(new NestedFile(name, isHidden, length));
                    }

                    continue;
                }

                if (folders.Count >= NestedTree.MaximumChildren)
                {
                    truncated = true;
                    continue;
                }

                folders.Add(new NestedEntry(name, isHidden, (attributes & FileAttributes.ReparsePoint) != 0));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return NestedListing.Failed("Access denied");
        }
        catch (DirectoryNotFoundException)
        {
            return NestedListing.Failed("No longer exists");
        }
        catch (IOException ex)
        {
            return NestedListing.Failed(ex.Message);
        }

        folders.Sort(static (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        listed.Sort(static (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return new NestedListing(folders, files, hiddenFiles, truncated) { Files = listed };
    }
}
