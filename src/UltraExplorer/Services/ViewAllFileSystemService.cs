using System.Globalization;
using System.Runtime.InteropServices;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// One-level-at-a-time file-system reader for View All.  It never recurses and
/// stops after the configured safety cap, which keeps opening a drive instant.
/// </summary>
public sealed class ViewAllFileSystemService
{
    /// <summary>
    /// How one drive is asked whether it is ready, and for its label, format
    /// and sizes: what its cell shows, or null for a drive that is not ready.
    /// Each of those is a question to the drive, which a drive mapped to a
    /// server that is off answers only after some twenty seconds.  A test puts
    /// a slow answer here to see that start-up does not wait for it.
    /// </summary>
    internal static Func<DriveInfo, ViewAllEntryDescriptor?> DescribeDrive { get; set; } = DescribeDriveRoot;

    /// <summary>The drives that are ready, in name order, once every drive has answered (see <see cref="AskDriveRoots"/>).</summary>
    public async Task<IReadOnlyList<ViewAllEntryDescriptor>> GetDriveRootsAsync(CancellationToken cancellationToken = default)
    {
        var answers = await Task.WhenAll(AskDriveRoots(cancellationToken).Select(drive => drive.Answer));
        return answers.OfType<ViewAllEntryDescriptor>().ToArray();
    }

    /// <summary>
    /// Asks every drive at once, each on a thread of its own, and hands the
    /// questions back in name order without waiting for any answer: one drive
    /// mapped to a server that is off takes some twenty seconds to say it is
    /// not ready, and the others need not wait for it.  Each answer is the
    /// drive's cell, by the root it has (<see cref="ViewAllPath.Normalize"/>),
    /// or null for a drive that is not ready or went while it was asked.
    /// Listing the letters asks no drive anything.
    /// </summary>
    public IReadOnlyList<(string Path, Task<ViewAllEntryDescriptor?> Answer)> AskDriveRoots(CancellationToken cancellationToken = default)
        => DriveInfo.GetDrives()
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .Select(drive => (
                ViewAllPath.Normalize(drive.RootDirectory.FullName),
                Task.Run<ViewAllEntryDescriptor?>(() => AskDrive(drive), cancellationToken)))
            .ToArray();

    private static ViewAllEntryDescriptor? AskDrive(DriveInfo drive)
    {
        try
        {
            return DescribeDrive(drive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Explorer also omits drives that disappear while refreshing.
            return null;
        }
    }

    /// <summary>A drive's cell as the disk describes it, or null when the drive is not ready.</summary>
    private static ViewAllEntryDescriptor? DescribeDriveRoot(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            return null;
        }

        var volume = string.IsNullOrWhiteSpace(drive.VolumeLabel)
            ? DriveTypeName(drive.DriveType)
            : drive.VolumeLabel;
        var driveName = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
        var secondary = $"{drive.DriveFormat}  ·  {FormatSize(drive.AvailableFreeSpace)} free of {FormatSize(drive.TotalSize)}";
        return new ViewAllEntryDescriptor(
            ViewAllPath.Normalize(drive.RootDirectory.FullName),
            $"{volume} ({driveName})",
            ViewAllEntryKind.Drive,
            IsHidden: false,
            IsReparsePoint: false,
            SizeBytes: drive.TotalSize,
            ModifiedUtc: DateTime.MinValue,
            SecondaryText: secondary);
    }

    /// <summary>
    /// Describes a single directory so it can be added as an extra root, which
    /// is how WSL and UNC shares join the graph alongside the local drives.
    /// </summary>
    public Task<ViewAllEntryDescriptor> DescribeDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = ViewAllPath.Normalize(directoryPath);
            var directory = new DirectoryInfo(ForWindows(normalized));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Folder no longer exists: {directoryPath}");
            }

            var attributes = directory.Attributes;
            return new ViewAllEntryDescriptor(
                normalized,
                string.IsNullOrEmpty(directory.Name) ? normalized : directory.Name,
                ViewAllEntryKind.Folder,
                attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System),
                IsLink(directory, attributes),
                SizeBytes: null,
                LastWriteUtc(directory),
                normalized);
        }, cancellationToken);

    /// <summary>
    /// Describes one object by name, folder or file, without regard to any of
    /// the rules that govern enumeration.  Something asked for by name is not
    /// something a display filter gets a say over.
    /// </summary>
    public Task<ViewAllEntryDescriptor> DescribeEntryAsync(
        string path,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Describe(path);
        }, cancellationToken);

    /// <summary>
    /// Describes a run of entries, each one inside the one before it, in a
    /// single trip to the thread pool, and stops at the first one that is not
    /// there.  This is how a path is brought into the graph without reading any
    /// folder on the way: one object per level instead of every object in every
    /// level, which for <c>C:\Windows\System32\drivers\etc</c> is four entries
    /// instead of five thousand.
    ///
    /// Names come back spelled the way the disk spells them.  A path typed as
    /// <c>c:\windows\system32</c> gets the nodes <c>Windows</c> and
    /// <c>System32</c>, exactly what reading the folders would have produced;
    /// otherwise the typed spelling would live on in the tree, the list title
    /// and the address bar for as long as the node did.  A step whose spelling
    /// cannot be looked up - its parent cannot be listed, say - keeps the name it
    /// was asked for, which is what describing it one at a time always did.
    /// </summary>
    /// <param name="parentPath">The folder the first step is inside, as the graph spells it.</param>
    /// <param name="steps">Full paths, outermost first, each inside the previous one.</param>
    public Task<IReadOnlyList<ViewAllEntryDescriptor>> DescribeChainAsync(
        string parentPath,
        IReadOnlyList<string> steps,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ViewAllEntryDescriptor>>(() =>
        {
            var described = new List<ViewAllEntryDescriptor>(steps.Count);
            var parent = parentPath;
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Joined onto the parent as described rather than taken
                    // whole, so a step whose own spelling could not be looked up
                    // still sits under its parent's real one.
                    var name = LookUpSpelling(step)?.Name;
                    if (string.IsNullOrEmpty(name))
                    {
                        name = Path.GetFileName(step);
                    }

                    var entry = Describe(string.IsNullOrEmpty(name) ? step : Path.Combine(parent, name));
                    described.Add(entry);
                    parent = entry.FullPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or ArgumentException or NotSupportedException)
                {
                    break;
                }
            }

            return described;
        }, cancellationToken);

    /// <summary>
    /// Which of <paramref name="paths"/> no longer name what they named: gone
    /// from the disk, or renamed to a different spelling of the same name.  The
    /// second is what a rename from <c>Photos</c> to <c>photos</c> looks like to
    /// a case-insensitive file system - still there, and wrong in every place
    /// the old name is shown.  A path whose spelling cannot be checked is taken
    /// as it is.
    /// </summary>
    public Task<bool[]> FindStaleAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var stale = new bool[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[index];
                try
                {
                    // Exists answers false for access denied and for a share
                    // that is down; only "not found" means gone.
                    try
                    {
                        _ = File.GetAttributes(ForWindows(path));
                    }
                    catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                    {
                        stale[index] = true;
                        continue;
                    }

                    var leaf = Path.GetFileName(path);
                    if (LookUpSpelling(path) is { } spelling
                        && !string.Equals(spelling.Name, leaf, StringComparison.Ordinal)
                        && !string.Equals(spelling.ShortName, leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        stale[index] = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or ArgumentException or NotSupportedException)
                {
                    // Unreadable is not the same as gone; leave it alone.
                }
            }

            return stale;
        }, cancellationToken);

    /// <summary>
    /// One object by name, folder or file.  Shared by the one-at-a-time and the
    /// chain descriptions so the two can never disagree about what a hidden or
    /// a linked entry is.
    /// </summary>
    private static ViewAllEntryDescriptor Describe(string path)
    {
        var normalized = ViewAllPath.Normalize(path);
        var asked = ForWindows(normalized);

        if (Directory.Exists(asked))
        {
            var directory = new DirectoryInfo(asked);
            var attributes = directory.Attributes;
            return new ViewAllEntryDescriptor(
                normalized,
                string.IsNullOrEmpty(directory.Name) ? normalized : directory.Name,
                ViewAllEntryKind.Folder,
                attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System),
                IsLink(directory, attributes),
                SizeBytes: null,
                LastWriteUtc(directory),
                BuildSecondaryText(directory, isDirectory: true, size: null));
        }

        var file = new FileInfo(asked);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"No longer exists: {path}", path);
        }

        var fileAttributes = file.Attributes;
        return new ViewAllEntryDescriptor(
            normalized,
            file.Name,
            ViewAllEntryKind.File,
            fileAttributes.HasFlag(FileAttributes.Hidden) || fileAttributes.HasFlag(FileAttributes.System),
            fileAttributes.HasFlag(FileAttributes.ReparsePoint),
            file.Length,
            LastWriteUtc(file),
            BuildSecondaryText(file, isDirectory: false, file.Length));
    }

    /// <summary>
    /// Whether a folder is a link - a junction, a symbolic link, a mount point
    /// - decided as the canvas's reader (<see cref="NestedDirectoryReader"/>)
    /// decides it: only a link reports a target.  A cloud placeholder
    /// (OneDrive files on demand) or a projected folder carries the same
    /// reparse attribute but is an ordinary folder to read.  Taken for a link
    /// when it was brought in by name, it was never read, was labelled a link
    /// over whatever had been read of it, and was replaced - with everything
    /// below it - once its parent's listing said otherwise.
    /// </summary>
    private static bool IsLink(FileSystemInfo directory, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        try
        {
            return directory.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The last component of <paramref name="path"/> as its directory entry
    /// spells it, with the 8.3 alias beside it, or null when the entry cannot be
    /// looked up.  One <c>FindFirstFileEx</c> on the exact name: the file
    /// system finds the entry itself, where enumerating the parent to compare
    /// names would read the whole folder - the very thing this is here to avoid.
    ///
    /// A name with a wildcard in it is refused rather than looked up, since the
    /// lookup would treat <c>Win*</c> as a pattern and answer <c>Windows</c>.
    /// </summary>
    private static (string Name, string ShortName)? LookUpSpelling(string path)
    {
        var leaf = Path.GetFileName(path);
        if (string.IsNullOrEmpty(leaf) || leaf.AsSpan().IndexOfAny(Wildcards) >= 0)
        {
            return null;
        }

        var query = path.Length < MaximumShortPath
            ? ForWindows(path)
            : path.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? path
                : ExtendedLength(path);

        var handle = FindFirstFileEx(query, FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, 0);
        if (handle == InvalidHandle)
        {
            return null;
        }

        FindClose(handle);
        return string.IsNullOrEmpty(data.FileName) ? null : (data.FileName, data.AlternateFileName ?? string.Empty);
    }

    /// <summary>
    /// The path to hand Windows for <paramref name="path"/>: as it is, unless a
    /// name in it ends in a dot or a space.  Windows cuts those off on the way
    /// - "backup." would be asked about as its neighbour "backup", or as
    /// nothing - and the extended-length form leaves them as they are.
    /// </summary>
    private static string ForWindows(string path) =>
        ViewAllPath.EndsANameInDotOrSpace(path) && Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? ExtendedLength(path)
            : path;

    /// <summary>A full path in its extended-length form: <c>\\?\C:\...</c>, or <c>\\?\UNC\server\share\...</c> for a share.</summary>
    private static string ExtendedLength(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private const int MaximumShortPath = 260;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly char[] Wildcards = ['*', '?', '<', '>', '"'];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileEx(
        string fileName,
        int infoLevel,
        out Win32FindData findData,
        int searchOperation,
        IntPtr searchFilter,
        int additionalFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);

    /// <summary>
    /// One folder's entries, folders before files, each by name - up to the
    /// options' cap, past which the snapshot says it was cut short.
    /// </summary>
    /// <param name="shownIn">
    /// The order the caller shows the entries in.  Ordered by type, every
    /// kind of file among them is named here, off the UI thread, so ordering
    /// them there never has to ask the Shell; in any order, a kind met for
    /// the first time is queued to be named in the background.
    /// </param>
    /// <param name="keepFirstShown">
    /// When there are more entries than the cap, keep the first ones in
    /// <paramref name="shownIn"/> rather than the first ones read, which is
    /// what the file system hands out first - roughly names from A.  A list
    /// ordered newest first has to start with the newest entry of all, not
    /// the newest of the first few thousand names; that takes reading all of
    /// them, up to <see cref="ViewAllGraphOptions.MaximumChildrenCeiling"/>.
    /// </param>
    public Task<ViewAllDirectorySnapshot> GetChildrenAsync(
        string directoryPath,
        ViewAllGraphOptions options,
        CancellationToken cancellationToken = default,
        ItemSort shownIn = default,
        bool keepFirstShown = false)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Asked for as the graph spells it, a name's end and all (see
            // ForWindows): "backup." was read as its neighbour "backup", whose
            // entries were then listed - and deleted and renamed - under its
            // name, and a lone "lonely." could not be read at all.
            var normalized = ViewAllPath.Normalize(directoryPath);
            var directory = new DirectoryInfo(ForWindows(normalized));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Folder no longer exists: {directoryPath}");
            }

            var maximum = options.SafeMaximumChildren;
            var limit = keepFirstShown && !shownIn.IsDefault ? ViewAllGraphOptions.MaximumChildrenCeiling : maximum;
            var entries = new List<ViewAllEntryDescriptor>(Math.Min(maximum, 512));
            var isTruncated = false;

            // A folder that cannot be listed fails the read rather than reading
            // as empty: ignoring what is inaccessible ignores the folder itself,
            // and the list said "This folder is empty." for one it was denied.
            // Only the folder is opened - its entries come with its listing -
            // so there is nothing else for the option to pass over.
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            foreach (var info in directory.EnumerateFileSystemInfos("*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = info.Attributes;

                    // A system file is hidden for the same reason a hidden one
                    // is, and the dialog flag that reveals either reveals both,
                    // so they are one concept here.
                    var isHidden = attributes.HasFlag(FileAttributes.Hidden)
                        || attributes.HasFlag(FileAttributes.System);
                    if (isHidden && !options.IncludeHidden)
                    {
                        continue;
                    }

                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (!isDirectory)
                    {
                        // Folders are never filtered: they are how the user gets
                        // to the files that do match.
                        if (!options.ShowFiles)
                        {
                            continue;
                        }

                        if (options.FileFilter is { } filter && !filter.Matches(info.Name))
                        {
                            continue;
                        }
                    }

                    if (entries.Count >= limit)
                    {
                        isTruncated = true;
                        break;
                    }

                    long? size = null;
                    if (!isDirectory && info is FileInfo file)
                    {
                        size = file.Length;
                    }

                    var kind = isDirectory ? ViewAllEntryKind.Folder : ViewAllEntryKind.File;

                    // Joined onto the folder's own path: read through the
                    // extended-length form, an entry's full path carries it.
                    entries.Add(new ViewAllEntryDescriptor(
                        ViewAllPath.Normalize(Path.Join(normalized, info.Name)),
                        info.Name,
                        kind,
                        isHidden,
                        isDirectory ? IsLink(info, attributes) : attributes.HasFlag(FileAttributes.ReparsePoint),
                        size,
                        LastWriteUtc(info),
                        BuildSecondaryText(info, isDirectory, size)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    // Entries can vanish while the directory is being enumerated.
                }
            }

            FileTypeNames.WarmNames(
                entries.Where(entry => entry.Kind == ViewAllEntryKind.File).Select(entry => entry.DisplayName),
                lookUpNow: shownIn.Column == SortColumn.Type);

            if (entries.Count > maximum)
            {
                // Read past the cap to keep the first ones in the order shown:
                // the rest are dropped, and the snapshot says it is cut short.
                entries = ViewAllEntryOrder.Sort(entries, static entry => entry, shownIn);
                entries.RemoveRange(maximum, entries.Count - maximum);
                isTruncated = true;
            }

            entries.Sort(static (left, right) =>
            {
                var kind = left.Kind.CompareTo(right.Kind);
                return kind != 0
                    ? kind
                    : StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            });

            return new ViewAllDirectorySnapshot(entries, isTruncated, entries.Count);
        }, cancellationToken);

    private static string BuildSecondaryText(FileSystemInfo info, bool isDirectory, long? size)
    {
        if (isDirectory)
        {
            // Made while the folder is listed: a date the culture's calendar
            // cannot write would otherwise fail the whole listing.
            return Infrastructure.CultureDates.Format(LastWriteUtc(info).ToLocalTime(), "g");
        }

        var extension = string.IsNullOrWhiteSpace(info.Extension)
            ? "File"
            : $"{info.Extension.TrimStart('.').ToUpperInvariant()} file";
        return size.HasValue
            ? $"{extension}  ·  {FormatSize(size.Value)}"
            : extension;
    }

    /// <summary>
    /// When an entry was last written, or <see cref="DateTime.MinValue"/> -
    /// no date, as a drive has - when what the disk holds is not a date at
    /// all.  A tool or a damaged volume can leave a time past the year 9999
    /// on an entry, and one in its last hours has no local time east of
    /// Greenwich; either throws, and one such entry would fail the whole
    /// listing of its folder, and every description of it, each time.  The
    /// canvas's reader passes over such a time the same way.
    /// </summary>
    private static DateTime LastWriteUtc(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    private static string DriveTypeName(DriveType type) => type switch
    {
        DriveType.Removable => "Removable Disk",
        DriveType.Network => "Network Drive",
        DriveType.CDRom => "DVD Drive",
        DriveType.Ram => "RAM Disk",
        _ => "Local Disk"
    };

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{bytes:N0} B" : $"{value:0.#} {suffixes[suffix]}";
    }
}
