using System.Globalization;
using System.IO;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed class FileSystemService(ShellIconService iconService)
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    public Task<IReadOnlyList<FileItemViewModel>> GetDirectoryItemsAsync(string path, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<FileItemViewModel>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Folder no longer exists: {path}");
            }

            var items = new List<FileItemViewModel>();
            foreach (var info in directory.EnumerateFileSystemInfos("*", EnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                    long? size = isDirectory ? null : ((FileInfo)info).Length;
                    items.Add(new FileItemViewModel
                    {
                        FullPath = info.FullName,
                        Name = info.Name,
                        IsDirectory = isDirectory,
                        TypeDescription = isDirectory ? "Folder" : GetTypeDescription(info.Extension),
                        SizeBytes = size,
                        SizeDisplay = size is null ? string.Empty : FormatSize(size.Value),
                        ModifiedUtc = info.LastWriteTimeUtc,
                        ModifiedDisplay = info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
                        IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                        Icon = iconService.GetSmallIcon(info.FullName, isDirectory)
                    });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // The Shell also skips entries that disappear during enumeration.
                }
            }

            return items
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<SearchResultViewModel>> SearchAsync(
        string rootPath,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<SearchResultViewModel>>(() =>
        {
            var results = new List<SearchResultViewModel>(Math.Min(maxResults, 128));
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0 && results.Count < maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();

                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool isDirectory;
                        try
                        {
                            isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);
                        }
                        catch
                        {
                            continue;
                        }

                        var name = Path.GetFileName(entry);
                        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new SearchResultViewModel(
                                name,
                                entry,
                                current,
                                isDirectory,
                                isDirectory ? "\uE8B7" : "\uE8A5"));
                            if (results.Count >= maxResults)
                            {
                                break;
                            }
                        }

                        if (isDirectory)
                        {
                            try
                            {
                                var attributes = File.GetAttributes(entry);
                                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                                {
                                    pending.Push(entry);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
                {
                }
            }

            return results;
        }, cancellationToken);

    public static IReadOnlyList<FavoriteItemViewModel> GetSystemFavorites()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            new FavoriteItemViewModel { Name = "Home", Path = profile, Glyph = "\uE80F", AccentHex = "#70A0FF" },
            new FavoriteItemViewModel { Name = "Desktop", Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Glyph = "\uE7F4" },
            new FavoriteItemViewModel { Name = "Downloads", Path = Path.Combine(profile, "Downloads"), Glyph = "\uE896" },
            new FavoriteItemViewModel { Name = "Documents", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Glyph = "\uE8A5" },
            new FavoriteItemViewModel { Name = "Pictures", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Glyph = "\uEB9F" },
            new FavoriteItemViewModel { Name = "Music", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Glyph = "\uE8D6" },
            new FavoriteItemViewModel { Name = "Videos", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Glyph = "\uE8B2" }
        };

        return candidates.Where(item => Directory.Exists(item.Path)).ToArray();
    }

    public static IReadOnlyList<FavoriteItemViewModel> GetDrives()
        => DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new FavoriteItemViewModel
            {
                Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({drive.Name.TrimEnd('\\')})" : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
                Path = drive.RootDirectory.FullName,
                Glyph = "\uEDA2",
                AccentHex = "#98A2B3"
            })
            .ToArray();

    public static FileSystemWatcher CreateWatcher(string path, Action changed)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler onChange = (_, _) => changed();
        RenamedEventHandler onRename = (_, _) => changed();
        ErrorEventHandler onError = (_, _) => changed();
        watcher.Created += onChange;
        watcher.Changed += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.Error += onError;
        return watcher;
    }

    public static string FormatSize(long bytes)
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

    private static string GetTypeDescription(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "File";
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }
}
