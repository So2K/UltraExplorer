using System.IO;
using System.Diagnostics;
using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// Stepping through the filter's matches (J060, J138, J141): a filter typed
/// over a tree of millions of names holds the thread for a slice of time,
/// not a count of names, and Enter straight after it goes to the first match
/// at once while the rest is judged a slice at a time; a match found in a
/// folder read again after the filter was set, or read for the first time,
/// is stepped to in its place on screen, not after everything else; and the matches on a drive that went
/// are let go of, so every press of Next goes somewhere.
/// </summary>
internal static partial class Program
{
    private static Task FilterStepChecks()
    {
        RunOnSta("filter steps: a drive gone", FilterStepDriveGoneAsync);
        RunOnSta("filter steps: a match read later", FilterStepReadLaterAsync);
        RunOnSta("filter steps: Enter while judging", FilterStepWhileJudgingAsync);
        return Task.CompletedTask;
    }

    private static NestedCanvas FilterStepCanvas(NestedTree tree)
    {
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        return canvas;
    }

    // ---- J141: a drive gone -----------------------------------------------------------------

    private static async Task FilterStepDriveGoneAsync()
    {
        Section("filter steps: the matches on a drive that went are let go of, and Next always goes somewhere (J141)");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\a", "x1.log", 1);
        disk.AddFile(@"Q:\c", "x3.log", 1);
        disk.AddFile(@"R:\b", "x2.log", 1);
        disk.AddFile(@"R:\d", "x4.log", 1);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        var q = new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive);
        var r = new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive);
        tree.SetRoots([q, r]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = FilterStepCanvas(tree);
        try
        {
            canvas.SetFilter("*.log");
            var before = canvas.FilterMatches.Count;
            tree.SetRoots([q]);

            var landed = new List<string>();
            var dead = 0;
            for (var press = 0; press < 4; press++)
            {
                if (canvas.GoToMatch(1) && canvas.FilterCursor >= 0)
                {
                    landed.Add(canvas.FilterMatches[canvas.FilterCursor]);
                }
                else
                {
                    dead++;
                }
            }

            Check($"with R: gone, every press of Next lands on a match ({dead} of 4 did nothing: {string.Join(", ", landed)})",
                dead == 0 && landed.All(path => path.StartsWith(@"Q:\", StringComparison.OrdinalIgnoreCase)));
            Check($"and the count is of the matches that are there ({before} before, {canvas.FilterMatches.Count} now)",
                before == 4 && canvas.FilterMatches.Count == 2);
            Check("and the steps go round them in order",
                landed.SequenceEqual([@"Q:\a\x1.log", @"Q:\c\x3.log", @"Q:\a\x1.log", @"Q:\c\x3.log"], StringComparer.OrdinalIgnoreCase));

            // Backwards too, from the start: the last match there is.
            canvas.SetFilter(null);
            tree.SetRoots([q, r]);
            await LoadEverythingAsync(tree, _ => true);
            canvas.SetFilter("*.log");
            tree.SetRoots([q]);
            var back = canvas.GoToMatch(-1);
            var backAt = back && canvas.FilterCursor >= 0 ? canvas.FilterMatches[canvas.FilterCursor] : "(none)";
            Check($"and Previous from the start lands on the last match there is ({backAt})",
                back && string.Equals(backAt, @"Q:\c\x3.log", StringComparison.OrdinalIgnoreCase) && canvas.FilterMatches.Count == 2);

            // Nothing left at all: nothing to go to, and nothing counted.
            tree.SetRoots([r]);
            var none = canvas.GoToMatch(1);
            Check($"and with no match left anywhere, Next says so ({canvas.FilterMatches.Count} counted)",
                !none && canvas.FilterMatches.Count == 0);
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    // ---- J138: a match read later ---------------------------------------------------------------

    private static async Task FilterStepReadLaterAsync()
    {
        Section("filter steps: a match found after the filter was set is stepped to in its place (J138)");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\a", "a1.log", 1);
        disk.AddFile(@"Q:\c", "c1.log", 1);
        disk.AddFile(@"Q:\e", "e1.log", 1);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = FilterStepCanvas(tree);
        try
        {
            canvas.SetFilter("*.log");
            disk.AddFile(@"Q:\a", "a2.log", 1);
            disk.AddFile(@"Q:\c", "c0.log", 1);
            await tree.RefreshAsync(tree.Find(@"Q:\a")!);
            await tree.RefreshAsync(tree.Find(@"Q:\c")!);

            // And a folder that was not there, read for the first time.
            disk.AddFile(@"Q:\b", "b1.log", 1);
            await tree.RefreshAsync(tree.Find(@"Q:\")!);
            await tree.LoadAsync(tree.Find(@"Q:\b")!);

            var expected = new List<string>();
            CanvasMiscExpectedMatches(tree.Root, name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase), expected);

            // In place as soon as the reads are in, before any step: the
            // window's marks are made from the first of them.
            var listed = canvas.FilterMatches.ToList();
            Check($"the list the count and the marks come from has the ones read later in their places ({string.Join(", ", listed.Select(Path.GetFileName))})",
                listed.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase));

            var landed = new List<string>();
            for (var press = 0; press < expected.Count; press++)
            {
                if (canvas.GoToMatch(1) && canvas.FilterCursor >= 0)
                {
                    landed.Add(canvas.FilterMatches[canvas.FilterCursor]);
                }
            }

            Check($"Next goes through the matches in their order on screen, the ones read later in their places ({string.Join(", ", landed.Select(Path.GetFileName))})",
                landed.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase));
            Check("and the list stays in that order",
                canvas.FilterMatches.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase));

            var previous = new List<string>();
            for (var press = 0; press < expected.Count; press++)
            {
                if (canvas.GoToMatch(-1) && canvas.FilterCursor >= 0)
                {
                    previous.Add(canvas.FilterMatches[canvas.FilterCursor]);
                }
            }

            var backwards = Enumerable.Range(1, expected.Count).Select(step => expected[(expected.Count - 1 - step + expected.Count) % expected.Count]).ToList();
            Check($"and Previous goes back through them in the same order ({string.Join(", ", previous.Select(Path.GetFileName))})",
                previous.SequenceEqual(backwards, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    // ---- J060: Enter while judging ------------------------------------------------------------

    private const int FilterStepGroups = 20;
    private const int FilterStepBoxes = 50;
    private const int FilterStepFiles = 2_000;

    private static async Task FilterStepWhileJudgingAsync()
    {
        Section("filter steps: a filter over two million names is typed and stepped through without a long stall (J060)");
        var shared = Enumerable.Range(0, FilterStepFiles).Select(index => new NestedFile($"n{index:D4}.dat", false, index)).ToArray();
        var disk = new FakeDisk
        {
            Hook = (path, _) =>
            {
                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    return new NestedListing(Enumerable.Range(0, FilterStepGroups).Select(group => new NestedEntry($"g{group:D2}", false, false)).ToList(), 0, 0, false);
                }

                if (parts.Length == 2)
                {
                    return new NestedListing(Enumerable.Range(0, FilterStepBoxes).Select(box => new NestedEntry($"b{box:D2}", false, false)).ToList(), 0, 0, false);
                }

                return new NestedListing([], shared.Length, 0, false) { Files = shared };
            }
        };

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = FilterStepCanvas(tree);
        const string pattern = "*77.dat";
        static bool Matches(string name) => name.EndsWith("77.dat", StringComparison.OrdinalIgnoreCase);
        try
        {
            // The first filter in the process also builds its pattern's code.
            canvas.SetFilter(pattern);
            await CanvasMiscJudgedAsync(canvas);
            canvas.SetFilter(null);

            var names = FilterStepGroups * FilterStepBoxes * (FilterStepFiles + 1);
            var started = Stopwatch.GetTimestamp();
            canvas.SetFilter(pattern);
            var held = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var judgingAfterSet = canvas.HasPendingWork;
            Check($"typed over {names:N0} names, the filter holds the thread {held:F1} ms before it returns, the rest left to slices",
                held < 40 && judgingAfterSet);

            // Enter at once: the first match is already known.
            started = Stopwatch.GetTimestamp();
            var went = canvas.GoToMatch(1);
            var stepped = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var judgingAfterStep = canvas.HasPendingWork;
            var first = went && canvas.FilterCursor >= 0 ? canvas.FilterMatches[canvas.FilterCursor] : "(none)";
            Check($"Enter straight after goes to the first match in {stepped:F1} ms ({first}), and the judging goes on in slices ({judgingAfterStep})",
                went && canvas.FilterCursor == 0 && stepped < 40 && judgingAfterStep);

            var widest = await CanvasMiscJudgedAsync(canvas);
            var expected = new List<string>();
            CanvasMiscExpectedMatches(tree.Root, Matches, expected);
            Check($"and once it is judged the matches are the ones one pass finds, in its order ({canvas.FilterMatches.Count:N0} of {expected.Count:N0}, the thread free every {widest:F0} ms)",
                canvas.FilterMatches.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) && widest < 100);
            Check($"with the step still on the first of them ({canvas.FilterCursor})",
                canvas.FilterCursor == 0 && string.Equals(first, expected[0], StringComparison.OrdinalIgnoreCase));
            Check("and the next Enter goes on to the second",
                canvas.GoToMatch(1) && canvas.FilterCursor == 1 && string.Equals(canvas.FilterMatches[1], expected[1], StringComparison.OrdinalIgnoreCase));

            // Previous from nowhere wants the last match, which only the
            // whole judging knows: it still judges everything first.
            canvas.SetFilter(null);
            canvas.SetFilter(pattern);
            var back = canvas.GoToMatch(-1);
            Check($"while Previous from the start still goes to the very last match ({canvas.FilterCursor:N0})",
                back && canvas.FilterCursor == expected.Count - 1
                && string.Equals(canvas.FilterMatches[canvas.FilterCursor], expected[^1], StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }
}
