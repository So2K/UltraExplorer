using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The nested canvas asks the marks for every folder it draws - nineteen
/// thousand in one frame of C:\Windows\WinSxS.  With nothing marked that must
/// be next to free, and stay so after thousands of marks came and went; it
/// was a walk over every bucket of the map and every one of its locks, which
/// a map that once held thousands of marks keeps however many are left
/// (review 2, J009).
/// </summary>
internal static partial class Program
{
    private static Task MarkLookupCostChecks()
    {
        Section("marks: a lookup costs the same however many marks there were (J009)");
        var marks = new FolderMarkService(Path.Combine(Path.GetTempPath(), "never-written-" + Guid.NewGuid().ToString("N") + ".json"));
        var drive = new NestedFolder(@"C:\", "C:", NestedFolderKind.Drive, null);
        var cells = Enumerable.Range(0, 19_000)
            .Select(index => NestedFolder.ChildOf(drive, $"cell-{index:D5}", false, false, 0))
            .ToArray();
        foreach (var cell in cells)
        {
            // Joined before anything is timed, as a drawn folder's path is.
            _ = cell.FullPath;
        }

        // The cost of one lookup in nanoseconds, the best of a few frames' worth.
        double PerLookup(out int marked)
        {
            var best = double.MaxValue;
            marked = 0;
            for (var pass = 0; pass < 7; pass++)
            {
                var found = 0;
                var watch = Stopwatch.StartNew();
                foreach (var cell in cells)
                {
                    if (!marks.Get(cell).IsEmpty)
                    {
                        found++;
                    }
                }

                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1_000_000 / cells.Length);
                marked = found;
            }

            return best;
        }

        var fresh = PerLookup(out var freshMarked);
        var coloured = Enumerable.Range(0, 20_000).Select(index => $@"C:\Marked\file-{index:D5}.bin").ToArray();
        foreach (var path in coloured)
        {
            marks.SetAccent(path, "#EF5A68");
        }

        foreach (var path in coloured)
        {
            marks.SetAccent(path, null);
        }

        var emptied = PerLookup(out var emptiedMarked);
        marks.SetAccent(cells[7].FullPath, "#3FA34D");
        var one = PerLookup(out var oneMarked);
        Console.WriteLine($"        {cells.Length:N0} cells a frame: {fresh:F0} ns a lookup with nothing ever marked, {emptied:F0} ns after 20,000 marks came and went, {one:F0} ns with one mark");
        Check($"with nothing marked, a lookup is next to free ({fresh:F0} ns, {fresh * cells.Length / 1_000_000:F2} ms a frame)",
            fresh < 100 && freshMarked == 0);
        Check($"and stays so after thousands of marks came and went ({emptied:F0} ns, {emptied * cells.Length / 1_000_000:F2} ms a frame)",
            emptied < 100 && emptiedMarked == 0);
        Check($"with one mark, a lookup is one look in the map ({one:F0} ns, {one * cells.Length / 1_000_000:F2} ms a frame)", one < 500);
        Check("and the one marked cell is the one found",
            oneMarked == 1 && marks.Get(cells[7]).AccentHex == "#3FA34D" && marks.Get(cells[8]).IsEmpty && marks.Get(cells[7].FullPath).AccentHex == "#3FA34D");
        return Task.CompletedTask;
    }
}
