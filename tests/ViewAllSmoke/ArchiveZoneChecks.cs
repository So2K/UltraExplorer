using System.IO;
using System.IO.Compression;
using System.Text;
using UltraExplorer.Services.Archives;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task ArchiveZoneChecks()
    {
        Section("archive origin metadata: direct, temporary, cached and nested extraction");
        Check("origin metadata checks use the bundled archive decoder", ArchiveService.IsAvailable);
        if (!ArchiveService.IsAvailable) return;
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerArchiveZone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousMode = ArchiveService.BrowseArchives;
        ArchiveService.BrowseArchives = true;
        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string text = "owned benign text fixture; never executed";
        var origin = Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/owned-fixture.zip\r\n");
        try
        {
            var inner = Path.Combine(root, "inner.zip");
            using (var archive = ZipFile.Open(inner, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("note.txt").Open())) writer.Write(text);
            var outer = Path.Combine(root, "download.zip");
            using (var archive = ZipFile.Open(outer, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("note.txt").Open())) writer.Write(text);
                archive.CreateEntryFromFile(inner, "inner.zip");
            }
            File.WriteAllBytes(outer + ArchiveZoneHelper.StreamSuffix, origin);
            Check("fixture archive stores a real NTFS Zone.Identifier stream", File.ReadAllBytes(outer + ArchiveZoneHelper.StreamSuffix).SequenceEqual(origin));

            bool Marked(string path) => File.Exists(path) && File.Exists(path + ArchiveZoneHelper.StreamSuffix)
                && File.ReadAllBytes(path + ArchiveZoneHelper.StreamSuffix).SequenceEqual(origin);
            var virtualText = Path.Combine(outer, "note.txt");
            var direct = await ArchiveService.ExtractToAsync([virtualText], Path.Combine(root, "direct"), null, CancellationToken.None);
            Check("direct extraction retains original origin metadata and text bytes", direct.Count == 1 && Marked(direct[0]) && File.ReadAllText(direct[0]) == text);
            var opened = await ArchiveService.ExtractForOpenAsync(virtualText, null, CancellationToken.None);
            cached.Add(opened);
            Check("temporary extraction for Open retains origin metadata before any launch", Marked(opened) && File.ReadAllText(opened) == text);
            File.Delete(opened + ArchiveZoneHelper.StreamSuffix);
            var reused = await ArchiveService.ExtractForOpenAsync(virtualText, null, CancellationToken.None);
            Check("returning an existing Open cache restores its source origin mark", reused == opened && Marked(reused));
            var dragged = await ArchiveService.ExtractToTempAsync([virtualText], null, CancellationToken.None);
            foreach (var path in dragged) cached.Add(path);
            Check("drag and clipboard temporary output retain origin metadata", dragged.Count == 1 && Marked(dragged[0]));

            var nestedText = Path.Combine(outer, "inner.zip", "note.txt");
            var nested = await ArchiveService.ExtractForOpenAsync(nestedText, null, CancellationToken.None);
            cached.Add(nested);
            Check("origin metadata crosses a nested archive and its final text file", Marked(nested) && File.ReadAllText(nested) == text);
            Check("the physical nested archive itself remains marked", ArchiveService.TryLocate(nestedText, out var location)
                && Marked(location.Index.Path));

            var unmarked = Path.Combine(root, "local.zip");
            File.WriteAllBytes(unmarked, File.ReadAllBytes(outer));
            var local = await ArchiveService.ExtractToAsync([Path.Combine(unmarked, "note.txt")], Path.Combine(root, "local"), null, CancellationToken.None);
            Check("an unmarked local archive does not acquire an invented Internet origin", local.Count == 1
                && !File.Exists(local[0] + ArchiveZoneHelper.StreamSuffix) && File.ReadAllText(local[0]) == text);
            var lateNestedPath = Path.Combine(unmarked, "inner.zip", "note.txt");
            var beforeMark = await ArchiveService.ExtractForOpenAsync(lateNestedPath, null, CancellationToken.None);
            cached.Add(beforeMark);
            Check("the initially local nested cache has no invented mark", !File.Exists(beforeMark + ArchiveZoneHelper.StreamSuffix));
            var localWriteTime = File.GetLastWriteTimeUtc(unmarked);
            File.WriteAllBytes(unmarked + ArchiveZoneHelper.StreamSuffix, origin);
            File.SetLastWriteTimeUtc(unmarked, localWriteTime);
            var afterMark = await ArchiveService.ExtractForOpenAsync(lateNestedPath, null, CancellationToken.None);
            Check("a mark added later crosses reused nested and Open caches", afterMark == beforeMark && Marked(afterMark));

            var stronger = Path.Combine(root, "stronger.txt");
            File.WriteAllText(stronger, text);
            var restricted = Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=4\r\n");
            File.WriteAllBytes(stronger + ArchiveZoneHelper.StreamSuffix, restricted);
            ArchiveZoneHelper.Propagate(outer, stronger);
            Check("propagation never weakens an existing Restricted origin", File.ReadAllBytes(stronger + ArchiveZoneHelper.StreamSuffix).SequenceEqual(restricted));
            var unicodeRestricted = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[ZoneTransfer]\r\nZoneId=4\r\n")).ToArray();
            File.WriteAllBytes(stronger + ArchiveZoneHelper.StreamSuffix, unicodeRestricted);
            ArchiveZoneHelper.Propagate(outer, stronger);
            Check("a UTF-16 BOM Restricted origin also stays intact", File.ReadAllBytes(stronger + ArchiveZoneHelper.StreamSuffix).SequenceEqual(unicodeRestricted));
            var preservedTime = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(direct[0], preservedTime);
            ArchiveZoneHelper.Propagate(outer, direct[0]);
            Check("origin propagation preserves the target's last-write cache identity", File.GetLastWriteTimeUtc(direct[0]) == preservedTime);
            Check("origin propagation leaves the downloaded archive and its stream unchanged",
                File.ReadAllBytes(outer + ArchiveZoneHelper.StreamSuffix).SequenceEqual(origin));

            var failedOutput = Path.Combine(root, "fresh-failure.txt");
            var callback = new ExtractCallback(new() { [0] = (failedOutput, 0, 0) }, () => null,
                new ArchiveService.ProgressSum(null, 0), CancellationToken.None, ArchiveZoneHelper.Capture(outer));
            Check("the failure fixture creates only a fresh owned text output", callback.GetStream(0, out _, 0) == 0 && File.Exists(failedOutput));
            File.SetAttributes(failedOutput, File.GetAttributes(failedOutput) | FileAttributes.ReadOnly);
            callback.SetOperationResult(0);
            Check("a known-origin write failure rejects and deletes the newly generated unmarked output",
                callback.Failed > 0 && !File.Exists(failedOutput)
                && callback.FirstProblem.Contains("Windows origin metadata", StringComparison.Ordinal));
        }
        finally
        {
            ArchiveService.BrowseArchives = previousMode;
            foreach (var path in cached) { try { File.Delete(path); } catch (IOException) { } }
            TryDelete(root);
        }
    }
}
