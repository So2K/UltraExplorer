using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The lazy folder tree behind the nested canvas, driven by an in-memory disk
/// so every read is counted and every failure can be staged; then the real
/// directory reader against a fixture on disk.
/// </summary>
internal static partial class Program
{
    private static async Task NestedTreeChecks()
    {
        Section("nested tree");

        var disk = new FakeDisk();
        disk.Folder(@"Q:\alpha\one\two\three\four\five");
        disk.Folder(@"Q:\beta");
        disk.AddFile(@"Q:\beta", "b.txt", 3);
        disk.AddFile(@"Q:\beta", "A.md", 10);
        disk.AddFile(@"Q:\beta", "c.bin", 0);
        disk.AddFile(@"Q:\beta", ".hidden", 7, hidden: true);
        disk.Folder(@"Q:\gamma");
        disk.Folder(@"Q:\.cache").IsHidden = true;
        disk.Folder(@"Q:\.cache\inner\deep");
        disk.Folder(@"Q:\.other").IsHidden = true;
        disk.Folder(@"Q:\locked").Error = "Access denied";
        disk.Folder(@"Q:\link").IsReparsePoint = true;
        disk.Folder(@"Q:\r\x\x1");
        disk.Folder(@"Q:\r\x\x2");
        disk.Folder(@"Q:\r\y\y1");
        disk.Folder(@"Q:\r\z");
        disk.Folder(@"R:\r1");

        using var tree = new NestedTree(disk.Read);
        var changes = 0;
        tree.Changed += (_, _) => changes++;

        // ---- roots -------------------------------------------------------------
        NestedRoot[] both =
        [
            new(@"Q:\", "Q:", NestedFolderKind.Drive, "10 GB free"),
            new(@"R:\", "R:", NestedFolderKind.Drive)
        ];
        tree.SetRoots(both);
        var q = tree.Find(@"Q:\");
        var r = tree.Find(@"R:\");
        Check("roots become the cells inside This PC",
            q is not null && r is not null
            && tree.Root.Children.SequenceEqual([q, r])
            && q.Index == 0 && r.Index == 1 && tree.Root.Grid.Count == 2);
        Check("a root keeps its kind, name and free space",
            q!.Kind == NestedFolderKind.Drive && q.Name == "Q:" && q.SecondaryText == "10 GB free" && q.Depth == 1);
        Check("setting the roots reads nothing", disk.Reads == 0);
        Check("and announces the change", changes > 0);
        Check("a root is found by path in any case", ReferenceEquals(tree.Find(@"q:\"), q));
        Check("a folder not read yet is not found", tree.Find(@"Q:\alpha") is null);

        tree.SetRoots(both);
        Check("setting the same roots again keeps the same folders",
            ReferenceEquals(tree.Find(@"Q:\"), q) && ReferenceEquals(tree.Find(@"R:\"), r));
        tree.SetRoots([both[0]]);
        Check("a root that went away is forgotten", r!.IsForgotten && tree.Find(@"R:\") is null && tree.Root.Children.Count == 1);
        tree.SetRoots(both);
        r = tree.Find(@"R:\")!;
        Check("and comes back as a new folder", !r.IsForgotten && r.Index == 1);

        // ---- revealing -----------------------------------------------------------
        var readsBefore = disk.Reads;
        const string FivePath = @"Q:\alpha\one\two\three\four\five";
        var five = await tree.RevealAsync(FivePath);
        Check("revealing a deep path returns exactly that folder", five?.FullPath == FivePath);
        Check("it reads each folder on the way there once", disk.Reads - readsBefore == 6);
        Check("but not the folder itself", five?.LoadState == NestedLoadState.NotLoaded);
        Check("every folder on the way is loaded and on the canvas",
            Ancestors(five!).All(folder => folder.IsLoaded && NestedTree.IsOnCanvas(folder)));
        Check("depth counts from This PC", five!.Depth == 7);

        readsBefore = disk.Reads;
        var again = await tree.RevealAsync(FivePath);
        Check("a second reveal returns the same folder and reads nothing", ReferenceEquals(again, five) && disk.Reads == readsBefore);
        var two = tree.Find(@"Q:\alpha\one\two")!;
        Check("case and a trailing separator do not matter",
            ReferenceEquals(await tree.RevealAsync(@"q:\ALPHA\one\TWO\"), two));
        Check("FindNearest below what was read is the deepest folder read",
            ReferenceEquals(tree.FindNearest(@"Q:\alpha\one\two\missing\x"), two));
        Check("Find of a path that is not a folder in the tree is null", tree.Find(@"Q:\alpha\one\two\missing") is null);
        Check("a path under no root reveals nothing",
            await tree.RevealAsync(@"Z:\nowhere\at\all") is null && tree.FindNearest(@"Z:\nowhere") is null);
        Check("a path that does not exist reveals the deepest folder that does",
            (await tree.RevealAsync(@"Q:\alpha\missing\x"))?.FullPath == @"Q:\alpha");

        // ---- reads that fail -------------------------------------------------------
        var locked = tree.Find(@"Q:\locked")!;
        await tree.LoadAsync(locked);
        Check("a folder that cannot be read is marked failed, with the reason",
            locked.LoadState == NestedLoadState.Failed && locked.ErrorMessage == "Access denied");
        Check("and holds nothing", locked.Children.Count == 0 && locked.Grid.IsEmpty && locked.Files.Count == 0);
        readsBefore = disk.Reads;
        await tree.LoadAsync(locked);
        Check("a failed folder is not read again by a plain load", disk.Reads == readsBefore);
        Check("revealing something inside it stops at it",
            ReferenceEquals(await tree.RevealAsync(@"Q:\locked\inside"), locked));
        disk.Folder(@"Q:\locked").Error = null;
        disk.Folder(@"Q:\locked\opened");
        await tree.RefreshAsync(locked);
        Check("a refresh reads a failed folder again, and clears the failure",
            locked.IsLoaded && locked.ErrorMessage.Length == 0 && locked.Children.Any(child => child.Name == "opened"));

        var gamma = tree.Find(@"Q:\gamma")!;
        disk.Remove(@"Q:\gamma");
        await tree.LoadAsync(gamma);
        Check("a folder deleted before it was read fails with a message",
            gamma.LoadState == NestedLoadState.Failed && gamma.ErrorMessage.Length > 0);

        // ---- files -------------------------------------------------------------------
        var beta = tree.Find(@"Q:\beta")!;
        await tree.LoadAsync(beta);
        Check("files are listed in name order, ignoring case",
            beta.Files.Select(file => file.Name).SequenceEqual(["A.md", "b.txt", "c.bin"]));
        Check("hidden files stay off the canvas but are counted",
            beta.Files.All(file => !file.IsHidden) && beta.FileCount == 4 && beta.HiddenFileCount == 1);
        Check("the file grid holds exactly the files shown", beta.FileGrid.Count == beta.Files.Count);
        Check("a file keeps its size", beta.Files.Single(file => file.Name == "b.txt").Length == 3);
        Check("a file's path is its folder and its name", beta.PathOf(beta.Files[0]) == @"Q:\beta\A.md");
        Check("a file's extension is lower case without the dot",
            new NestedFile("Photo.JPG", false, 0).Extension == "jpg" && new NestedFile(".gitignore", false, 0).Extension == ""
            && new NestedFile("trailing.", false, 0).Extension == "");

        tree.IncludeHidden = true;
        Check("showing hidden items adds the hidden file", beta.Files.Count == 4 && beta.FileGrid.Count == 4);
        tree.IncludeHidden = false;
        Check("and hiding them takes it away again", beta.Files.Count == 3 && beta.FileGrid.Count == 3);

        disk.Folder(@"Q:\beta").UnlistedFiles = 7;
        await tree.RefreshAsync(beta);
        Check("files past the listing limit are counted as not drawn", beta.UnlistedFileCount == 7 && beta.FileCount == 11);

        // ---- hidden folders ------------------------------------------------------------
        var cache = tree.Find(@"Q:\.cache")!;
        var other = tree.Find(@"Q:\.other")!;
        Check("a hidden folder is read but not placed",
            q.AllChildren.Contains(cache) && cache.Index == -1 && !q.Children.Contains(cache));
        Check("the grid is made for the visible children only",
            q.Grid.Count == q.Children.Count && q.Children.Count == q.AllChildren.Length - 2);
        Check("and every visible child sits where that grid says", PlacementProblem(q) is null);
        Check("FindNearest does not stop on a folder that is off the canvas",
            ReferenceEquals(tree.FindNearest(@"Q:\.cache\inner"), q));

        tree.IncludeHidden = true;
        Check("showing hidden items places hidden folders too",
            cache.Index >= 0 && other.Index >= 0 && q.Grid.Count == q.AllChildren.Length && PlacementProblem(q) is null);
        tree.IncludeHidden = false;
        Check("and hiding them takes them off again", cache.Index == -1 && other.Index == -1 && PlacementProblem(q) is null);

        // ---- folders the user hid --------------------------------------------------------
        var betaIndex = beta.Index;
        var betaOffset = (beta.OffsetX, beta.OffsetY, beta.Scale);
        var visibleBefore = q.Children.Count;
        tree.SetUserHidden([@"q:\BETA\"]);
        Check("a folder the user hid is taken off the canvas",
            beta.Index == -1 && !q.Children.Contains(beta) && tree.IsUserHidden(@"Q:\beta"));
        Check("its siblings close up over the space", q.Children.Count == visibleBefore - 1 && PlacementProblem(q) is null);
        Check("and nothing inside it is on the canvas", !NestedTree.IsOnCanvas(beta));
        tree.SetUserHidden([]);
        Check("showing it again puts it back exactly where it was",
            beta.Index == betaIndex && (beta.OffsetX, beta.OffsetY, beta.Scale) == betaOffset && PlacementProblem(q) is null);

        // ---- folders asked for by name -----------------------------------------------------
        tree.ForceVisible([@"Q:\.cache\inner"]);
        Check("a hidden folder on the way to a folder asked for is placed", cache.Index >= 0);
        Check("but a hidden folder next to it is not", other.Index == -1);
        var deep = await tree.RevealAsync(@"Q:\.cache\inner\deep");
        Check("so the folder asked for is on the canvas",
            deep?.FullPath == @"Q:\.cache\inner\deep" && NestedTree.IsOnCanvas(deep));

        // Reveal is how every navigation reaches the nested canvas, so this is
        // "the user went to a folder, then hid it".  The hide must win: it is
        // the newer request, and the canvas is the only thing that shows it.
        var revealedTwo = tree.Find(@"Q:\alpha\one\two")!;
        tree.SetUserHidden([revealedTwo.FullPath]);
        Check("a folder the user hid after going to it leaves the canvas", revealedTwo.Index == -1);
        tree.SetUserHidden([]);

        // ---- refresh ----------------------------------------------------------------------
        var rFolder = tree.Find(@"Q:\r")!;
        await tree.LoadAsync(rFolder);
        var x = tree.Find(@"Q:\r\x")!;
        var y = tree.Find(@"Q:\r\y")!;
        var z = tree.Find(@"Q:\r\z")!;
        await tree.LoadAsync(x);
        await tree.LoadAsync(y);
        var x1 = tree.Find(@"Q:\r\x\x1")!;
        var y1 = tree.Find(@"Q:\r\y\y1")!;

        disk.Remove(@"Q:\r\y");
        disk.Folder(@"Q:\r\w");
        disk.Folder(@"Q:\r\z").IsReparsePoint = true;
        var loaded = new List<NestedFolder>();
        tree.FolderLoaded += loaded.Add;
        await tree.RefreshAsync(rFolder);
        tree.FolderLoaded -= loaded.Add;

        Check("a refresh keeps a child that is still there as the same object",
            ReferenceEquals(tree.Find(@"Q:\r\x"), x) && rFolder.Children.Contains(x) && !x.IsForgotten);
        Check("with everything already read below it",
            x.IsLoaded && ReferenceEquals(tree.Find(@"Q:\r\x\x1"), x1) && x.Children.Contains(x1));
        Check("a child that is gone is forgotten",
            y.IsForgotten && y.Index == -1 && tree.Find(@"Q:\r\y") is null && !rFolder.Children.Contains(y));
        Check("and so is everything below it", NestedTree.IsDetached(y1) && !NestedTree.IsOnCanvas(y1) && tree.Find(@"Q:\r\y\y1") is null);
        var w = tree.Find(@"Q:\r\w");
        Check("a new child appears, unread", w is not null && rFolder.Children.Contains(w) && w.LoadState == NestedLoadState.NotLoaded);
        var zNow = tree.Find(@"Q:\r\z");
        Check("a child that turned into a link is replaced",
            zNow is { IsReparsePoint: true } && !ReferenceEquals(zNow, z) && z.IsForgotten);
        Check("children follow the listing's order",
            rFolder.Children.Select(child => child.Name).SequenceEqual(["w", "x", "z"]) && PlacementProblem(rFolder) is null);
        Check("a refresh says it loaded the folder", loaded.SequenceEqual([rFolder]));

        readsBefore = disk.Reads;
        await tree.RefreshAsync(w!);
        Check("refreshing a folder never read reads nothing", disk.Reads == readsBefore && w!.LoadState == NestedLoadState.NotLoaded);
        await tree.LoadAsync(x);
        Check("loading a folder already read reads nothing", disk.Reads == readsBefore);

        // ---- invalidate ---------------------------------------------------------------------
        tree.Invalidate(rFolder);
        Check("invalidating forgets everything below the folder",
            x.IsForgotten && NestedTree.IsDetached(x1) && tree.Find(@"Q:\r\x") is null && tree.Find(@"Q:\r\x\x1") is null);
        Check("and empties it until it is read again",
            rFolder.LoadState == NestedLoadState.NotLoaded && rFolder.Children.Count == 0
            && rFolder.Grid.IsEmpty && rFolder.Files.Count == 0 && rFolder.FileGrid.IsEmpty);
        await tree.LoadAsync(rFolder);
        var xAgain = tree.Find(@"Q:\r\x");
        Check("reading it again brings its children back as new folders",
            xAgain is not null && !ReferenceEquals(xAgain, x) && xAgain.LoadState == NestedLoadState.NotLoaded);

        // ---- links -------------------------------------------------------------------------
        var link = tree.Find(@"Q:\link")!;
        tree.Request(link, 1_000);
        Check("the canvas never reads a link on its own",
            !link.CanLoad && link.LoadState == NestedLoadState.NotLoaded && tree.PendingCount == 0);
        await tree.LoadAsync(link);
        Check("but a link asked for by name is read", link.IsLoaded);

        // ---- a folder with more sub-folders than one listing holds ---------------------------
        var huge = Enumerable.Range(0, NestedTree.MaximumChildren)
            .Select(index => new NestedEntry($"d{index:D6}", false, false))
            .ToList();
        disk.Folder(@"R:\huge");
        disk.Hook = (path, _) => string.Equals(path, @"R:\huge", StringComparison.OrdinalIgnoreCase)
            ? new NestedListing(huge, 5, 0, IsTruncated: true)
            {
                Files = [.. Enumerable.Range(0, 5).Select(index => new NestedFile($"f{index}.txt", false, index))]
            }
            : null;
        await tree.LoadAsync(r);
        var hugeFolder = tree.Find(@"R:\huge")!;
        var placing = Stopwatch.StartNew();
        await tree.LoadAsync(hugeFolder);
        placing.Stop();
        disk.Hook = null;
        Check("a listing cut off at the limit says so", hugeFolder.IsTruncated);
        Check($"all {NestedTree.MaximumChildren:N0} sub-folders become cells",
            hugeFolder.Children.Count == NestedTree.MaximumChildren && hugeFolder.Grid.Count == NestedTree.MaximumChildren);
        Check("each of them inside the folder and in its own place", PlacementProblem(hugeFolder) is null);
        Report($"reading and placing {NestedTree.MaximumChildren:N0} sub-folders", placing.ElapsedMilliseconds, 3_000);

        // ---- a read that is cancelled ------------------------------------------------------
        // Every public read takes a token.  A read given up half way has to
        // leave the folder as it found it, or it can never be read again.
        disk.Folder(@"Q:\slow\inside");
        await tree.RefreshAsync(q);
        var slow = tree.Find(@"Q:\slow")!;
        disk.Hook = (path, token) =>
        {
            if (string.Equals(path, @"Q:\slow", StringComparison.OrdinalIgnoreCase))
            {
                token.WaitHandle.WaitOne(5_000);
                token.ThrowIfCancellationRequested();
            }

            return null;
        };

        var cancelledOut = false;
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try
            {
                await tree.LoadAsync(slow, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                cancelledOut = true;
            }
        }

        disk.Hook = null;
        Check("a cancelled load ends in a cancellation", cancelledOut);
        Check($"a cancelled load leaves the folder unread, not reading (it is {slow.LoadState})",
            slow.LoadState == NestedLoadState.NotLoaded);

        var retried = false;
        using (var guard = new CancellationTokenSource(TimeSpan.FromMilliseconds(1_500)))
        {
            try
            {
                await tree.LoadAsync(slow, guard.Token);
                retried = slow.IsLoaded;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Check("and a later load of it completes", retried);
    }

    /// <summary>The real reader against real folders, then the tree on top of it.</summary>
    private static async Task NestedReaderChecks(string fixtureRoot)
    {
        Section("nested directory reader");

        var root = Path.Combine(fixtureRoot, "nested-real");
        Directory.CreateDirectory(Path.Combine(root, "alpha", "a1", "a2"));
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        Directory.CreateDirectory(Path.Combine(root, "gamma"));
        Directory.CreateDirectory(Path.Combine(root, ".hiddenDir")).Attributes |= FileAttributes.Hidden;
        Directory.CreateDirectory(Path.Combine(root, "sys")).Attributes |= FileAttributes.System;
        File.WriteAllText(Path.Combine(root, "b.txt"), "abc");
        File.WriteAllBytes(Path.Combine(root, "A.md"), new byte[10]);
        File.WriteAllBytes(Path.Combine(root, "c.bin"), []);
        File.WriteAllBytes(Path.Combine(root, "Zeta.TXT"), new byte[7]);
        var hiddenFile = Path.Combine(root, "hidden.dat");
        File.WriteAllBytes(hiddenFile, new byte[5]);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(root, "alpha", "note.md"), "n");
        var link = Path.Combine(root, "link");
        var hasLink = TryCreateJunction(link, Path.Combine(root, "alpha"));

        var listing = NestedDirectoryReader.Read(root, CancellationToken.None);
        string[] folderNames = [".hiddenDir", "alpha", "Beta", "gamma", "sys", .. hasLink ? new[] { "link" } : []];
        Check("a directory reads without an error", listing.ErrorMessage.Length == 0);
        Check("its sub-folders are listed in name order, ignoring case",
            listing.Folders.Select(entry => entry.Name)
                .SequenceEqual(folderNames.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)));
        Check("its files are listed the same way",
            listing.Files.Select(file => file.Name)
                .SequenceEqual(["A.md", "b.txt", "c.bin", "hidden.dat", "Zeta.TXT"]));
        Check("every file is counted, hidden ones included", listing.FileCount == 5 && listing.HiddenFileCount == 1);
        Check("a hidden file is flagged", listing.Files.Single(file => file.IsHidden).Name == "hidden.dat");
        Check("sizes are read",
            listing.Files.Single(file => file.Name == "b.txt").Length == 3
            && listing.Files.Single(file => file.Name == "A.md").Length == 10
            && listing.Files.Single(file => file.Name == "Zeta.TXT").Length == 7);
        Check("hidden and system folders are flagged hidden, others not",
            listing.Folders.Single(entry => entry.Name == ".hiddenDir").IsHidden
            && listing.Folders.Single(entry => entry.Name == "sys").IsHidden
            && !listing.Folders.Single(entry => entry.Name == "alpha").IsHidden);
        if (hasLink)
        {
            Check("a junction is flagged as a link",
                listing.Folders.Single(entry => entry.Name == "link").IsReparsePoint
                && !listing.Folders.Single(entry => entry.Name == "alpha").IsReparsePoint);
        }
        else
        {
            Console.WriteLine("  note  could not create a junction here; the link flag is not checked");
        }

        Check("a small directory is not truncated", !listing.IsTruncated);
        Check("a directory that is not there fails with a message",
            NestedDirectoryReader.Read(Path.Combine(root, "missing"), CancellationToken.None).ErrorMessage == "No longer exists");
        Check("a file is not a directory",
            NestedDirectoryReader.Read(Path.Combine(root, "b.txt"), CancellationToken.None).ErrorMessage.Length > 0);

        using var tree = new NestedTree();
        tree.SetRoots([new NestedRoot(root, "nested-real", NestedFolderKind.Drive)]);
        var a2Path = Path.Combine(root, "alpha", "a1", "a2");
        var a2 = await tree.RevealAsync(a2Path);
        Check("the tree reveals a real path through the real reader", a2 is not null && ViewAllPath.Equals(a2.FullPath, a2Path));
        var top = tree.Find(root)!;
        Check("hidden and system folders are read but kept off the canvas",
            top.AllChildren.Length == folderNames.Length
            && top.Children.All(child => child.Name is not ".hiddenDir" and not "sys"));
        Check("so is the hidden file", top.Files.Count == 4 && top.FileCount == 5);
        tree.IncludeHidden = true;
        Check("until hidden items are shown", top.Children.Count == folderNames.Length && top.Files.Count == 5);
        tree.IncludeHidden = false;
        Check("every real folder the tree placed is inside its parent", PlacementProblem(top) is null);

        if (hasLink && tree.Find(link) is { } linkFolder)
        {
            tree.Request(linkFolder, 1_000);
            Check("a real junction is not read by the canvas", !linkFolder.CanLoad && linkFolder.LoadState == NestedLoadState.NotLoaded);
            await tree.LoadAsync(linkFolder);
            Check("but is read when asked for by name", linkFolder.IsLoaded && linkFolder.Children.Any(child => child.Name == "a1"));
        }

        var alpha = tree.Find(Path.Combine(root, "alpha"))!;
        var gammaBefore = tree.Find(Path.Combine(root, "gamma"))!;
        Directory.CreateDirectory(Path.Combine(root, "delta"));
        Directory.Delete(Path.Combine(root, "gamma"));
        await tree.RefreshAsync(top);
        Check("a refresh finds a real folder that was created", tree.Find(Path.Combine(root, "delta")) is not null);
        Check("and forgets one that was deleted", tree.Find(Path.Combine(root, "gamma")) is null && gammaBefore.IsForgotten);
        Check("and keeps the rest as they were", ReferenceEquals(tree.Find(Path.Combine(root, "alpha")), alpha) && alpha.IsLoaded);
    }

    // ---- helpers ---------------------------------------------------------------------

    private static IEnumerable<NestedFolder> Ancestors(NestedFolder folder)
    {
        for (var current = folder.Parent; current is not null && !current.IsComputer; current = current.Parent)
        {
            yield return current;
        }
    }

    /// <summary>
    /// Whether the tree put a folder's visible children and files where its own
    /// grids say, inside the folder, clear of each other; null when it did.
    /// </summary>
    private static string? PlacementProblem(NestedFolder folder)
    {
        var grid = folder.Grid;
        if (grid.Count != folder.Children.Count)
        {
            return $"{folder}: its grid holds {grid.Count} but it shows {folder.Children.Count}";
        }

        for (var index = 0; index < folder.Children.Count; index++)
        {
            var child = folder.Children[index];
            var (x, y) = grid.Origin(index);
            if (child.Index != index || !ReferenceEquals(child.Parent, folder))
            {
                return $"{child}: index {child.Index}, expected {index}";
            }

            if (Math.Abs(child.OffsetX - x) > 1e-12 || Math.Abs(child.OffsetY - y) > 1e-12 || child.Scale != grid.Scale)
            {
                return $"{child}: at ({child.OffsetX}, {child.OffsetY}) x{child.Scale}, its grid says ({x}, {y}) x{grid.Scale}";
            }

            if (x < NestedLayout.Padding - LayoutEpsilon
                || x + grid.Scale > 1 - NestedLayout.Padding + LayoutEpsilon
                || y < NestedLayout.HeaderHeight - LayoutEpsilon
                || y + grid.Scale * NestedLayout.CellHeight > NestedLayout.CellHeight - NestedLayout.Padding + LayoutEpsilon)
            {
                return $"{child}: outside its parent's content area";
            }
        }

        // Every child the folder read either is the cell at its index or has
        // none; with the loop above that makes the cells exactly the visible ones.
        var placed = 0;
        foreach (var child in folder.AllChildren)
        {
            if (child.Index < 0)
            {
                continue;
            }

            placed++;
            if (child.Index >= folder.Children.Count || !ReferenceEquals(folder.Children[child.Index], child))
            {
                return $"{child}: off the canvas but still has index {child.Index}";
            }
        }

        if (placed != folder.Children.Count)
        {
            return $"{folder}: {placed} children claim a place, {folder.Children.Count} are shown";
        }

        var files = folder.FileGrid;
        if (files.Count != folder.Files.Count)
        {
            return $"{folder}: its file grid holds {files.Count} but it shows {folder.Files.Count} files";
        }

        if (files.Count > 0)
        {
            var folderBottom = grid.IsEmpty ? NestedLayout.HeaderHeight : grid.Top + grid.Height;
            var lastRowBottom = files.Top + (files.Rows - 1) * files.StepY + files.TileHeight;
            var lastColumnRight = files.Left + (files.Columns - 1) * files.StepX + files.TileWidth;
            if (files.Top < folderBottom - LayoutEpsilon
                || files.Left < NestedLayout.Padding - LayoutEpsilon
                || lastColumnRight > 1 - NestedLayout.Padding + LayoutEpsilon
                || lastRowBottom > NestedLayout.CellHeight - NestedLayout.Padding + LayoutEpsilon)
            {
                return $"{folder}: its files run outside the space under its sub-folders";
            }
        }

        return null;
    }

    /// <summary>Reads every folder the predicate accepts, a level at a time.</summary>
    private static async Task LoadEverythingAsync(NestedTree tree, Func<NestedFolder, bool> predicate)
    {
        var level = tree.Root.AllChildren.ToList();
        while (level.Count > 0)
        {
            var wanted = level
                .Where(folder => folder.CanLoad && folder.LoadState == NestedLoadState.NotLoaded && predicate(folder))
                .ToList();
            await Task.WhenAll(wanted.Select(folder => tree.LoadAsync(folder)));
            level = [.. wanted.SelectMany(folder => folder.AllChildren)];
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(10_000);
            return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
