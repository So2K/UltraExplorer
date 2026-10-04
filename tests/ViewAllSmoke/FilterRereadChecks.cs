using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// J059: with the filter on, a folder read again had everything read below
/// it judged anew, on the UI thread and in one go - a drive's root read again
/// matched every name on the drive.  Only what the read can have changed is
/// judged now: the folder's own files and the sub-folders it did not have
/// before.  The sub-folders it kept were judged already, and so was
/// everything under them; their matches stay where they are.
/// </summary>
internal static partial class Program
{
    private static Task FilterRereadChecks()
    {
        RunOnSta("filter re-read: only what the read changed", FilterRereadShallowAsync);
        return Task.CompletedTask;
    }

    private const int FilterRereadGroups = 20;
    private const int FilterRereadBoxes = 50;
    private const int FilterRereadFiles = 2_000;

    private static async Task FilterRereadShallowAsync()
    {
        Section("filter re-read: a folder read again with the filter on judges what came in, not everything under it (J059)");
        var shared = Enumerable.Range(0, FilterRereadFiles).Select(index => new NestedFile($"n{index:D4}.dat", false, index)).ToArray();
        var changed = new ConcurrentDictionary<string, NestedFile[]>(StringComparer.OrdinalIgnoreCase);
        var removed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var added = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disk = new FakeDisk
        {
            Hook = (path, _) =>
            {
                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                var files = changed.TryGetValue(path, out var own) ? own : null;
                if (parts.Length <= 2)
                {
                    var names = parts.Length == 1
                        ? Enumerable.Range(0, FilterRereadGroups).Select(group => $"g{group:D2}")
                        : Enumerable.Range(0, FilterRereadBoxes).Select(box => $"b{box:D2}");
                    if (added.TryGetValue(path, out var extra))
                    {
                        names = names.Append(extra);
                    }

                    var entries = names
                        .Where(name => !removed.ContainsKey(Path.Combine(path, name)))
                        .Select(name => new NestedEntry(name, false, false))
                        .ToList();
                    files ??= [];
                    return new NestedListing(entries, files.Length, 0, false) { Files = files };
                }

                files ??= shared;
                return new NestedListing([], files.Length, 0, false) { Files = files };
            }
        };

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        const string pattern = "*77.dat";
        static bool Matches(string name) => name.EndsWith("77.dat", StringComparison.OrdinalIgnoreCase);
        try
        {
            canvas.SetFilter(pattern);
            await CanvasMiscJudgedAsync(canvas);
            var drive = tree.Find(@"Q:\")!;
            var matches = canvas.FilterMatches.ToList();
            var states = CanvasMiscFilterStates(tree, stamped: true);

            // ---- the drive's root read again, nothing new -----------------------------
            var started = Stopwatch.GetTimestamp();
            canvas.FolderLoadedForTests(drive);
            var rootMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var names = FilterRereadGroups * FilterRereadBoxes * (FilterRereadFiles + 1);
            Check($"the drive's root read again with {names:N0} names read under it is judged in {rootMs:F1} ms",
                rootMs < 50);
            Check($"and every match and every folder's light stay as they were ({canvas.FilterMatches.Count:N0} matches)",
                canvas.FilterMatches.SequenceEqual(matches, StringComparer.OrdinalIgnoreCase)
                && CanvasMiscSameStates(states, CanvasMiscFilterStates(tree, stamped: true)));

            // ---- real reads that change something --------------------------------------
            // A box loses its matches and gains another, a group loses a box,
            // and the drive gains a group.
            changed[@"Q:\g03\b04"] = [.. shared.Where(file => !Matches(file.Name)).Append(new NestedFile("late-77.dat", false, 1)).OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)];
            removed[@"Q:\g05\b07"] = true;
            added[@"Q:\"] = "g99";
            changed[@"Q:\"] = [.. new[] { new NestedFile("top-77.dat", false, 1), new NestedFile("top.txt", false, 1) }.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)];
            await tree.RefreshAsync(tree.Find(@"Q:\g03\b04")!);
            await tree.RefreshAsync(tree.Find(@"Q:\g05")!);
            await tree.RefreshAsync(drive);

            var expected = new List<string>();
            CanvasMiscExpectedMatches(tree.Root, Matches, expected);
            var found = canvas.FilterMatches.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check($"folders read again with files and sub-folders come and gone leave exactly the matches there are ({found.Count:N0} of {expected.Count:N0})",
                found.SetEquals(expected) && canvas.FilterMatches.Count == expected.Count);

            var afterReads = CanvasMiscFilterStates(tree);
            canvas.SetFilter(null);
            canvas.SetFilter(pattern);
            await CanvasMiscJudgedAsync(canvas);
            Check("and every folder lit or faded as a filter typed afresh judges it",
                CanvasMiscSameStates(afterReads, CanvasMiscFilterStates(tree)));

            // The group that came in is judged, and so is its own read later.
            changed[@"Q:\g99"] = [new NestedFile("new-77.dat", false, 1)];
            var newGroup = tree.Find(@"Q:\g99")!;
            await tree.LoadAsync(newGroup);
            Check("a folder that came in with a read is judged when it is read itself",
                canvas.FilterMatches.Contains(@"Q:\g99\new-77.dat", StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }
}
