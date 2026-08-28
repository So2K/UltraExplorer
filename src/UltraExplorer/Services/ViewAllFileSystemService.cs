using System.Globalization;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// One-level-at-a-time file-system reader for View All.  It never recurses and
/// stops after the configured safety cap, which keeps opening a drive instant.
/// </summary>
public sealed class ViewAllFileSystemService
{
    public Task<IReadOnlyList<ViewAllEntryDescriptor>> GetDriveRootsAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ViewAllEntryDescriptor>>(() =>
        {
            var roots = new List<ViewAllEntryDescriptor>();
            foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    var volume = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? DriveTypeName(drive.DriveType)
                        : drive.VolumeLabel;
                    var driveName = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                    var secondary = $"{drive.DriveFormat}  ·  {FormatSize(drive.AvailableFreeSpace)} free of {FormatSize(drive.TotalSize)}";
                    roots.Add(new ViewAllEntryDescriptor(
                        ViewAllPath.Normalize(drive.RootDirectory.FullName),
                        $"{volume} ({driveName})",
                        ViewAllEntryKind.Drive,
                        IsHidden: false,
                        IsReparsePoint: false,
                        SizeBytes: drive.TotalSize,
                        ModifiedUtc: DateTime.MinValue,
                        SecondaryText: secondary));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Explorer also omits drives that disappear while refreshing.
                }
            }

            return roots;
        }, cancellationToken);

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
            var directory = new DirectoryInfo(normalized);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Folder no longer exists: {directoryPath}");
            }

            var attributes = directory.Attributes;
            return new ViewAllEntryDescriptor(
                normalized,
                string.IsNullOrEmpty(directory.Name) ? normalized : directory.Name,
                ViewAllEntryKind.Folder,
                attributes.HasFlag(FileAttributes.Hidden),
                attributes.HasFlag(FileAttributes.ReparsePoint),
                SizeBytes: null,
                directory.LastWriteTimeUtc,
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
            var normalized = ViewAllPath.Normalize(path);

            if (Directory.Exists(normalized))
            {
                var directory = new DirectoryInfo(normalized);
                var attributes = directory.Attributes;
                return new ViewAllEntryDescriptor(
                    normalized,
                    string.IsNullOrEmpty(directory.Name) ? normalized : directory.Name,
                    ViewAllEntryKind.Folder,
                    attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System),
                    attributes.HasFlag(FileAttributes.ReparsePoint),
                    SizeBytes: null,
                    directory.LastWriteTimeUtc,
                    BuildSecondaryText(directory, isDirectory: true, size: null));
            }

            var file = new FileInfo(normalized);
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
                file.LastWriteTimeUtc,
                BuildSecondaryText(file, isDirectory: false, file.Length));
        }, cancellationToken);

    public Task<ViewAllDirectorySnapshot> GetChildrenAsync(
        string directoryPath,
        ViewAllGraphOptions options,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(ViewAllPath.Normalize(directoryPath));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Folder no longer exists: {directoryPath}");
            }

            var maximum = options.SafeMaximumChildren;
            var entries = new List<ViewAllEntryDescriptor>(Math.Min(maximum, 512));
            var isTruncated = false;
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
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

                    if (entries.Count >= maximum)
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
                    entries.Add(new ViewAllEntryDescriptor(
                        ViewAllPath.Normalize(info.FullName),
                        info.Name,
                        kind,
                        isHidden,
                        attributes.HasFlag(FileAttributes.ReparsePoint),
                        size,
                        info.LastWriteTimeUtc,
                        BuildSecondaryText(info, isDirectory, size)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    // Entries can vanish while the directory is being enumerated.
                }
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
            return info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
        }

        var extension = string.IsNullOrWhiteSpace(info.Extension)
            ? "File"
            : $"{info.Extension.TrimStart('.').ToUpperInvariant()} file";
        return size.HasValue
            ? $"{extension}  ·  {FormatSize(size.Value)}"
            : extension;
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
