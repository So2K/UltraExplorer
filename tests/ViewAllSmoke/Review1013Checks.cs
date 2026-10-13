using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// Fixes of the review of 2026-10-13 (docs/REVIEW_2026-10-13.md) that can be
/// held without a window: a save that renames its original away kept the
/// folder's rename aliases for good, until nothing added there pulsed (T1);
/// Clear on the shelf took off what another window had put there since (A3);
/// the folder walk joined a drive root and a name with two separators (V3);
/// an operator left dangling while typing joined the path term next to it
/// (V1); and Ctrl+N accepted names Windows refuses later (V4).
/// </summary>
internal static partial class Program
{
    private static async Task Review1013Checks()
    {
        Section("review 2026-10-13: arrivals, shelf, search and new item names");
        await Review1013ArrivalAliasChecks();
        Review1013ShelfClearChecks();
        Review1013SearchChecks();
        Review1013CreateNameChecks();
        await Review1013WorkspaceHoldChecks();
        Review1013HistoryChecks();
    }

    /// <summary>
    /// V2: two quick Backs, the first overtaken by the second before it got
    /// anywhere and the second failing (its folder deleted), took the history
    /// back to the first's place while the pane had stayed put.
    /// </summary>
    private static void Review1013HistoryChecks()
    {
        var history = new NavigationHistory();
        history.Record(@"C:\3");
        history.Record(@"C:\4");
        history.Record(@"C:\5");
        history.Back();                       // to 4: overtaken, never lands
        var from = history.Position;
        history.Back();                       // to 3: deleted, fails
        history.Undo(from, history.Version);
        Check("a failed Back after an overtaken one leaves the history where the pane is",
            history.Current == @"C:\5" && history.Count == 3 && history.CanGoBack && !history.CanGoForward);

        history.Back();
        history.Landed(history.Position);     // got to 4
        from = history.Position;
        history.Back();                       // to 3: fails
        history.Undo(from, history.Version);
        Check("and after a Back that landed, a failed one returns to where that one got",
            history.Current == @"C:\4" && history.CanGoForward);
    }

    /// <summary>
    /// G2: a workspace file another program held while the canvas started was
    /// taken for a first run, and the first save wrote the empty start over
    /// the whole arrangement. A moment's hold is waited out; a longer one
    /// leaves the file as it is for the session.
    /// </summary>
    private static async Task Review1013WorkspaceHoldChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerReview1013Workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var statePath = Path.Combine(folder, "view-all.workspace.json");
        try
        {
            await new ViewAllWorkspaceStore(statePath).SaveAsync(new ViewAllWorkspaceState { ActivePath = @"C:\kept" });
            var original = File.ReadAllBytes(statePath);

            var held = new ViewAllWorkspaceStore(statePath);
            ViewAllWorkspaceState? loaded;
            using (new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                loaded = await held.LoadAsync();
            }

            await held.SaveAsync(new ViewAllWorkspaceState { ActivePath = @"C:\fresh start" });
            Check("a workspace held by another program is not taken for a first run's to overwrite",
                loaded is null && held.SavesHeld && File.ReadAllBytes(statePath).AsSpan().SequenceEqual(original));

            var brief = new ViewAllWorkspaceStore(statePath);
            var holder = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None);
            _ = Task.Delay(200).ContinueWith(_ => holder.Dispose());
            var briefly = await brief.LoadAsync();
            Check("a hold of a moment is waited out", briefly?.ActivePath == @"C:\kept" && !brief.SavesHeld);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// T1: Word saves by renaming the document away, renaming its temporary
    /// file to the document's name and deleting the backup. Each save left
    /// two rename pairs that never settled; after about 128 saves the folder's
    /// set was Incomplete and no new item in it pulsed again.
    /// </summary>
    private static async Task Review1013ArrivalAliasChecks()
    {
        var root = ArrivalDirectory();
        try
        {
            var document = Path.Combine(root, "report.docx");
            File.WriteAllText(document, "v0");
            using var tree = new NestedTree { IsReadingOnDemand = false, PostBackground = null };
            var arrivals = new List<NestedArrival>();
            tree.ItemsAdded += batch => arrivals.AddRange(batch);
            tree.SetRoots([new NestedRoot(root, "arrival fixture", NestedFolderKind.Drive)]);
            var top = tree.Find(root)!;
            await tree.LoadAsync(top);

            for (var save = 0; save < 150; save++)
            {
                var backup = Path.Combine(root, "~WRL0001.tmp");
                var written = Path.Combine(root, "~WRD0000.tmp");
                File.Move(document, backup);
                File.WriteAllText(written, $"v{save + 1}");
                File.Move(written, document);
                File.Delete(backup);
                tree.OnFolderChanged(top, new FolderChange(root, ChangeKinds.Structural, 0,
                    new RenamePair[] { new("report.docx", "~WRL0001.tmp"), new("~WRD0000.tmp", "report.docx") }, default));
                await tree.RefreshAsync(top);
            }

            Check($"150 rename-away saves of one document announce nothing new ({arrivals.Count})", arrivals.Count == 0);
            File.WriteAllText(Path.Combine(root, "added-after-saves.txt"), "new");
            await tree.RefreshAsync(top);
            Check("and a file added after them still arrives", arrivals.Count == 1 && arrivals[0].Name == "added-after-saves.txt");
        }
        finally { TryDelete(root); }
    }

    /// <summary>
    /// A3: Clear in one window replayed "clear everything" over the file: a
    /// card another window or copy of the app had put on the shelf since -
    /// and its copy, which may be the only one left - went with it.
    /// </summary>
    private static void Review1013ShelfClearChecks()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "UltraExplorerReview1013Shelf", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixture, "Shelf");
        var sources = Path.Combine(fixture, "Sources");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sources);
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        try
        {
            var seen = Path.Combine(sources, "seen.txt");
            File.WriteAllText(seen, "seen by this window");
            File.WriteAllText(Path.Combine(root, "shelf.json"),
                JsonSerializer.Serialize(new { Width = 520.0, Entries = new[] { new ShelfEntry(seen, DateTime.UtcNow, false) } }, options));
            var window = new ShelfStore(root);

            // Meanwhile another copy of the app put a taken-out file on the shelf.
            var copies = Path.Combine(root, "copies", "0123456789ab");
            Directory.CreateDirectory(copies);
            var later = Path.Combine(copies, "from-archive.txt");
            File.WriteAllText(later, "the only copy");
            File.WriteAllText(Path.Combine(root, "shelf.json"), JsonSerializer.Serialize(new
            {
                Width = 520.0,
                Entries = new[] { new ShelfEntry(seen, window.Entries[0].AddedUtc, false), new ShelfEntry(later, DateTime.UtcNow, true) }
            }, options));

            window.Clear();
            var reopened = new ShelfStore(root);
            Check("Clear takes off only the cards this window showed", reopened.Entries.Count == 1
                && string.Equals(reopened.Entries[0].Path, later, StringComparison.OrdinalIgnoreCase));
            Check("and leaves another window's copy where it is", File.Exists(later) && File.Exists(seen));
        }
        finally
        {
            try { Directory.Delete(fixture, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>V1 and V3: what Everything is asked, and the folder walk in a drive root.</summary>
    private static void Review1013SearchChecks()
    {
        Check("a bar left at the end does not join the folders left out",
            SearchQuery.Parse("foo |").Excluding([@"L:\"]) == @"foo !path:""L:\""");
        Check("nor a lone ! left at the end",
            SearchQuery.Parse("foo !").Excluding([@"L:\"]) == @"foo !path:""L:\""");
        Check("a bar at the start does not join the folder searched in",
            SearchQuery.Parse("|foo").Under(@"D:\Games") == @"path:""D:\Games\"" foo");
        Check("operators inside the text and words ending in ! are kept",
            SearchQuery.Parse("foo | bar").Excluding([@"L:\"]) == @"foo | bar !path:""L:\"""
            && SearchQuery.Parse("wow!").Excluding([@"L:\"]) == @"wow! !path:""L:\""");
        Check("an open quote keeps its text as typed",
            SearchQuery.Parse("\"foo |").Excluding([@"L:\"]) == "\"foo |\" !path:\"L:\\\"");

        var rooted = SearchQuery.Parse(@"C:\Windows");
        Check("a path term finds the folder itself in a drive root during the walk",
            rooted.Matches("Windows", @"C:\", true) && rooted.Matches("System32", @"C:\Windows", true));
    }

    /// <summary>V4: names Ctrl+N refused only after the dialog had closed.</summary>
    private static void Review1013CreateNameChecks()
    {
        var prompt = new CreateItemPrompt(@"C:\fixture", IsFolder: true);
        Check("a name of 256 characters is refused in the dialog", prompt.ValidationError(new string('a', 256)) is not null
            && prompt.ValidationError(new string('a', 255)) is null);
        Check("console device names are refused", prompt.ValidationError("CONOUT$") is not null
            && prompt.ValidationError("conin$") is not null && prompt.ValidationError("CONOUT$ notes") is null);
    }
}
