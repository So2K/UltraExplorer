using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// A directory tree held in memory, for driving <see cref="NestedTree"/> without
/// a disk: folders, files, hidden and link flags, folders that cannot be read,
/// and a count of how often anything was read.  Its <see cref="Read"/> is what
/// the tree's constructor takes in place of the real reader.
/// </summary>
internal sealed class FakeDisk
{
    private readonly Dictionary<string, FakeDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _reads;

    /// <summary>Reads so far, including failed ones.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Answers a read before the table does; returning null falls through to the table.</summary>
    public Func<string, CancellationToken, NestedListing?>? Hook { get; set; }

    /// <summary>The folder at <paramref name="path"/>, created with every folder above it.</summary>
    public FakeDirectory Folder(string path)
    {
        lock (_gate)
        {
            if (_directories.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var created = new FakeDirectory(path);
            _directories[path] = created;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Folder(parent).Children.Add(Path.GetFileName(path));
            }

            return created;
        }
    }

    /// <param name="modified">When the file was last written; none leaves the date at zero, as a reader that does not say.</param>
    public void AddFile(string folder, string name, long length = 0, bool hidden = false, DateTime? modified = null)
    {
        lock (_gate)
        {
            Folder(folder).Files.Add(new NestedFile(name, hidden, length, TicksOf(modified)));
        }
    }

    public void AddFiles(string folder, int count, string stem)
    {
        for (var index = 0; index < count; index++)
        {
            AddFile(folder, $"{stem}{index:D3}.txt", 100 + index);
        }
    }

    /// <summary>Deletes a folder and everything under it, as if from another program.</summary>
    public void Remove(string path)
    {
        lock (_gate)
        {
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (parent is not null && _directories.TryGetValue(parent, out var holder))
            {
                holder.Children.RemoveAll(child => string.Equals(child, name, StringComparison.OrdinalIgnoreCase));
            }

            var prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var key in _directories.Keys
                         .Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase)
                                       || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _directories.Remove(key);
            }
        }
    }

    public NestedListing Read(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        if (Hook?.Invoke(path, cancellationToken) is { } answered)
        {
            return answered;
        }

        lock (_gate)
        {
            if (!_directories.TryGetValue(path, out var directory))
            {
                return NestedListing.Failed("No longer exists");
            }

            if (directory.Error is { } error)
            {
                return NestedListing.Failed(error);
            }

            // Sorted the way the real reader sorts, which is what lets the
            // canvas find a file by binary search.
            var folders = directory.Children
                .Select(name => _directories[Path.Combine(path, name)])
                .Select(child => new NestedEntry(child.Name, child.IsHidden, child.IsReparsePoint, TicksOf(child.Modified)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var files = directory.Files
                .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new NestedListing(
                folders,
                files.Count + directory.UnlistedFiles,
                files.Count(file => file.IsHidden),
                directory.IsTruncated)
            {
                Files = files
            };
        }
    }

    /// <summary>
    /// A date as the real reader reports it: UTC ticks, and zero for none.  A
    /// date given in local time is turned round to UTC first; one of no stated
    /// kind is taken to be UTC already, which is what the tests write.
    /// </summary>
    internal static long TicksOf(DateTime? modified) => modified switch
    {
        null => 0,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime().Ticks,
        { } date => date.Ticks
    };
}

internal sealed class FakeDirectory(string path)
{
    public string FullPath { get; } = path;

    public string Name => Path.GetFileName(FullPath) is { Length: > 0 } name ? name : FullPath;

    public bool IsHidden { get; set; }

    public bool IsReparsePoint { get; set; }

    /// <summary>Set to make every read of this folder fail with this message.</summary>
    public string? Error { get; set; }

    public bool IsTruncated { get; set; }

    /// <summary>Files counted in the listing but not named in it.</summary>
    public int UnlistedFiles { get; set; }

    /// <summary>When the folder was last written, as its parent's listing reports it; none is a date of zero.</summary>
    public DateTime? Modified { get; set; }

    public List<string> Children { get; } = [];

    public List<NestedFile> Files { get; } = [];
}
