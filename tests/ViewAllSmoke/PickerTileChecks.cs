using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>Tile picker regressions: selection paths stay exact while the
/// focused folder catches up, and file-type changes reuse cached listings.</summary>
internal static partial class Program
{
    private static async Task PickerTileChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerTiles", Guid.NewGuid().ToString("N"));
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var leftFile = Path.Combine(left, "same.txt");
            var rightFile = Path.Combine(right, "same.txt");
            File.WriteAllText(leftFile, "left");
            File.WriteAllText(rightFile, "right");

            PickerTileSelectionChecks(root, left, right, leftFile, rightFile);
            await PickerTileListingChecks();
            await PickerPhysicalHierarchyChecks(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void PickerTileSelectionChecks(string root, string left, string right, string leftFile, string rightFile)
    {
        Section("tile picker: selection paths and typed names");
        var clients = new FileDialogClientStore(Path.Combine(root, "clients.json"));

        var folder = new FileDialogSession(new FileDialogRequest
        {
            Mode = FileDialogMode.PickFolder,
            InitialFolder = root
        }, clients);
        folder.ReportSelection([new SelectionItem(left, true, 0)]);
        folder.CurrentFolder = left;
        Check("a selected folder survives the focus moving into that folder",
            PickerTileHasPaths(folder.Prepare([left]), left));
        Check("a selected folder is not resolved as a second child with the same name",
            !folder.Prepare([left]).Paths.Contains(Path.Combine(left, Path.GetFileName(left)), StringComparer.OrdinalIgnoreCase));

        var open = new FileDialogSession(new FileDialogRequest { InitialFolder = left }, clients);
        open.ReportSelection([new SelectionItem(rightFile, false, 5)]);
        Check("a tile in another folder is accepted before asynchronous focus catches up",
            PickerTileHasPaths(open.Prepare([rightFile]), rightFile));
        open.CurrentFolder = root;
        Check("a focus change cannot reinterpret a selection-generated basename",
            PickerTileHasPaths(open.Prepare([rightFile]), rightFile));
        open.ReportSelection(Array.Empty<SelectionItem>());
        open.CurrentFolder = left;
        Check("background deselection and navigation preserve the exact pending file choice",
            open.LastSelection.Count == 0 && open.PendingSelection.SequenceEqual([rightFile])
            && PickerTileHasPaths(open.Prepare([]), rightFile));
        Check("the pending hint names the file and its real folder",
            open.PendingSelectionLabel.Contains("same.txt", StringComparison.Ordinal)
            && open.PendingSelectionLocation.Contains(right, StringComparison.Ordinal));
        open.ReportSelection([new SelectionItem(leftFile, false, 4)]);
        Check("a new file choice replaces the previous pending path",
            PickerTileHasPaths(open.Prepare([]), leftFile));
        open.ClearChoice();
        Check("explicit clear removes the name and pending choice",
            open.FileNameText.Length == 0 && !open.HasPendingSelection
            && open.Prepare([]).Kind == FileDialogActionKind.None);

        var multi = new FileDialogSession(new FileDialogRequest
        {
            InitialFolder = root,
            Options = FileDialogOptions.AllowMultiSelect
        }, clients);
        multi.ReportSelection([new SelectionItem(leftFile, false, 4), new SelectionItem(rightFile, false, 5)]);
        Check("multi-select preserves both full paths when basenames are identical",
            PickerTileHasPaths(multi.Prepare([leftFile, rightFile]), leftFile, rightFile));
        Check("the caller sees the exact multi-selection snapshot",
            multi.LastSelection.SequenceEqual([leftFile, rightFile], StringComparer.OrdinalIgnoreCase));
        multi.ReportSelection(Array.Empty<SelectionItem>());
        multi.CurrentFolder = right;
        Check("multi-selection keeps both full paths after navigation clears its highlight",
            multi.LastSelection.Count == 0 && PickerTileHasPaths(multi.Prepare([]), leftFile, rightFile));
        var confirmed = new FileDialogSession(new FileDialogRequest
        {
            InitialFolder = right, FileName = multi.FileNameText, Options = FileDialogOptions.AllowMultiSelect
        }, clients);
        confirmed.CarryChoiceFrom(multi);
        Check("the same dialog's confirmed contract retains deselected full paths",
            PickerTileHasPaths(confirmed.Prepare([]), leftFile, rightFile));
        var fresh = new FileDialogSession(new FileDialogRequest { InitialFolder = right }, clients);
        Check("a new request inherits no pending choice", fresh.PendingSelection.Count == 0 && !fresh.CanAccept);

        open.CurrentFolder = left;
        open.FileNameText = "typed.txt";
        Check("editing the name overrides the old canvas selection",
            !open.HasPendingSelection && PickerTileHasPaths(open.Prepare([rightFile]), Path.Combine(left, "typed.txt")));
        open.FileNameText = rightFile;
        Check("typing a full path keeps that explicit destination",
            PickerTileHasPaths(open.Prepare([leftFile]), rightFile));

        var request = new FileDialogRequest
        {
            Mode = FileDialogMode.Save,
            InitialFolder = right,
            DefaultExtension = "txt"
        };
        request.Filters.Add(new FileDialogFilterSpec("Text", "*.txt"));
        request.Filters.Add(new FileDialogFilterSpec("Markdown", "*.md"));
        var save = new FileDialogSession(request, clients);
        save.ReportSelection([new SelectionItem(rightFile, false, 5)]);
        save.SelectedFilterIndex = 1;
        Check("changing the save type changes the selected name extension",
            save.FileNameText == "same.md");
        Check("a changed save extension supersedes the selected existing path",
            PickerTileHasPaths(save.Prepare([rightFile]), Path.Combine(right, "same.md")));
        save.FileNameText = "export";
        Check("a typed save name supersedes selection and gets the configured extension",
            PickerTileHasPaths(save.Prepare([rightFile]), Path.Combine(right, "export.txt")));
        save.FileNameText = "explicit.bin";
        save.SelectedFilterIndex = 0;
        Check("changing type preserves an explicitly typed unmatched extension",
            save.FileNameText == "explicit.bin"
            && PickerTileHasPaths(save.Prepare([rightFile]), Path.Combine(right, "explicit.bin")));
    }

    private static async Task PickerTileListingChecks()
    {
        Section("tile picker: file types, hidden entries and cached listings");
        const string folderPath = @"Q:\picker";
        var disk = new FakeDisk();
        disk.Folder(Path.Combine(folderPath, "visible-folder"));
        disk.Folder(Path.Combine(folderPath, "hidden-folder")).IsHidden = true;
        disk.AddFile(folderPath, "report.txt", 1);
        disk.AddFile(folderPath, "photo.png", 2);
        disk.AddFile(folderPath, "hidden.txt", 3, hidden: true);
        disk.AddFile(folderPath, "readme", 4);

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, PostBackground = null };
        var text = FileDialogFilter.Parse("*.txt");
        tree.FileNameFilter = text.Matches;
        tree.SetRoots([new NestedRoot(folderPath, "picker", NestedFolderKind.Drive)]);
        var folder = tree.Find(folderPath)!;
        await tree.LoadAsync(folder);
        Check("the initial file type filters the very first applied listing",
            folder.Files.Select(file => file.Name).SequenceEqual(["report.txt"]));
        Check("a file-type filter keeps visible folders regardless of their names",
            folder.Children.Select(child => child.Name).SequenceEqual(["visible-folder"]));
        Check("hidden files and folders stay hidden before requested",
            folder.Files.All(file => !file.IsHidden) && folder.Children.All(child => !child.IsHidden));

        var reads = disk.Reads;
        var listing = folder.AllFiles;
        var image = FileDialogFilter.Parse("*.png");
        tree.FileNameFilter = image.Matches;
        Check("switching type replaces visible file tiles immediately",
            folder.Files.Select(file => file.Name).SequenceEqual(["photo.png"]));
        Check("switching type reuses the original complete listing without another read",
            disk.Reads == reads && ReferenceEquals(folder.AllFiles, listing) && folder.AllFiles.Length == 4);

        tree.IncludeHidden = true;
        Check("including hidden folders does not bypass the selected file type",
            folder.Children.Count == 2 && folder.Files.Select(file => file.Name).SequenceEqual(["photo.png"]));
        tree.FileNameFilter = text.Matches;
        Check("a matching hidden file becomes visible with the hidden rule enabled",
            folder.Files.Select(file => file.Name).SequenceEqual(["hidden.txt", "report.txt"]));

        tree.ShowFiles = false;
        tree.FlushSortWork();
        Check("folder-only mode removes file tiles while retaining folders and the cached file listing",
            folder.Files.Count == 0 && folder.Children.Count == 2 && ReferenceEquals(folder.AllFiles, listing));
        tree.ShowFiles = true;
        tree.FlushSortWork();
        Check("returning to file mode restores the selected type from cache",
            folder.Files.Select(file => file.Name).SequenceEqual(["hidden.txt", "report.txt"]) && disk.Reads == reads);

        tree.FileNameFilter = null;
        Check("All Files restores extensionless names too",
            folder.Files.Count == 4 && folder.Files.Any(file => file.Name == "readme"));
        tree.IncludeHidden = false;
        Check("turning hidden entries off reapplies both folder and file visibility",
            folder.Files.Count == 3 && folder.Children.Count == 1
            && folder.Files.All(file => !file.IsHidden) && folder.Children.All(child => !child.IsHidden));
        Check("every visibility change above used the already-read listing", disk.Reads == reads);
    }

    private static bool PickerTileHasPaths(FileDialogAction action, params string[] paths) =>
        action.Kind == FileDialogActionKind.Accept
        && action.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);

    private static async Task PickerPhysicalHierarchyChecks(string root)
    {
        Section("picker hierarchy: named ancestors without directory enumeration");
        var drive = Path.Combine(root, "physical-drive");
        var unrelated = Path.Combine(root, "unvisited-drive");
        var profile = Path.Combine(drive, "Users", "Fixture");
        var downloads = Path.Combine(profile, "Downloads");
        var pictures = Path.Combine(profile, "Pictures");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(pictures);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(downloads, "chosen.txt"), "chosen");
        var reads = new List<string>();
        using var tree = new NestedTree((path, token) =>
        {
            lock (reads) reads.Add(path);
            return NestedDirectoryReader.Read(path, token);
        }) { IsReadingOnDemand = false };
        tree.SetRoots([new(drive, "C:", NestedFolderKind.Drive), new(unrelated, "D:", NestedFolderKind.Drive)]);
        var folder = await tree.MaterializePathAsync(downloads);
        Check("metadata materialization does not enumerate any folder contents", reads.Count == 0);
        Check("the materialized folder has its real profile, Users and drive parents",
            folder?.Parent?.FullPath == profile && folder.Parent.Parent?.Name == "Users"
            && folder.Parent.Parent.Parent?.FullPath == drive && folder.Parent.Parent.Parent.Parent?.IsComputer == true);
        Check("This PC contains only the two physical drive roots", tree.Root.AllChildren.Length == 2
            && tree.Root.AllChildren.All(child => child.Kind == NestedFolderKind.Drive));
        Check("ancestors remain explicitly partial and unread", tree.Find(drive) is { HasPartialListing: true, IsLoaded: false }
            && tree.Find(profile) is { HasPartialListing: true, IsLoaded: false });
        await tree.LoadAsync(folder!);
        Check("the initial contents read touches only the requested folder",
            reads.SequenceEqual([downloads]) && folder!.Files.Any(file => file.Name == "chosen.txt"));
        var sibling = await tree.MaterializePathAsync(pictures);
        Check("a favorite sibling is nested under the same physical profile without extra reads or roots",
            sibling?.Parent == folder!.Parent && reads.Count == 1 && tree.Root.AllChildren.Length == 2);
        await tree.LoadAsync(tree.Find(drive)!);
        await tree.LoadAsync(tree.Find(Path.Combine(drive, "Users"))!);
        await tree.LoadAsync(tree.Find(profile)!);
        Check("later full ancestor listings preserve the focused folder and cached files",
            ReferenceEquals(tree.Find(downloads), folder) && ReferenceEquals(tree.Find(pictures), sibling)
            && folder!.Files.Any(file => file.Name == "chosen.txt"));
        Check("a fully read ancestor stops being a partial listing", tree.Find(profile) is { HasPartialListing: false, IsLoaded: true });
        Check("the other drive remains unread throughout navigation", !reads.Contains(unrelated));
        Check("a missing named folder is not invented or returned as its parent",
            await tree.MaterializePathAsync(Path.Combine(profile, "missing-folder")) is null);
    }
}
