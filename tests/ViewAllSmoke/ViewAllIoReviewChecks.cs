using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The tree's reading and saving, from the second full review: a folder whose
/// name ends in a dot or a space (J051).
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllIoReviewChecks()
    {
        await FolderNamedWithADotIsReadAsItselfAsync();
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
}
