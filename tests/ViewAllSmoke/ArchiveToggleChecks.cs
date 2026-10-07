using System.IO;
using System.IO.Compression;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ArchiveToggleChecks()
    {
        RunOnSta("archive switches and extraction regressions", ArchiveToggleOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ArchiveToggleOnStaAsync()
    {
        Section("archives: opt-in and safe extraction");
        var previous = ArchiveService.BrowseArchives;
        var previousPrompt = ArchiveService.PasswordPrompt;
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerArchiveToggle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ordinary", "deep"));
        var zip = Path.Combine(root, "test.zip");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("folder/file.txt").Open());
                writer.Write("complete bytes");
            }

            ArchiveService.BrowseArchives = false;
            using var tree = new NestedTree();
            Check("new tree starts with archive folders disabled", !tree.ArchivesEnabled);
            tree.SetRoots([new NestedRoot(root, "fixture", NestedFolderKind.Folder)]);
            var physical = tree.Find(root)!;
            await tree.LoadAsync(physical);
            Check("OFF shows ZIP as an ordinary file", physical.AllFiles.Any(file => file.Name == "test.zip")
                && physical.AllChildren.All(folder => folder.Name != "test.zip"));
            var ordinary = tree.Find(Path.Combine(root, "ordinary"))!;
            await tree.LoadAsync(ordinary);
            Check("OFF keeps archive APIs inactive", ArchiveService.TryRead(zip, default) is null && !ArchiveService.IsArchiveFile(zip));

            if (!ArchiveService.IsAvailable)
            {
                Console.WriteLine("  (archive runtime unavailable: codec integration checks skipped)");
                await ArchiveToggleReadGenerationCheck();
                return;
            }

            ArchiveService.BrowseArchives = true;
            tree.ArchivesEnabled = true;
            await tree.RefreshAsync(physical);
            Check("ON changes the ZIP file into an archive folder", tree.Find(zip) is { IsArchive: true }
                && physical.AllFiles.All(file => file.Name != "test.zip"));
            Check("toggle preserves a loaded physical branch", ReferenceEquals(tree.Find(ordinary.FullPath), ordinary) && ordinary.IsLoaded);
            var inner = await tree.MaterializePathAsync(Path.Combine(zip, "folder"));
            Check("ON can navigate into the archive", inner is { IsInArchive: true });
            var pickerListing = NestedDirectoryReader.Read(root, default, archivesEnabled: false);
            Check("a picker read stays on real files while the normal window is ON", pickerListing.Files.Any(file => file.Name == "test.zip")
                && pickerListing.Folders.All(folder => !folder.IsArchive));

            ArchiveService.BrowseArchives = false;
            await tree.LoadAsync(inner!);
            Check("an enabled tree reads its archive even when another window sets the default OFF", inner!.Files.Any(file => file.Name == "file.txt"));
            Check("an explicit ON reader remains independent of the global default", NestedDirectoryReader.Read(zip, default, true)
                .Folders.Any(folder => folder.Name == "folder"));
            var nestedForMode = Path.Combine(root, "mode.zip");
            using (var archive = ZipFile.Open(nestedForMode, ZipArchiveMode.Create)) archive.CreateEntryFromFile(zip, "inner.zip");
            Check("nested archive classification follows the explicit ON reader", NestedDirectoryReader.Read(nestedForMode, default, true)
                .Folders.Any(folder => folder.Name == "inner.zip" && folder.IsArchive));
            Check("direct virtual-folder navigation resolves without a preloaded listing", ArchiveService.IsFolderLikeForNavigation(Path.Combine(nestedForMode, "inner.zip", "folder")));

            ArchiveService.BrowseArchives = false;
            tree.ArchivesEnabled = false;
            await tree.RefreshAsync(physical);
            Check("OFF removes the virtual subtree and restores the file", tree.Find(zip) is null
                && physical.AllFiles.Any(file => file.Name == "test.zip") && inner is not null && NestedTree.IsDetached(inner));
            Check("write safety still recognizes a virtual path after OFF", ArchiveService.IsInsideArchive(Path.Combine(zip, "folder", "file.txt")));
            Check("OFF materialization cannot reuse a lingering virtual branch", await tree.MaterializePathAsync(Path.Combine(zip, "folder")) is null);

            var acceptedOut = Path.Combine(root, "accepted-out");
            Directory.CreateDirectory(acceptedOut);
            var accepted = await ArchiveService.ExtractToAsync([Path.Combine(zip, "folder", "file.txt")], acceptedOut, null, default);
            Check("an accepted backend extraction can finish after browsing is OFF", File.ReadAllText(accepted[0]) == "complete bytes");

            ArchiveService.BrowseArchives = true;
            tree.ArchivesEnabled = true;
            await tree.RefreshAsync(physical);
            Check("ON works again after an OFF round trip", tree.Find(zip) is { IsArchive: true });

            var outDirectory = Path.Combine(root, "out");
            Directory.CreateDirectory(outDirectory);
            var made = await ArchiveService.ExtractToAsync([Path.Combine(zip, "folder"), Path.Combine(zip, "folder", "file.txt")], outDirectory, null, default);
            Check("overlapping folder and child selections both produce their output", made.Count == 2
                && File.ReadAllText(Path.Combine(outDirectory, "folder", "file.txt")) == "complete bytes"
                && File.ReadAllText(Path.Combine(outDirectory, "file.txt")) == "complete bytes");
            var duplicates = await ArchiveService.ExtractToAsync([Path.Combine(zip, "folder", "file.txt"), Path.Combine(zip, "folder", "file.txt")], outDirectory, null, default);
            Check("duplicate selection destinations are unique and both have bytes", duplicates.Count == 2 && duplicates[0] != duplicates[1]
                && duplicates.All(path => File.ReadAllText(path) == "complete bytes"));

            var opens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "folder", "file.txt"), null, default)));
            Check("concurrent opens publish only a complete shared cache file", opens.Distinct().Count() == 1
                && opens.All(path => File.ReadAllText(path) == "complete bytes"));

            var nestedZip = Path.Combine(root, "nested.zip");
            using (var archive = ZipFile.Open(nestedZip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(zip, "inner.zip");
            var nestedPath = Path.Combine(nestedZip, "inner.zip", "folder");
            var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ArchiveService.TryRead(nestedPath, default))));
            Check("concurrent nested reads share a fully extracted archive", reads.All(read => read is { ErrorMessage.Length: 0 }
                && read.Files.Any(file => file.Name == "file.txt")));

            var caseZip = Path.Combine(root, "case.zip");
            using (var archive = ZipFile.Open(caseZip, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("foo.txt").Open())) writer.Write("lower");
                using (var writer = new StreamWriter(archive.CreateEntry("Foo.txt").Open())) writer.Write("upper");
            }
            var caseListing = NestedDirectoryReader.Read(caseZip, default, true);
            Check("case-distinct archive entries both remain visible", caseListing.Files.Count == 2);
            var caseCopies = await ArchiveService.ExtractToAsync([Path.Combine(caseZip, "foo.txt"), Path.Combine(caseZip, "Foo.txt")], outDirectory, null, default);
            Check("case-distinct entries keep their own bytes during copy", File.ReadAllText(caseCopies[0]) == "lower"
                && File.ReadAllText(caseCopies[1]) == "upper");
            var conflictRefused = false;
            try { await ArchiveService.ExtractArchiveAsync(caseZip, outDirectory, true, null, default); }
            catch (InvalidDataException) { conflictRefused = true; }
            Check("whole extraction refuses case-colliding output names instead of losing a file", conflictRefused);

            var oversized = Path.Combine(root, "oversized.zip");
            using (var archive = ZipFile.Open(oversized, ZipArchiveMode.Create)) archive.CreateEntryFromFile(zip, "inner.zip", CompressionLevel.NoCompression);
            var headerBytes = File.ReadAllBytes(oversized);
            for (var at = 0; at + 28 <= headerBytes.Length; at++)
            {
                if (headerBytes[at] != 0x50 || headerBytes[at + 1] != 0x4b) continue;
                var offset = headerBytes[at + 2] == 3 && headerBytes[at + 3] == 4 ? 22
                    : headerBytes[at + 2] == 1 && headerBytes[at + 3] == 2 ? 24 : -1;
                if (offset >= 0) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(at + offset, 4), 513u << 20);
            }
            File.WriteAllBytes(oversized, headerBytes);
            var boundedRead = await Task.Run(() => ArchiveService.TryRead(Path.Combine(oversized, "inner.zip"), default, true));
            Check("automatic nested archive browsing refuses a declared payload above 512 MiB", boundedRead?.ErrorMessage.Contains("512 MiB", StringComparison.Ordinal) == true);

            var capped = Path.Combine(root, "capped.zip");
            using (var archive = ZipFile.Open(capped, ZipArchiveMode.Create))
                for (var number = 0; number < NestedTree.MaximumFiles + 2; number++) archive.CreateEntry($"file{number:D6}.txt");
            var cappedListing = await Task.Run(() => NestedDirectoryReader.Read(capped, default, true));
            Check("large archive file listings respect the canvas cap and preserve total counts", cappedListing.Files.Count == NestedTree.MaximumFiles
                && cappedListing.FileCount == NestedTree.MaximumFiles + 2);

            var link = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outDirectory);
                var refused = false;
                try { await ArchiveService.ExtractToAsync([Path.Combine(zip, "folder", "file.txt")], link, null, default); }
                catch (IOException) { refused = true; }
                Check("extraction refuses a destination symbolic link", refused);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Console.WriteLine("  (symbolic-link privilege unavailable: destination-link fixture skipped)");
            }

            var junction = Path.Combine(root, "junction");
            RunTool("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{outDirectory}\"");
            Check("owned junction fixture is created without symbolic-link privilege", Directory.Exists(junction)
                && new DirectoryInfo(junction).LinkTarget is not null);
            var outputCount = Directory.GetFiles(outDirectory).Length;
            var junctionRefused = false;
            try { await ArchiveService.ExtractToAsync([Path.Combine(zip, "folder", "file.txt")], junction, null, default); }
            catch (IOException) { junctionRefused = true; }
            Check("the cloud-placeholder exception still refuses a real junction and writes no destination file", junctionRefused
                && Directory.GetFiles(outDirectory).Length == outputCount);

            await ArchiveNestedPasswordCheck(root, zip);
            await ArchiveOperationPromptOwnerCheck(root, zip);
            await ArchiveToggleReadGenerationCheck();
        }
        finally
        {
            ArchiveService.BrowseArchives = previous;
            ArchiveService.PasswordPrompt = previousPrompt;
            TryDelete(root);
        }
    }

    private static async Task ArchiveNestedPasswordCheck(string root, string sourceZip)
    {
        if (!File.Exists(SevenZipExe)) return;
        var locked = Path.Combine(root, "hidden-inner.7z");
        RunTool(SevenZipExe, $"a -t7z -psecret -mhe=on \"{locked}\" \"{sourceZip}\"");
        var outer = Path.Combine(root, "hidden-outer.zip");
        using (var archive = ZipFile.Open(outer, ZipArchiveMode.Create)) archive.CreateEntryFromFile(locked, "inner.7z");
        var path = Path.Combine(outer, "inner.7z");
        var asked = 0;
        ArchiveService.PasswordPrompt = _ => { asked++; return "secret"; };
        var before = await Task.Run(() => ArchiveService.TryRead(path, default));
        Check("background nested header read never opens a password prompt", asked == 0
            && before?.ErrorMessage.Contains("Password", StringComparison.OrdinalIgnoreCase) == true);
        Check("nested header password is accepted for its own archive", await ArchiveService.AskPasswordAsync(path));
        var after = await Task.Run(() => ArchiveService.TryRead(path, default));
        Check("nested encrypted headers unlock after one prompt", asked == 1 && after is { ErrorMessage.Length: 0 }
            && after.Folders.Any(folder => folder.Name == "test.zip"));
    }

    private static async Task ArchiveToggleReadGenerationCheck()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        using var tree = new NestedTree((_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return new NestedListing([new NestedEntry("stale.zip", false, false, IsArchive: true)], 0, 0, false);
            }

            return new NestedListing([], 1, 0, false) { Files = [new NestedFile("stale.zip", false, 3)] };
        }) { ArchivesEnabled = true };
        tree.SetRoots([new NestedRoot(@"Q:\", "fixture", NestedFolderKind.Drive)]);
        var parent = tree.Find(@"Q:\")!;
        var loading = tree.LoadAsync(parent);
        try
        {
            await Until(() => entered.IsSet, 5000);
            tree.ArchivesEnabled = false;
        }
        finally { release.Set(); }
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        Check("an in-flight ON read cannot restore archive folders after OFF", parent.AllChildren.Length == 0
            && parent.AllFiles.Any(file => file.Name == "stale.zip") && reads >= 2);
    }

    private static async Task ArchiveOperationPromptOwnerCheck(string root, string innerZip)
    {
        if (!File.Exists(SevenZipExe)) return;
        var source = Path.Combine(root, "owner-source.txt");
        await File.WriteAllTextAsync(source, "owned password payload");
        var first = Path.Combine(root, "owner-first.zip");
        var second = Path.Combine(root, "owner-second.zip");
        var captured = Path.Combine(root, "owner-captured.zip");
        var nested = Path.Combine(root, "owner-nested.zip");
        RunTool(SevenZipExe, $"a -tzip -pfirst \"{first}\" \"{source}\"");
        RunTool(SevenZipExe, $"a -tzip -psecond \"{second}\" \"{source}\"");
        RunTool(SevenZipExe, $"a -tzip -pcaptured \"{captured}\" \"{source}\"");
        RunTool(SevenZipExe, $"a -tzip -pnested \"{nested}\" \"{innerZip}\"");
        var firstCalls = 0;
        var secondCalls = 0;
        var closedOwnerCalls = 0;
        Func<string, string?> closedOwner = _ =>
        {
            Interlocked.Increment(ref closedOwnerCalls);
            throw new InvalidOperationException("A closed window must not own another window's password prompt.");
        };
        ArchiveService.PasswordPrompt = closedOwner;
        var results = await Task.WhenAll(
            ArchiveService.ExtractForOpenAsync(Path.Combine(first, "owner-source.txt"), null, default,
                _ => { Interlocked.Increment(ref firstCalls); return "first"; }),
            ArchiveService.ExtractForOpenAsync(Path.Combine(second, "owner-source.txt"), null, default,
                _ => { Interlocked.Increment(ref secondCalls); return "second"; }));
        Check("concurrent encrypted operations each use their initiating owner's callback", firstCalls == 1 && secondCalls == 1
            && closedOwnerCalls == 0 && results.All(path => File.ReadAllText(path) == "owned password payload"));

        var captureCalls = 0;
        ArchiveService.PasswordPrompt = _ => { Interlocked.Increment(ref captureCalls); return "captured"; };
        var output = Path.Combine(root, "prompt-captured-out");
        Directory.CreateDirectory(output);
        var pending = ArchiveService.ExtractToAsync([Path.Combine(captured, "owner-source.txt")], output, null, default);
        ArchiveService.PasswordPrompt = closedOwner;
        var copied = await pending;
        Check("a dispatched operation captures its fallback before another window replaces it", captureCalls == 1 && closedOwnerCalls == 0
            && File.ReadAllText(copied[0]) == "owned password payload");

        var nestedCalls = 0;
        var nestedOpen = await ArchiveService.ExtractForOpenAsync(Path.Combine(nested, "test.zip", "folder", "file.txt"), null, default,
            _ => { Interlocked.Increment(ref nestedCalls); return "nested"; });
        Check("nested archive extraction carries its owner through native callbacks", nestedCalls == 1 && closedOwnerCalls == 0
            && File.ReadAllText(nestedOpen) == "complete bytes");
    }
}
