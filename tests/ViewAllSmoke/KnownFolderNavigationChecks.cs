using System.IO;
using System.Runtime.InteropServices;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task KnownFolderNavigationChecks()
    {
        Section("Known folders: current Windows locations");
        using var icons = new ShellIconService();
        var quickAccess = new FileSystemService(icons).GetQuickAccess();
        var downloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = ReadNavigationKnownFolderPath(ref downloadsId, 0, 0, out var pointer);
        string? downloads;
        try { downloads = result >= 0 && pointer != 0 ? Marshal.PtrToStringUni(pointer) : null; }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
        var downloadItem = quickAccess.SingleOrDefault(item => item.Name == "Downloads");
        var downloadsAvailable = !string.IsNullOrEmpty(downloads) && Directory.Exists(downloads);
        Check("Downloads uses the current KnownFolder path, including Windows redirection",
            downloadsAvailable
                ? downloadItem is not null && SameNavigationPath(downloadItem.Path, downloads!)
                : downloadItem is null);
        Check("unresolved known folders are not represented by empty paths",
            quickAccess.All(item => item.Path.Length > 0 && Path.IsPathFullyQualified(item.Path)));

        foreach (var (name, folder) in new[]
        {
            ("Desktop", Environment.SpecialFolder.DesktopDirectory),
            ("Documents", Environment.SpecialFolder.MyDocuments),
            ("Pictures", Environment.SpecialFolder.MyPictures)
        })
        {
            var path = Environment.GetFolderPath(folder);
            var item = quickAccess.SingleOrDefault(candidate => candidate.Name == name);
            Check($"{name} retains its current Windows special-folder path",
                path.Length > 0 && Directory.Exists(path)
                    ? item is not null && SameNavigationPath(item.Path, path)
                    : item is null);
        }
        return Task.CompletedTask;
    }

    private static bool SameNavigationPath(string first, string second)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    [DllImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath", ExactSpelling = true)]
    private static extern int ReadNavigationKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
}
