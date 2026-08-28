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
                    var isHidden = attributes.HasFlag(FileAttributes.Hidden);
                    if (isHidden && !options.IncludeHidden)
                    {
                        continue;
                    }

                    if (entries.Count >= maximum)
                    {
                        isTruncated = true;
                        break;
                    }

                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
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
