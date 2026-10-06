using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// J083: a folder opened under a parent that holds little else is framed
/// with the parent's title band across the top edge of the view - its name
/// cut off above the edge - and the trail, which named only folders whose
/// whole title band was above the view, left the parent out: its name was
/// nowhere on screen.  A folder whose title is mostly above the view is in
/// the trail now; one whose title can be read is not.
/// </summary>
internal static partial class Program
{
    private static Task TrailParentChecks()
    {
        RunOnSta("trail: the parent whose title is cut off", TrailParentAsync);
        return Task.CompletedTask;
    }

    private static async Task TrailParentAsync()
    {
        Section("trail: a parent whose title is cut off by the top of the view is named in the trail (J083)");
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\Top\target", 30, "t");
        disk.AddFiles(@"Q:\Top\target\inner", 4, "i");
        disk.AddFiles(@"Q:\Other", 3, "o");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            var top = tree.Find(@"Q:\Top")!;
            var target = tree.Find(@"Q:\Top\target")!;

            // The landings a folder open makes, and where each leaves the
            // parent's title band.
            var cutOff = 0;
            var cutOffNamed = 0;
            var readable = 0;
            var readableNamed = 0;
            foreach (var fill in (ReadOnlySpan<double>)[0.80, 0.85, 0.88, 0.90, 0.92, 0.94, 0.96, 0.98])
            {
                canvas.FlyTo(target, fill, animated: false);
                canvas.RenderNow();
                var cell = canvas.ScreenRectOf(top)!.Value;
                var header = cell.Width * NestedLayout.HeaderHeight;
                var named = canvas.TrailForTests.Contains(top);
                if (cell.Y + header / 2 < 0 && cell.Y + header > 0)
                {
                    cutOff++;
                    cutOffNamed += named ? 1 : 0;
                }
                else if (cell.Y + header / 2 >= 0)
                {
                    readable++;
                    readableNamed += named ? 1 : 0;
                }

                Console.WriteLine($"  note  fill {fill:F2}: the parent's title band from {cell.Y:F1} to {cell.Y + header:F1}, in the trail: {named}");
            }

            Check($"a landing that cuts the parent's title off at the top edge names the parent in the trail ({cutOffNamed} of {cutOff})",
                cutOff > 0 && cutOffNamed == cutOff);
            Check($"and one that leaves the parent's title readable does not ({readableNamed} of {readable})",
                readableNamed == 0);

            // Whole title bands above the view are named as they always were,
            // and the trail stops at the folder whose title is on screen.
            canvas.FlyTo(tree.Find(@"Q:\Top\target\inner")!, 0.92, animated: false);
            canvas.RenderNow();
            var trail = canvas.TrailForTests.Select(folder => folder.Name).ToList();
            Check($"deeper in, the trail names the folders above the view ({string.Join(" > ", trail)})",
                trail.Contains("Q:") && trail.Contains("Top") && !trail.Contains("inner"));
        }
        finally
        {
            canvas.Tree = null;
        }
    }
}
