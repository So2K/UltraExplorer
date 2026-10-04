using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The tree's reading and saving, from the second full review: a folder whose
/// name ends in a dot or a space (J051), and a folder with more entries than
/// one read takes (J050).
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllIoReviewChecks()
    {
        await FolderNamedWithADotIsReadAsItselfAsync();
        await FolderPastTheCapKeepsItsFoldersAsync();
    }

    // ---- J051: a folder whose name ends in a dot or a space ----------------

    /// <summary>
    /// "backup." beside "backup", and "lonely." alone, as WSL, git or a share
    /// can leave them.  Read by the name the graph keeps, the dotted folder
    /// was read as its neighbour - backup's files listed under backup.'s
    /// title, so Delete and Rename on them acted on backup's - and the lone
    /// one could not be read at all.
    /// </summary>
    private static async Task FolderNamedWithADotIsReadAsItselfAsync()
    {
        Section("view-all io: a folder whose name ends in a dot or a space is read as itself (J051)");
        const string ExtendedLength = @"\\?\";
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerDotRead", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup");
        var dotted = backup + ".";
        var lonely = Path.Combine(root, "lonely.");
        var spaced = Path.Combine(dotted, "inner ");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "in-backup.txt"), "plain");

        // Made through the extended-length form, which keeps the names' ends.
        Directory.CreateDirectory(ExtendedLength + spaced);
        Directory.CreateDirectory(ExtendedLength + lonely);
        File.WriteAllText(ExtendedLength + Path.Combine(dotted, "in-dotted.txt"), "dotted");
        File.WriteAllText(ExtendedLength + Path.Combine(lonely, "alone.txt"), "alone");
        File.WriteAllText(ExtendedLength + Path.Combine(spaced, "deep.txt"), "deep");
        try
        {
            var files = new ViewAllFileSystemService();
            var options = new ViewAllGraphOptions();
            async Task<string> ReadAsync(string folder)
            {
                try
                {
                    var snapshot = await files.GetChildrenAsync(folder, options);
                    return string.Join(" | ", snapshot.Entries.Select(entry => $"{entry.FullPath} ({entry.DisplayName})"));
                }
                catch (DirectoryNotFoundException ex)
                {
                    return ex.Message;
                }
            }

            var dottedRead = await ReadAsync(dotted);
            Check($"'backup.' lists its own entries, not backup's ({dottedRead})",
                dottedRead == $"{spaced} (inner ) | {Path.Combine(dotted, "in-dotted.txt")} (in-dotted.txt)");
            var lonelyRead = await ReadAsync(lonely);
            Check($"'lonely.', with no neighbour, can be read ({lonelyRead})", lonelyRead == $"{Path.Combine(lonely, "alone.txt")} (alone.txt)");
            var spacedRead = await ReadAsync(spaced);
            Check($"so can a folder ending in a space inside it ({spacedRead})", spacedRead == $"{Path.Combine(spaced, "deep.txt")} (deep.txt)");
            var plainRead = await ReadAsync(backup);
            var rootRead = await ReadAsync(root);
            Check($"and every other folder reads as ever ({plainRead}; {rootRead})",
                plainRead == $"{Path.Combine(backup, "in-backup.txt")} (in-backup.txt)"
                && rootRead == $"{backup} (backup) | {dotted} (backup.) | {lonely} (lonely.)");

            // The tree: opening backup. shows backup.'s own, and backup keeps its own.
            using var graph = new ViewAllGraphService();
            var top = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(top);
            if (graph.TryGetNode(dotted, out var dottedNode) && graph.TryGetNode(backup, out var backupNode))
            {
                await graph.ExpandAsync(dottedNode);
                await graph.ExpandAsync(backupNode);
                var underDotted = string.Join(" | ", dottedNode.Children.Select(child => child.FullPath));
                var underPlain = string.Join(" | ", backupNode.Children.Select(child => child.FullPath));
                Check($"on the tree, 'backup.' holds its own and 'backup' its own ({underDotted}; {underPlain})",
                    underDotted == $"{spaced} | {Path.Combine(dotted, "in-dotted.txt")}"
                    && underPlain == Path.Combine(backup, "in-backup.txt"));
            }
            else
            {
                Check("on the tree, 'backup.' and 'backup' are both there", false);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(ExtendedLength + root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder.
            }
        }
    }

    // ---- J050: a folder with more entries than one read takes ---------------

    /// <summary>
    /// A camera folder: forty pictures, and the folders Archive, Screens and
    /// Videos, read with a cap of thirty-two.  The file system hands out
    /// Archive, then the pictures, then Screens and Videos; cut where the
    /// thirty-second entry fell, the list, a dialog and the tree showed
    /// Archive alone, in the order the folders lead in, and nothing offered
    /// to load the other two.
    /// </summary>
    private static async Task FolderPastTheCapKeepsItsFoldersAsync()
    {
        Section("view-all io: a folder past the cap still shows every sub-folder (J050)");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerCapFolders", Guid.NewGuid().ToString("N"));
        string[] folders = ["Archive", "Screens", "Videos"];
        try
        {
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
            }

            for (var index = 1; index <= 40; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"IMG_{index:D4}.jpg"), []);
            }

            var files = new ViewAllFileSystemService();
            var options = new ViewAllGraphOptions(MaximumChildrenPerFolder: 32);
            string Describe(IEnumerable<ViewAllEntryDescriptor> entries, bool isTruncated)
            {
                var list = entries.ToList();
                var shownFolders = list.Where(entry => entry.Kind == ViewAllEntryKind.Folder).Select(entry => entry.DisplayName);
                return $"{list.Count} entries, folders [{string.Join(", ", shownFolders)}]{(isTruncated ? ", cut short" : string.Empty)}";
            }

            // Every folder, then as many pictures as are left room for, in
            // name order, and the read still says it was cut short.
            bool Kept(IReadOnlyList<ViewAllEntryDescriptor> entries, bool isTruncated, bool filesByName = true)
            {
                var pictures = entries.Skip(folders.Length).Select(entry => entry.DisplayName).ToList();
                return isTruncated
                    && entries.Count == 32
                    && entries.Take(folders.Length).Select(entry => entry.DisplayName).SequenceEqual(folders)
                    && entries.Skip(folders.Length).All(entry => entry.Kind == ViewAllEntryKind.File)
                    && (!filesByName || pictures.SequenceEqual(pictures.Order(StringComparer.CurrentCultureIgnoreCase)));
            }

            // The list and a dialog, in the default order.
            var listed = await files.GetChildrenAsync(root, options, default, ItemSort.Default, keepFirstShown: true);
            Check($"the list in names from A shows every folder ({Describe(listed.Entries, listed.IsTruncated)})",
                Kept(listed.Entries, listed.IsTruncated));

            // The tree's own read, which keeps the first ones read.
            var read = await files.GetChildrenAsync(root, options);
            Check($"so does the tree's read ({Describe(read.Entries, read.IsTruncated)})", Kept(read.Entries, read.IsTruncated));

            // Newest first already read them all; it still does.
            var newest = await files.GetChildrenAsync(root, options, default, new ItemSort(SortColumn.Modified, true), keepFirstShown: true);
            Check($"and the list newest first, as before ({Describe(newest.Entries, newest.IsTruncated)})",
                Kept(newest.Entries, newest.IsTruncated, filesByName: false));

            // On the tree itself, and Load more brings in the rest once.
            using var graph = new ViewAllGraphService(options);
            var top = (await graph.AddRootAsync(root))!;
            var expansion = await graph.ExpandAsync(top);
            var children = top.Children.Select(child => child.Entry).ToList();
            Check($"the tree opens the folder with every sub-folder ({Describe(children, expansion.IsTruncated)})",
                Kept(children, expansion.IsTruncated));
            var more = await graph.LoadMoreAsync(top, additionalChildren: 64);
            Check($"and Load more brings in the rest, each once ({top.Children.Count} children)",
                !more.IsTruncated && top.Children.Count == 43
                && top.Children.Select(child => child.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 43);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
