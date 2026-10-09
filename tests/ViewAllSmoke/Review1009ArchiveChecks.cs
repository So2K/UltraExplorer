using System.IO;
using System.IO.Compression;
using System.Text;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;

namespace ViewAllSmoke;

/// <summary>
/// The 2026-10-09 review's archive findings: an edited Open copy is never
/// overwritten or served again as the archive's (H1), extraction to a volume
/// without named streams keeps its files (M6), a destination behind a link
/// chosen by the user works (M7), names made equal by cleaning and device
/// names survive (L5), and sibling extraction is bounded while a cancelled
/// or concurrent extraction leaves nothing half made (L6).
/// </summary>
internal static partial class Program
{
    private static async Task Review1009ArchiveChecks()
    {
        Section("review 2026-10-09: archives");
        Check("review archive checks use the bundled archive decoder", ArchiveService.IsAvailable);
        if (!ArchiveService.IsAvailable) return;
        var previousMode = ArchiveService.BrowseArchives;
        ArchiveService.BrowseArchives = true;
        var root = Path.Combine(Path.GetTempPath(), "UE1009", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            await Review1009OpenCopyChecks(root);
            await Review1009ZoneChecks(root);
            await Review1009LinkedDestinationChecks(root);
            await Review1009NameChecks(root);
            await Review1009StagingChecks(root);
        }
        finally
        {
            ArchiveZoneHelper.NamedStreamsOverride = null;
            ArchiveService.BrowseArchives = previousMode;
            TryDelete(root);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, level).Open());
        writer.Write(text);
    }

    private static async Task Review1009OpenCopyChecks(string root)
    {
        var zip = Path.Combine(root, "open.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "doc.txt", "original");
            WriteEntry(archive, "tool.exe", "owned inert bytes, never run");
            WriteEntry(archive, "tool.cfg", "config");
        }

        var doc = Path.Combine(zip, "doc.txt");
        var first = await ArchiveService.ExtractForOpenAsync(doc, null, default);
        Check("H1: an untouched Open copy is reused", await ArchiveService.ExtractForOpenAsync(doc, null, default) == first);
        File.WriteAllText(first, "edited by the user, now longer");
        var second = await ArchiveService.ExtractForOpenAsync(doc, null, default);
        Check("H1: reopening after a size-changing edit keeps the user's edit", File.ReadAllText(first) == "edited by the user, now longer");
        Check("H1: …and opens the archive's own bytes from a fresh copy", second != first && File.ReadAllText(second) == "original");

        File.WriteAllText(second, "ORIGINAL");
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(second).AddMinutes(1));
        var third = await ArchiveService.ExtractForOpenAsync(doc, null, default);
        Check("H1: a same-size edit is never served as the archive's content", third != second && File.ReadAllText(third) == "original"
            && File.ReadAllText(second) == "ORIGINAL");

        var tool = await ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "tool.exe"), null, default);
        Check("H1: a program opened after an edit does not overwrite the edited sibling", File.ReadAllText(first) == "edited by the user, now longer"
            && Path.GetDirectoryName(tool) != Path.GetDirectoryName(first));
        var cfg = Path.Combine(Path.GetDirectoryName(tool)!, "tool.cfg");
        Check("H1: …and comes out with its own siblings", File.ReadAllText(cfg) == "config" && File.ReadAllText(Path.Combine(Path.GetDirectoryName(tool)!, "doc.txt")) == "original");
        File.WriteAllText(cfg, "edited config");
        var toolAgain = await ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "tool.exe"), null, default);
        Check("H1: reopening the program keeps its edited sibling", toolAgain == tool && File.ReadAllText(cfg) == "edited config");

        // A sibling declared above the 512 MiB automatic cap: the program comes out alone.
        var big = Path.Combine(root, "big.zip");
        using (var archive = ZipFile.Open(big, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "setup.exe", "owned inert bytes, never run", CompressionLevel.NoCompression);
            WriteEntry(archive, "payload.bin", "small", CompressionLevel.NoCompression);
        }

        var bytes = File.ReadAllBytes(big);
        var name = Encoding.ASCII.GetBytes("payload.bin");
        for (var at = 0; at + 46 + name.Length <= bytes.Length; at++)
        {
            if (bytes[at] != 0x50 || bytes[at + 1] != 0x4b) continue;
            var (offset, nameAt) = bytes[at + 2] == 3 && bytes[at + 3] == 4 ? (22, 30)
                : bytes[at + 2] == 1 && bytes[at + 3] == 2 ? (24, 46) : (-1, 0);
            if (offset >= 0 && bytes.AsSpan(at + nameAt, name.Length).SequenceEqual(name))
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + offset, 4), 600u << 20);
        }

        File.WriteAllBytes(big, bytes);
        var setup = await ArchiveService.ExtractForOpenAsync(Path.Combine(big, "setup.exe"), null, default);
        Check("L6: a program beside more than 512 MiB comes out without its siblings", File.ReadAllText(setup) == "owned inert bytes, never run"
            && !File.Exists(Path.Combine(Path.GetDirectoryName(setup)!, "payload.bin")));
    }

    private static async Task Review1009ZoneChecks(string root)
    {
        var zip = Path.Combine(root, "marked.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) WriteEntry(archive, "note.txt", "marked text");
        File.WriteAllBytes(zip + ArchiveZoneHelper.StreamSuffix, Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"));
        Check("M6: an NTFS temporary folder reports named streams", ArchiveZoneHelper.SupportsNamedStreams(root));
        ArchiveZoneHelper.NamedStreamsOverride = _ => false;
        try
        {
            var made = await ArchiveService.ExtractToAsync([Path.Combine(zip, "note.txt")], Path.Combine(root, "fat"), null, default);
            Check("M6: a marked archive extracts to a volume without named streams and keeps its files",
                made.Count == 1 && File.ReadAllText(made[0]) == "marked text" && !File.Exists(made[0] + ArchiveZoneHelper.StreamSuffix));
        }
        finally
        {
            ArchiveZoneHelper.NamedStreamsOverride = null;
        }

        var ntfs = await ArchiveService.ExtractToAsync([Path.Combine(zip, "note.txt")], Path.Combine(root, "ntfs"), null, default);
        Check("M6: on NTFS the mark is still written", ntfs.Count == 1 && File.Exists(ntfs[0] + ArchiveZoneHelper.StreamSuffix));
    }

    private static async Task Review1009LinkedDestinationChecks(string root)
    {
        var zip = Path.Combine(root, "linked.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "a/one.txt", "one");
            WriteEntry(archive, "two.txt", "two");
        }

        var real = Path.Combine(root, "real-work");
        Directory.CreateDirectory(real);
        var work = Path.Combine(root, "work-link");
        RunTool("cmd.exe", $"/d /c mklink /J \"{work}\" \"{real}\"");
        Check("M7: owned junction fixture exists", new DirectoryInfo(work).LinkTarget is not null);
        var below = Path.Combine(work, "projects");
        var made = await ArchiveService.ExtractToAsync([Path.Combine(zip, "two.txt")], below, null, default);
        Check("M7: copying out into a folder below a user junction works", made.Count == 1
            && File.ReadAllText(Path.Combine(real, "projects", "two.txt")) == "two");
        var whole = await ArchiveService.ExtractArchiveAsync(zip, below, alwaysOwnFolder: true, null, default);
        Check("M7: unpacking a whole archive below a user junction works", File.ReadAllText(Path.Combine(whole, "a", "one.txt")) == "one");
        var opened = await ArchiveService.ExtractForOpenAsync(Path.Combine(zip, "a", "one.txt"), null, default);
        Check("M7: Open from an archive still works", File.ReadAllText(opened) == "one");

        var planted = Path.Combine(real, "planted");
        RunTool("cmd.exe", $"/d /c mklink /J \"{planted}\" \"{root}\"");
        var refused = false;
        try { ArchiveService.EnsureSafeOutput(Path.Combine(work, "planted", "x.txt"), work); }
        catch (IOException) { refused = true; }
        Check("M7: a link inside the extraction root is still refused", refused);
        var allowed = true;
        try { ArchiveService.EnsureSafeOutput(Path.Combine(below, "x", "y.txt"), below); }
        catch (IOException) { allowed = false; }
        Check("M7: a link above the extraction root is not", allowed);
    }

    private static async Task Review1009NameChecks(string root)
    {
        Check("L5: device names get a leading underscore", ArchiveIndex.Clean("CON") == "_CON" && ArchiveIndex.Clean(@"x/lpt1.log") == @"x\_lpt1.log"
            && ArchiveIndex.Clean("nul.tar.gz") == "_nul.tar.gz" && ArchiveIndex.Clean("Com9") == "_Com9");
        Check("L5: names that only start like one stay", ArchiveIndex.Clean("CONSOLE.txt") == "CONSOLE.txt" && ArchiveIndex.Clean("COM0") == "COM0"
            && ArchiveIndex.Clean("auxiliary") == "auxiliary");

        var zip = Path.Combine(root, "names.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "report.", "dot");
            WriteEntry(archive, "report", "plain");
            WriteEntry(archive, "a:b.txt", "colon");
            WriteEntry(archive, "a_b.txt", "underscore");
            WriteEntry(archive, "CON.txt", "device");
            WriteEntry(archive, "dup.txt", "first");
            WriteEntry(archive, "dup.txt", "second");
        }

        var listing = NestedDirectoryReader.Read(zip, default, true);
        var names = listing.Files.Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
        Check("L5: names made equal by cleaning are all listed, numbered", names.SequenceEqual(
            new[] { "_CON.txt", "a_b (2).txt", "a_b.txt", "dup.txt", "report", "report (2)" }.Order(StringComparer.Ordinal)));
        var into = Path.Combine(root, "names-out");
        var whole = await ArchiveService.ExtractArchiveAsync(zip, into, alwaysOwnFolder: true, null, default);
        var texts = Directory.GetFiles(whole).Select(File.ReadAllText).Order(StringComparer.Ordinal).ToArray();
        Check("L5: every one of them extracts with its own bytes", texts.SequenceEqual(
            new[] { "colon", "device", "dot", "plain", "second", "underscore" }));
        Check("L5: an identical name twice still keeps the later one", File.ReadAllText(Path.Combine(whole, "dup.txt")) == "second");
    }

    private sealed class CancelOnProgress(CancellationTokenSource source) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value)
        {
            if (value.Done > 0) source.Cancel();
        }
    }

    private static async Task Review1009StagingChecks(string root)
    {
        var zip = Path.Combine(root, "staging.zip");
        var random = new Random(9);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            for (var number = 0; number < 6; number++)
            {
                var bytes = new byte[4 << 20];
                random.NextBytes(bytes);
                using var stream = archive.CreateEntry($"part{number}.bin", CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }

        var cancelled = Path.Combine(root, "cancelled");
        Directory.CreateDirectory(cancelled);
        using (var source = new CancellationTokenSource())
        {
            var stopped = false;
            try { await ArchiveService.ExtractArchiveAsync(zip, cancelled, alwaysOwnFolder: true, new CancelOnProgress(source), source.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check("L6: a cancelled unpack stops", stopped);
        }

        Check("L6: …and leaves no partial folder or staging behind", Directory.GetFileSystemEntries(cancelled).Length == 0);

        using (var source = new CancellationTokenSource())
        {
            var stopped = false;
            try { await ArchiveService.ExtractToAsync([Path.Combine(zip, "part1.bin"), Path.Combine(zip, "part2.bin")], cancelled, new CancelOnProgress(source), source.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check("L6: a cancelled copy-out leaves nothing behind", stopped && Directory.GetFileSystemEntries(cancelled).Length == 0);
        }

        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ArchiveService.ExtractArchiveAsync(zip, shared, alwaysOwnFolder: true, null, default)));
        Check("L6: concurrent unpacks into one folder each get their own name", results.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4
            && results.All(result => Directory.GetFiles(result).Length == 6));
        Check("L6: …and no staging folder remains", Directory.GetDirectories(shared).Length == 4);
    }
}
