using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;

namespace ViewAllSmoke;

/// <summary>
/// Archives as folders, against real archives made here: a zip, a 7z, a
/// tar.gz, a zip with a 7z inside it, and password-protected ones - listed
/// the way the canvas reads a folder, files taken out byte for byte, whole
/// archives unpacked without a folder in a folder, entries that try to climb
/// out of where they go kept in, an archive written to read again, and the
/// speed of it all printed.  Skipped where 7-Zip is not installed.
/// </summary>
internal static partial class Program
{
    private static async Task ArchiveChecks()
    {
        Section("archives: as folders");
        if (!ArchiveService.IsAvailable)
        {
            Console.WriteLine("  (7-Zip is not installed: archive checks skipped)");
            return;
        }

        Console.WriteLine($"  7z.dll: {SevenZipLibrary.LoadedFrom}");
        var previousMode = ArchiveService.BrowseArchives;
        ArchiveService.BrowseArchives = true;
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerArchives", Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "source");
        try
        {
            BuildArchiveSource(source);
            var zip = Path.Combine(root, "site.zip");
            ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, includeBaseDirectory: false);

            ArchivePathChecks(root, zip);
            ArchiveListingChecks(root, zip, source);
            RunOnSta("archives: going there", () => ArchiveRevealChecks(zip));
            await ArchiveExtractChecks(root, zip, source);
            await ArchiveOtherFormatChecks(root, source);
            await ArchiveChangeChecks(root);
            await ArchivePasswordChecks(root, source);
            await ArchiveSpeedChecks(root);
        }
        finally
        {
            ArchiveService.PasswordPrompt = null;
            ArchiveService.BrowseArchives = previousMode;
            TryDelete(root);
        }
    }

    private static readonly string SevenZipExe = File.Exists(Path.Combine(AppContext.BaseDirectory, "archives", "7z.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "archives", "7z.exe")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");

    private static void BuildArchiveSource(string source)
    {
        Directory.CreateDirectory(Path.Combine(source, "docs", "deep", "deeper"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "top level");
        File.WriteAllText(Path.Combine(source, "docs", "guide.md"), "# guide\n" + new string('x', 5000));
        File.WriteAllText(Path.Combine(source, "docs", "deep", "deeper", "leaf.txt"), "leaf");
        File.WriteAllText(Path.Combine(source, "Привет мир.txt"), "unicode name");
        var random = new Random(7);
        var bytes = new byte[300_000];
        random.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(source, "docs", "noise.bin"), bytes);
        File.SetLastWriteTimeUtc(Path.Combine(source, "readme.txt"), new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc));
    }

    private static void ArchivePathChecks(string root, string zip)
    {
        Section("archives: paths");
        Check("a path through an archive is inside one", ArchiveService.IsInsideArchive(Path.Combine(zip, "docs", "guide.md")));
        Check("the archive itself is a file on disk, not inside one", !ArchiveService.IsInsideArchive(zip) && ArchiveService.IsArchiveFile(zip));
        Check("a plain folder is not inside an archive", !ArchiveService.IsInsideArchive(root));
        var folderNamedLikeOne = Path.Combine(root, "backup.zip");
        Directory.CreateDirectory(folderNamedLikeOne);
        Check("a folder named like an archive is a folder", !ArchiveService.IsArchiveFile(folderNamedLikeOne) && !ArchiveService.IsInsideArchive(folderNamedLikeOne));
        Directory.Delete(folderNamedLikeOne);

        Check("an entry climbing out with .. stays inside", ArchiveIndex.Clean(@"..\..\Windows\evil.dll") == @"Windows\evil.dll");
        Check("an entry with a drive or a leading slash stays inside", ArchiveIndex.Clean("C:/x/y.txt") == @"x\y.txt" && ArchiveIndex.Clean("/etc/passwd") == @"etc\passwd");
        Check("a tar.gz's name loses both extensions", ArchiveService.StemOf(@"C:\a\site.tar.gz") == "site" && ArchiveService.StemOf(@"C:\a\x.7z.001") == "x");
        Check("a later part of a multi-part rar is not a folder of its own",
            ArchiveFormats.IsBrowsable("movie.part1.rar") && !ArchiveFormats.IsBrowsable("movie.part2.rar"));
        Check("an exe or a docx stays a file", !ArchiveFormats.IsBrowsable("setup.exe") && !ArchiveFormats.IsBrowsable("report.docx"));
    }

    private static void ArchiveListingChecks(string root, string zip, string source)
    {
        Section("archives: listing");
        var parent = NestedDirectoryReader.Read(root, CancellationToken.None);
        var entry = parent.Folders.FirstOrDefault(folder => folder.Name == "site.zip");
        Check("the folder holding an archive lists it among its folders", entry.IsArchive);
        Check("…and not among its files", parent.Files.All(file => file.Name != "site.zip") && parent.FileCount == 0);

        var top = NestedDirectoryReader.Read(zip, CancellationToken.None);
        Check("an archive reads like a folder", string.IsNullOrEmpty(top.ErrorMessage));
        Check("its folders are its folders, empty ones too",
            top.Folders.Select(folder => folder.Name).OrderBy(name => name).SequenceEqual(["docs", "empty"]));
        Check("its files are its files, a Cyrillic name too",
            top.Files.Select(file => file.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "readme.txt", "Привет мир.txt" }.OrderBy(name => name, StringComparer.Ordinal)));
        var readme = top.Files.First(file => file.Name == "readme.txt");
        Check("a file's size is its own", readme.Length == new FileInfo(Path.Combine(source, "readme.txt")).Length);
        Check("a file's date is its own, to the two seconds a zip keeps",
            Math.Abs(new DateTime(readme.ModifiedTicks, DateTimeKind.Utc).Subtract(new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc)).TotalSeconds) <= 2
            || Math.Abs(new DateTime(readme.ModifiedTicks).Subtract(new DateTime(2020, 5, 17, 10, 30, 0)).TotalHours) <= 14);

        var deep = NestedDirectoryReader.Read(Path.Combine(zip, "docs", "deep", "deeper"), CancellationToken.None);
        Check("a folder three down in an archive reads", deep.Files.Count == 1 && deep.Files[0].Name == "leaf.txt");
        var gone = NestedDirectoryReader.Read(Path.Combine(zip, "nothing-here"), CancellationToken.None);
        Check("a folder the archive does not have fails like a folder that went", gone.ErrorMessage == "No longer exists");
    }

    private static async Task ArchiveExtractChecks(string root, string zip, string source)
    {
        Section("archives: taking out");
        var into = Path.Combine(root, "into");
        Directory.CreateDirectory(into);
        var made = await ArchiveService.ExtractToAsync([Path.Combine(zip, "docs"), Path.Combine(zip, "readme.txt")], into, null, CancellationToken.None);
        Check("a folder and a file copied out come out under their own names",
            made.Count == 2 && Directory.Exists(Path.Combine(into, "docs")) && File.Exists(Path.Combine(into, "readme.txt")));
        Check("…byte for byte", SameBytes(Path.Combine(source, "docs", "noise.bin"), Path.Combine(into, "docs", "noise.bin"))
            && SameBytes(Path.Combine(source, "docs", "deep", "deeper", "leaf.txt"), Path.Combine(into, "docs", "deep", "deeper", "leaf.txt")));
        Check("…with the archive's dates",
            Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(into, "readme.txt")) - new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc)).TotalHours) <= 14);

        var again = await ArchiveService.ExtractToAsync([Path.Combine(zip, "readme.txt")], into, null, CancellationToken.None);
        Check("copied out again beside itself, it is not overwritten but numbered", again[0].EndsWith("readme (2).txt", StringComparison.Ordinal));

        var open = await ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "docs", "guide.md"), null, CancellationToken.None);
        Check("a file opened from an archive is a real file with its bytes", SameBytes(Path.Combine(source, "docs", "guide.md"), open));
        var openAgain = await ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "docs", "guide.md"), null, CancellationToken.None);
        Check("opened twice, it is taken out once", open == openAgain);

        var whole = await ArchiveService.ExtractArchiveAsync(zip, root, alwaysOwnFolder: false, null, CancellationToken.None);
        Check("a zip of several things unpacks into a folder named after it", whole == Path.Combine(root, "site") && File.Exists(Path.Combine(whole, "Привет мир.txt")));
        Check("…holding every folder, empty ones too", Directory.Exists(Path.Combine(whole, "empty")) && Directory.Exists(Path.Combine(whole, "docs", "deep", "deeper")));

        var single = Path.Combine(root, "one.zip");
        using (var archive = ZipFile.Open(single, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(source, "docs", "guide.md"), "project/guide.md");
            archive.CreateEntryFromFile(Path.Combine(source, "readme.txt"), "project/readme.txt");
        }

        var smart = await ArchiveService.ExtractArchiveAsync(single, root, alwaysOwnFolder: false, null, CancellationToken.None);
        Check("a zip of one folder unpacks as that folder, not a folder in a folder", smart == Path.Combine(root, "project") && File.Exists(Path.Combine(smart, "readme.txt")));

        var temp = await ArchiveService.ExtractToTempAsync([Path.Combine(zip, "readme.txt")], null, CancellationToken.None);
        Check("what a drag carries out of an archive is a real file", File.Exists(temp[0]) && temp[0].StartsWith(ArchiveService.TempRoot, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ArchiveOtherFormatChecks(string root, string source)
    {
        Section("archives: other formats, one inside another");
        if (!File.Exists(SevenZipExe))
        {
            Console.WriteLine("  (7z.exe not found: 7z and tar checks skipped)");
            return;
        }

        var sevenZip = Path.Combine(root, "pack.7z");
        RunTool(SevenZipExe, $"a -t7z -mx=5 \"{sevenZip}\" \"{source}\\*\"");
        var listed = NestedDirectoryReader.Read(Path.Combine(sevenZip, "docs"), CancellationToken.None);
        Check("a 7z reads like a folder", listed.Files.Any(file => file.Name == "noise.bin") && listed.Folders.Any(folder => folder.Name == "deep"));
        var out7 = Path.Combine(root, "out7");
        Directory.CreateDirectory(out7);
        await ArchiveService.ExtractToAsync([Path.Combine(sevenZip, "docs")], out7, null, CancellationToken.None);
        Check("a solid 7z gives its files back byte for byte", SameBytes(Path.Combine(source, "docs", "noise.bin"), Path.Combine(out7, "docs", "noise.bin")));

        var tarGz = Path.Combine(root, "src.tar.gz");
        var tar = Path.Combine(root, "src.tar");
        RunTool(SevenZipExe, $"a -ttar \"{tar}\" \"{source}\\*\"");
        RunTool(SevenZipExe, $"a -tgzip \"{tarGz}\" \"{tar}\"");
        File.Delete(tar);
        var gz = NestedDirectoryReader.Read(tarGz, CancellationToken.None);
        Check("a tar.gz holds its tar, shown as a folder too", gz.Folders.Any(folder => folder.Name == "src.tar" && folder.IsArchive));
        var throughTar = NestedDirectoryReader.Read(Path.Combine(tarGz, "src.tar", "docs", "deep"), CancellationToken.None);
        Check("…and through the tar, its folders", throughTar.Folders.Any(folder => folder.Name == "deeper"));

        var outer = Path.Combine(root, "outer.zip");
        using (var archive = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sevenZip, "bundle/pack.7z");
        }

        var nested = NestedDirectoryReader.Read(Path.Combine(outer, "bundle", "pack.7z", "docs", "deep", "deeper"), CancellationToken.None);
        Check("a 7z inside a zip is gone into like any folder", nested.Files.Count == 1 && nested.Files[0].Name == "leaf.txt");
        var nestedOpen = await ArchiveService.ExtractForOpenAsync(Path.Combine(outer, "bundle", "pack.7z", "readme.txt"), null, CancellationToken.None);
        Check("…and a file in it opens", File.ReadAllText(nestedOpen) == "top level");
    }

    private static async Task ArchiveChangeChecks(string root)
    {
        Section("archives: written to");
        var changing = Path.Combine(root, "changing.zip");
        using (var archive = ZipFile.Open(changing, ZipArchiveMode.Create))
        {
            archive.CreateEntry("first.txt");
        }

        var before = NestedDirectoryReader.Read(changing, CancellationToken.None);
        await Task.Delay(50);
        using (var archive = ZipFile.Open(changing, ZipArchiveMode.Update))
        {
            archive.CreateEntry("second.txt");
        }

        File.SetLastWriteTimeUtc(changing, DateTime.UtcNow.AddSeconds(5));
        var after = NestedDirectoryReader.Read(changing, CancellationToken.None);
        Check("an archive written to is read again, not from what was kept", before.Files.Count == 1 && after.Files.Count == 2);
    }

    private static async Task ArchivePasswordChecks(string root, string source)
    {
        Section("archives: passwords");
        if (!File.Exists(SevenZipExe))
        {
            return;
        }

        var locked = Path.Combine(root, "locked.zip");
        RunTool(SevenZipExe, $"a -tzip -psecret \"{locked}\" \"{Path.Combine(source, "readme.txt")}\"");
        var listed = NestedDirectoryReader.Read(locked, CancellationToken.None);
        Check("a zip with a password still lists - only its contents are locked", listed.Files.Any(file => file.Name == "readme.txt"));

        var asked = 0;
        ArchiveService.PasswordPrompt = _ => { asked++; return "secret"; };
        var opened = await ArchiveService.ExtractForOpenAsync(Path.Combine(locked, "readme.txt"), null, CancellationToken.None);
        Check("with the right password its file comes out, asked for once", File.ReadAllText(opened) == "top level" && asked == 1);

        var wrong = Path.Combine(root, "wrong.zip");
        RunTool(SevenZipExe, $"a -tzip -psecret \"{wrong}\" \"{Path.Combine(source, "readme.txt")}\"");
        ArchiveService.PasswordPrompt = _ => "nope";
        var failed = false;
        try
        {
            await ArchiveService.ExtractForOpenAsync(Path.Combine(wrong, "readme.txt"), null, CancellationToken.None);
        }
        catch (ArchivePasswordException)
        {
            failed = true;
        }

        Check("with a wrong one it says so, and leaves no half-written file", failed);

        var hidden = Path.Combine(root, "hidden.7z");
        RunTool(SevenZipExe, $"a -t7z -psecret -mhe=on \"{hidden}\" \"{Path.Combine(source, "readme.txt")}\"");
        ArchiveService.PasswordPrompt = null;
        var refused = NestedDirectoryReader.Read(hidden, CancellationToken.None);
        Check("a 7z whose names are encrypted asks nobody while reading, and says why it is closed", refused.ErrorMessage.Contains("Password", StringComparison.OrdinalIgnoreCase));
        ArchiveService.PasswordPrompt = _ => "secret";
        ArchiveService.AskPassword(hidden);
        var unlocked = NestedDirectoryReader.Read(hidden, CancellationToken.None);
        Check("…and lists once given the password", unlocked.Files.Any(file => file.Name == "readme.txt"));
    }

    private static async Task ArchiveSpeedChecks(string root)
    {
        Section("archives: speed");
        var many = Path.Combine(root, "many");
        Directory.CreateDirectory(many);
        var random = new Random(11);
        var text = string.Join(' ', Enumerable.Range(0, 4000).Select(index => $"word{random.Next(5000)}"));
        for (var folder = 0; folder < 40; folder++)
        {
            var dir = Path.Combine(many, $"folder{folder:D2}");
            Directory.CreateDirectory(dir);
            for (var file = 0; file < 100; file++)
            {
                File.WriteAllText(Path.Combine(dir, $"file{file:D3}.txt"), text);
            }
        }

        var big = Path.Combine(root, "many.zip");
        ZipFile.CreateFromDirectory(many, big, CompressionLevel.Optimal, includeBaseDirectory: false);
        var unpacked = Directory.EnumerateFiles(many, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

        var watch = Stopwatch.StartNew();
        var top = NestedDirectoryReader.Read(big, CancellationToken.None);
        var listMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        _ = NestedDirectoryReader.Read(Path.Combine(big, "folder17"), CancellationToken.None);
        var againMs = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  listing {top.Folders.Count} folders / 4,000 files: {listMs:N1} ms first, {againMs:N2} ms for a folder after");
        Check("a 4,000-file zip lists in well under a second", listMs < 1000 && top.Folders.Count == 40);
        Check("going into another folder of it opens nothing again", againMs < 20);

        var outFolder = Path.Combine(root, "many-out");
        watch.Restart();
        var made = await ArchiveService.ExtractArchiveAsync(big, root, alwaysOwnFolder: true, null, CancellationToken.None);
        var seconds = watch.Elapsed.TotalSeconds;
        var count = Directory.EnumerateFiles(made, "*", SearchOption.AllDirectories).Count();
        Console.WriteLine($"  unpacking {unpacked / 1048576.0:N0} MB in 4,000 files: {seconds * 1000:N0} ms ({unpacked / 1048576.0 / seconds:N0} MB/s)");
        Check("every one of the 4,000 files comes out", count == 4000);
        Check("…with the same bytes", SameBytes(Path.Combine(many, "folder33", "file042.txt"), Path.Combine(made, "folder33", "file042.txt")));

        if (File.Exists(SevenZipExe))
        {
            var compare = Path.Combine(root, "by-7z");
            watch.Restart();
            RunTool(SevenZipExe, $"x -y -o\"{compare}\" \"{big}\"");
            Console.WriteLine($"  the same with 7z.exe x: {watch.ElapsedMilliseconds:N0} ms");
        }
    }

    /// <summary>A folder inside an archive is gone to by its path, as any folder is: the tree reads its way there.</summary>
    private static async Task ArchiveRevealChecks(string zip)
    {
        Section("archives: going there");
        using var tree = new NestedTree { ArchivesEnabled = true };
        var drive = Path.GetPathRoot(zip)!;
        tree.SetRoots([new NestedRoot(drive, drive.TrimEnd('\\'), UltraExplorer.Models.NestedFolderKind.Drive)]);
        var target = Path.Combine(zip, "docs", "deep", "deeper");
        var folder = await tree.MaterializePathAsync(target);
        Check("a folder three down inside a zip is found by its path", folder is not null && UltraExplorer.Models.ViewAllPath.Equals(folder.FullPath, target));
        Check("…with the zip on the way an archive cell", folder?.Parent?.Parent?.Parent is { IsArchive: true });
    }

    private static bool SameBytes(string left, string right) =>
        File.Exists(left) && File.Exists(right) && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));

    private static void RunTool(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
}
