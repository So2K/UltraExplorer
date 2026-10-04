using System.Globalization;
using System.IO.Enumeration;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// Reads one directory in a single pass over its raw entries: the sub-folders,
/// and the files - named up to <see cref="NestedTree.MaximumFiles"/>, counted
/// past it without a string ever being made for them.
/// </summary>
public static class NestedDirectoryReader
{
    private sealed class Counts
    {
        public int Files;
        public int HiddenFiles;
    }

    /// <summary>
    /// The lists a read gathers its entries in, one pair per reading thread,
    /// emptied after every read and filled again by the next: the listing
    /// gets exact copies.  Made new for every read, a folder of thirty
    /// thousand files grew its list by doubling through arrays of a megabyte
    /// and more, every one of them garbage on the large object heap as soon
    /// as the read was done - and a big folder that keeps changing is read
    /// again every time it does.  What a thread keeps is the room its largest
    /// read needed, and goes with the thread.
    /// </summary>
    [ThreadStatic]
    private static List<NestedEntry>? t_folders;

    [ThreadStatic]
    private static List<NestedFile>? t_files;

    public static NestedListing Read(string path, CancellationToken cancellationToken)
    {
        var folders = t_folders ??= [];
        var listed = t_files ??= [];
        try
        {
            return Read(path, folders, listed, cancellationToken);
        }
        finally
        {
            // Emptied whatever happened, so no name outlives its read here.
            folders.Clear();
            listed.Clear();
        }
    }

    private static NestedListing Read(string path, List<NestedEntry> folders, List<NestedFile> listed, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var counts = new Counts();
        var truncated = false;

        try
        {
            // The date is in the same buffer as the name and attributes: taking
            // it costs no further call to the file system.
            var entries = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length, long ModifiedTicks, bool IsDirectory, bool IsLink)>(
                ExtendedLength(path),
                static (ref FileSystemEntry entry) => (
                    entry.FileName.ToString(),
                    entry.Attributes,
                    entry.IsDirectory ? 0 : entry.Length,
                    WriteTicks(ref entry),
                    entry.IsDirectory,
                    entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(ref entry)),
                options)
            {
                // Files are counted here, before the transform, so the ones
                // past the cap are never turned into anything.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory)
                    {
                        return true;
                    }

                    counts.Files++;
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        counts.HiddenFiles++;
                    }

                    return counts.Files <= NestedTree.MaximumFiles;
                }
            };

            foreach (var (name, attributes, length, modifiedTicks, isDirectory, isLink) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (!isDirectory)
                {
                    listed.Add(new NestedFile(name, isHidden, length, modifiedTicks));
                    continue;
                }

                if (folders.Count >= NestedTree.MaximumChildren)
                {
                    truncated = true;
                    continue;
                }

                folders.Add(new NestedEntry(name, isHidden, isLink, modifiedTicks));
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
            // A share that did not answer or a drive that was not ready may
            // well answer later; the canvas tries such a folder again.
            return NestedListing.Failed(ex.Message) with { IsRetryable = true };
        }

        // Culture order as Explorer shows it, and ordinal order between names
        // culture order calls equal, so two folders differing only in case (a
        // WSL tree can have them) always come out the same way round.  The
        // culture's rules are taken once: StringComparer.CurrentCultureIgnoreCase
        // makes a new comparer every time it is asked for, and asked inside the
        // comparison that was one per comparison - a read of thirty thousand
        // files threw away megabytes of them.
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        folders.Sort((left, right) =>
        {
            var order = culture.Compare(left.Name, right.Name, CompareOptions.IgnoreCase);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        listed.Sort((left, right) =>
        {
            var order = culture.Compare(left.Name, right.Name, CompareOptions.IgnoreCase);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        return new NestedListing(folders.ToArray(), counts.Files, counts.HiddenFiles, truncated) { Files = listed.ToArray() };
    }

    /// <summary>
    /// The directory's own last-write time, as UTC ticks, or zero when it
    /// cannot be had: one attribute query, no listing.  Taken just before a
    /// folder on a polled volume or a share is read, so polling can tell a
    /// directory that changed since from one that did not without reading it.
    /// Before rather than after the read, so a change made while the read runs
    /// leaves the time newer than the one kept, and is read again.
    /// </summary>
    public static long DirectoryWriteTicks(string path)
    {
        try
        {
            var time = Directory.GetLastWriteTimeUtc(ExtendedLength(path));
            return time.Year <= 1601 ? 0 : time.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// An entry's last-write time as UTC ticks, or zero when it has none worth
    /// the name: a time of zero - which would read as 1601 - or one past what
    /// a date can hold, which a tool or a damaged volume can leave on a file
    /// and which would otherwise throw and fail the whole folder's read, every
    /// time it was read.  The same as <see cref="DirectoryWriteTicks"/> says.
    /// </summary>
    private static long WriteTicks(ref FileSystemEntry entry)
    {
        try
        {
            var time = entry.LastWriteTimeUtc.UtcDateTime;
            return time.Year <= 1601 ? 0 : time.Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether a folder with a reparse point is a link - a junction, a symbolic
    /// link, a mount point - rather than a cloud placeholder (OneDrive files on
    /// demand) or a projected folder, which carry the same attribute but are
    /// ordinary folders to read.  Only links report a target.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry)
    {
        try
        {
            return entry.ToFileSystemInfo().LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The path in its extended-length form for a local drive or a share
    /// (<c>\\server\share\...</c> as <c>\\?\UNC\server\share\...</c>, a WSL
    /// distribution's included).  Without it the enumerator normalises the
    /// path, which strips a trailing dot or space - and a folder named
    /// "backup." would be read as its neighbour "backup".  A path in either
    /// form already, or a device's, is left as it is.
    /// </summary>
    private static string ExtendedLength(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && path[2] == Path.DirectorySeparatorChar)
        {
            return @"\\?\" + path;
        }

        if (path.Length > 2 && path[0] == Path.DirectorySeparatorChar && path[1] == Path.DirectorySeparatorChar
            && !(path.Length > 3 && path[2] is ('?' or '.') && path[3] == Path.DirectorySeparatorChar))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path;
    }
}
