using System.IO;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>A drive that answered as ready: the name the navigation pane shows it by, its root, and whether it is a share.</summary>
public readonly record struct ReadyDrive(string Name, string Path, bool IsNetwork);

/// <summary>
/// Shell-level helpers: navigation pane content and recursive search.
/// Directory enumeration for the graph itself lives in
/// <see cref="ViewAllFileSystemService"/>; keeping what is shown current as
/// the disk changes is the change hub's (<see cref="Watch.ChangeHub"/>).
/// </summary>
public sealed class FileSystemService(ShellIconService iconService)
{
    private const string WslRoot = @"\\wsl$";

    /// <summary>
    /// Breadth-first so the shallowest — and usually most relevant — matches
    /// arrive first, and streamed through <paramref name="progress"/> so the
    /// results list fills in while the walk is still running.
    /// </summary>
    public Task<IReadOnlyList<SearchResultViewModel>> SearchAsync(
        string rootPath,
        string query,
        int maxResults,
        IProgress<SearchResultViewModel>? progress,
        CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<SearchResultViewModel>>(() =>
        {
            var results = new List<SearchResultViewModel>(Math.Min(maxResults, 128));
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(rootPath);

            while (pending.Count > 0 && results.Count < maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try
                        {
                            attributes = File.GetAttributes(entry);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                        var name = Path.GetFileName(entry);
                        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var result = new SearchResultViewModel(
                                name,
                                entry,
                                current,
                                isDirectory,
                                isDirectory ? "\uE8B7" : "\uE8A5");
                            results.Add(result);
                            progress?.Report(result);
                            if (results.Count >= maxResults)
                            {
                                break;
                            }
                        }

                        // Reparse points are skipped: following them turns the
                        // search into an unbounded walk over the same folders.
                        if (isDirectory && !attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Enqueue(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
                {
                }
            }

            return results;
        }, cancellationToken);

    public IReadOnlyList<FavoriteItemViewModel> GetQuickAccess()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Create("Home", profile, "\uE80F", "#60CDFF"),
            Create("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4"),
            Create("Downloads", Path.Combine(profile, "Downloads"), "\uE896"),
            Create("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5"),
            Create("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F"),
            Create("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "\uE8D6"),
            Create("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "\uE8B2")
        };

        return candidates.Where(item => Directory.Exists(item.Path)).ToArray();
    }

    public IReadOnlyList<FavoriteItemViewModel> GetDrives() => GetDrives(ListReadyDrives());

    /// <summary>The navigation pane's items for drives already listed (<see cref="ListReadyDrives"/>).</summary>
    public IReadOnlyList<FavoriteItemViewModel> GetDrives(IReadOnlyList<ReadyDrive> drives)
        => drives
            .Select(drive =>
            {
                var item = new FavoriteItemViewModel
                {
                    Name = drive.Name,
                    Path = drive.Path,
                    Glyph = drive.IsNetwork ? "\uE968" : "\uEDA2",
                    AccentHex = "#9AA4B2",
                    Kind = SidebarItemKind.Drive
                };
                item.Icon = iconService.GetSmallIcon(item.Path, isDirectory: true);
                return item;
            })
            .ToArray();

    /// <summary>
    /// The drives that are ready, named as the navigation pane names them.
    /// Asking a drive whether it is ready, and for its label, can take seconds
    /// for a disc spinning up or a share that has gone, so a caller that can
    /// wait - a volume arriving or leaving - asks off the UI thread.
    /// </summary>
    public static IReadOnlyList<ReadyDrive> ListReadyDrives()
        => DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new ReadyDrive(
                $"{SafeVolumeLabel(drive)} ({drive.Name.TrimEnd('\\')})",
                drive.RootDirectory.FullName,
                drive.DriveType == DriveType.Network))
            .ToArray();

    /// <summary>WSL distributions and the Explorer network view, when present.</summary>
    public IReadOnlyList<FavoriteItemViewModel> GetNetworkLocations()
    {
        var items = new List<FavoriteItemViewModel>();

        try
        {
            if (Directory.Exists(WslRoot))
            {
                foreach (var distribution in Directory.EnumerateDirectories(WslRoot).Take(8))
                {
                    var item = new FavoriteItemViewModel
                    {
                        Name = Path.GetFileName(distribution),
                        Path = distribution,
                        Glyph = "\uEC7A",
                        AccentHex = "#E3B341",
                        Kind = SidebarItemKind.Network
                    };
                    item.Icon = iconService.GetSmallIcon(distribution, isDirectory: true);
                    items.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stopped WSL service simply means there is nothing to list.
        }

        items.Add(new FavoriteItemViewModel
        {
            Name = "Network",
            Path = "shell:NetworkPlacesFolder",
            Glyph = "\uE968",
            AccentHex = "#9AA4B2",
            Kind = SidebarItemKind.Network,
            OpensInShell = true
        });

        return items;
    }

    public void AttachIcons(IEnumerable<FavoriteItemViewModel> items)
    {
        foreach (var item in items.Where(item => item.Icon is null && !item.OpensInShell))
        {
            item.Icon = iconService.GetSmallIcon(item.Path, isDirectory: true);
        }
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

    private FavoriteItemViewModel Create(string name, string path, string glyph, string accentHex = "#C8C8C8")
    {
        var item = new FavoriteItemViewModel
        {
            Name = name,
            Path = path,
            Glyph = glyph,
            AccentHex = accentHex
        };

        if (Directory.Exists(path))
        {
            item.Icon = iconService.GetSmallIcon(path, isDirectory: true);
        }

        return item;
    }

    private static string SafeVolumeLabel(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.DriveType switch
                {
                    DriveType.Removable => "Removable Disk",
                    DriveType.Network => "Network Drive",
                    DriveType.CDRom => "DVD Drive",
                    _ => "Local Disk"
                }
                : drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Disk";
        }
    }
}
