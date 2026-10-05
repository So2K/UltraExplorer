using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.Services;

/// <summary>A drive that answered as ready: the name the navigation pane shows it by, its root, and whether it is a share.</summary>
public readonly record struct ReadyDrive(string Name, string Path, bool IsNetwork);

/// <summary>A known folder that answered as there: the name the navigation pane shows it by, where it is, and how it is drawn.</summary>
public readonly record struct KnownPlace(string Name, string Path, string Glyph, string AccentHex = "#C8C8C8");

/// <summary>
/// Shell-level helpers: navigation pane content.  Search is
/// <see cref="Search.SearchEngine"/>'s.
/// Directory enumeration for the graph itself lives in
/// <see cref="ViewAllFileSystemService"/>; keeping what is shown current as
/// the disk changes is the change hub's (<see cref="Watch.ChangeHub"/>).
/// </summary>
public sealed class FileSystemService(ShellIconService iconService)
{
    private const string WslRoot = @"\\wsl$";

    /// <summary>
    /// How a drive's root, a known folder or <c>\\wsl$</c> is asked whether it
    /// is there.  A drive mapped to a server that is off takes some twenty
    /// seconds to answer, and so can a known folder moved onto a share, which
    /// is why every list here that asks is made off the UI thread.  A test puts
    /// a slow answer here to see that nothing waits for it there.
    /// </summary>
    internal static Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    public IReadOnlyList<FavoriteItemViewModel> GetQuickAccess() => GetQuickAccess(ListQuickAccess());

    /// <summary>The navigation pane's items for known folders already found (<see cref="ListQuickAccess"/>).</summary>
    public IReadOnlyList<FavoriteItemViewModel> GetQuickAccess(IReadOnlyList<KnownPlace> places)
        => places
            .Select(place =>
            {
                var item = new FavoriteItemViewModel
                {
                    Name = place.Name,
                    Path = place.Path,
                    Glyph = place.Glyph,
                    AccentHex = place.AccentHex
                };
                item.Icon = iconService.GetSmallIcon(item.Path, isDirectory: true);
                return item;
            })
            .ToArray();

    /// <summary>
    /// Home and the known folders that are there, in the order the navigation
    /// pane shows them.  Asks the disk about each (<see cref="FolderExists"/>),
    /// so a window asks off the UI thread.
    /// </summary>
    public static IReadOnlyList<KnownPlace> ListQuickAccess()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            new KnownPlace("Home", profile, "\uE80F", "#60CDFF"),
            new KnownPlace("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4"),
            // Downloads can be moved to another drive or share. Ask Windows
            // for its current location instead of inventing a profile path.
            new KnownPlace("Downloads", DialogNative.KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? string.Empty, "\uE896"),
            new KnownPlace("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5"),
            new KnownPlace("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F"),
            new KnownPlace("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "\uE8D6"),
            new KnownPlace("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "\uE8B2")
        };

        return candidates.Where(place => place.Path.Length > 0 && FolderExists(place.Path)).ToArray();
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
    /// wait - a volume arriving or leaving - asks off the UI thread, and so
    /// does a window as it starts.  Every drive is asked at once, in the order
    /// listed: one after the other, two drives mapped to servers that are off
    /// took twice the twenty seconds each takes to say it is not ready.
    /// </summary>
    public static IReadOnlyList<ReadyDrive> ListReadyDrives()
    {
        var asking = DriveInfo.GetDrives()
            .Select(drive => Task.Run(() =>
                // What DriveInfo.IsReady asks, through FolderExists.
                FolderExists(drive.Name)
                    ? new ReadyDrive(
                        $"{SafeVolumeLabel(drive)} ({drive.Name.TrimEnd('\\')})",
                        drive.RootDirectory.FullName,
                        drive.DriveType == DriveType.Network)
                    : (ReadyDrive?)null))
            .ToArray();
        Task.WaitAll(asking);
        return asking
            .Where(answer => answer.Result is not null)
            .Select(answer => answer.Result!.Value)
            .ToArray();
    }

    /// <summary>WSL distributions and the Explorer network view, when present.</summary>
    public IReadOnlyList<FavoriteItemViewModel> GetNetworkLocations() => GetNetworkLocations(ListWslDistributions());

    /// <summary>The navigation pane's network places for WSL distributions already listed (<see cref="ListWslDistributions"/>), and the Explorer network view.</summary>
    public IReadOnlyList<FavoriteItemViewModel> GetNetworkLocations(IReadOnlyList<string> distributions)
    {
        var items = new List<FavoriteItemViewModel>();
        foreach (var distribution in distributions)
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

    /// <summary>
    /// The WSL distributions, at most eight.  Asks <c>\\wsl$</c>, which can be
    /// slow to answer, so a window asks off the UI thread.
    /// </summary>
    public static IReadOnlyList<string> ListWslDistributions()
    {
        var distributions = new List<string>();
        try
        {
            if (FolderExists(WslRoot))
            {
                foreach (var distribution in Directory.EnumerateDirectories(WslRoot).Take(8))
                {
                    distributions.Add(distribution);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stopped WSL service simply means there is nothing to list.
        }

        return distributions;
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
