using System.IO;
using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The canvas rules that are not about reading the disk: what hiding a branch
/// does to the space it occupied, and what turns a dragged file into a file
/// operation instead of a layout move.
/// </summary>
internal static partial class Program
{
    private static async Task HiddenBranches(string root)
    {
        Section("hidden branches");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);
        var alphaBounds = alpha.Bounds;
        var childBounds = alpha.Children.Select(child => child.Bounds).ToArray();

        Check("a folder starts visible", alpha.IsTreeVisible && !alpha.IsUserHidden);
        Check("its space is taken", graph.Index.IsOccupied(alphaBounds));

        graph.Hide(alpha);
        Check("hiding takes the folder off the canvas", !alpha.IsTreeVisible && alpha.IsUserHidden);
        Check("and everything under it", alpha.Children.All(child => !child.IsTreeVisible));

        // The point of hiding is the room, not just the pixels.
        Check("the space it took is free again", !graph.Index.IsOccupied(alphaBounds));
        Check("so is the space its children took",
            childBounds.All(bounds => !graph.Index.IsOccupied(bounds)));
        Check("it is listed as hidden",
            graph.HiddenPaths.Contains(alpha.FullPath, StringComparer.OrdinalIgnoreCase));

        // Dragging the parent offsets every descendant, hidden ones included.
        node.Location = new Point(node.Location.X + 700, node.Location.Y + 400);
        Check("dragging an ancestor does not put a hidden branch back in the way",
            !graph.Index.IsOccupied(alpha.Bounds));

        graph.CollapseAll();
        await graph.ExpandAsync(node);
        await graph.ExpandAsync(alpha);
        Check("re-expanding the parent does not un-hide it", !alpha.IsTreeVisible);

        await graph.RefreshBranchAsync(node);
        var alphaAfterRefresh = node.Children.First(child => child.DisplayName == "alpha");
        Check("a refresh does not un-hide it",
            alphaAfterRefresh.IsUserHidden && !alphaAfterRefresh.IsTreeVisible);

        // ---- across a restart -------------------------------------------------
        var saved = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
        Check("the hide is written to the workspace",
            saved.HiddenPaths.Contains(alpha.FullPath, StringComparer.OrdinalIgnoreCase));

        using var reopened = new ViewAllGraphService();
        await reopened.InitializeAsync(saved);
        var reopenedRoot = (await reopened.AddRootAsync(root))!;
        await reopened.ExpandAsync(reopenedRoot);
        var reopenedAlpha = reopenedRoot.Children.FirstOrDefault(child => child.DisplayName == "alpha");
        Check("a hidden folder comes back hidden", reopenedAlpha is { IsUserHidden: true, IsTreeVisible: false });
        Check("and takes no space", reopenedAlpha is null || !reopened.Index.IsOccupied(reopenedAlpha.Bounds));
        Check("its siblings are laid out as if it were not there",
            reopenedRoot.Children.Where(child => !child.IsUserHidden).All(child => child.HasLayoutPosition));

        // ---- and back again ---------------------------------------------------
        Check("showing it again works", reopened.Show(reopenedAlpha!.FullPath));
        Check("it is visible", reopenedAlpha.IsTreeVisible && !reopenedAlpha.IsUserHidden);
        Check("nothing is left in the hidden list", reopened.HiddenPaths.Count == 0);

        var placed = reopened.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
        var overlapping = false;
        for (var i = 0; i < placed.Length && !overlapping; i++)
        {
            for (var j = i + 1; j < placed.Length; j++)
            {
                if (placed[i].Bounds.IntersectsWith(placed[j].Bounds))
                {
                    overlapping = true;
                    break;
                }
            }
        }

        Check("it does not come back on top of anything", !overlapping);
        Check("showing something that is not hidden is a no-op", !reopened.Show(root));
    }

    private static async Task TidyLayout(string root)
    {
        Section("tidy the layout");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var wide = node.Children.First(child => child.DisplayName == "wide");
        await graph.ExpandAsync(wide);

        // One node dragged far away is what every later expansion has to route
        // around, and what a tidy is for.
        var stray = node.Children.First(child => child.DisplayName == "beta");
        stray.Location = new Point(9000, 9000);
        Check("a dragged node is an anchor", stray.HasManualPosition);

        graph.Relayout();

        Check("tidying drops every hand-placed position",
            graph.Nodes.All(item => !item.HasManualPosition));
        Check("every visible node is placed again",
            graph.Nodes.Where(item => item.IsTreeVisible).All(item => item.HasLayoutPosition));
        Check("the stray came home",
            Math.Abs(stray.Location.X - 9000) > 1000 || Math.Abs(stray.Location.Y - 9000) > 1000);
        Check("children are still below their parent",
            node.Children.All(child => child.Location.Y > node.Location.Y));

        var placed = graph.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
        var overlapping = false;
        for (var i = 0; i < placed.Length && !overlapping; i++)
        {
            for (var j = i + 1; j < placed.Length; j++)
            {
                if (placed[i].Bounds.IntersectsWith(placed[j].Bounds))
                {
                    overlapping = true;
                    break;
                }
            }
        }

        Check("and nothing overlaps afterwards", !overlapping);
    }

    private static Task ProgramTargets()
    {
        Section("dropping onto a program");

        Check("an exe is a program", NativeShellService.IsExecutable(@"C:\Windows\notepad.exe"));
        Check("so is a shortcut", NativeShellService.IsExecutable(@"C:\x\thing.lnk"));
        Check("and a batch file", NativeShellService.IsExecutable(@"C:\x\build.cmd"));
        Check("a document is not", !NativeShellService.IsExecutable(@"C:\x\notes.txt"));
        Check("neither is something with no extension", !NativeShellService.IsExecutable(@"C:\x\LICENSE"));

        // A path ending in a backslash is the case a naive pair of quotes eats:
        // the backslash escapes the closing quote and the argument runs on.
        Check("a plain name needs no quotes",
            NativeShellService.BuildCommandLine(["notes.txt"]) == "notes.txt");
        Check("a name with a space is quoted",
            NativeShellService.BuildCommandLine([@"C:\my files\notes.txt"]) == "\"C:\\my files\\notes.txt\"");
        Check("a trailing backslash cannot escape the closing quote",
            NativeShellService.BuildCommandLine([@"C:\my files\"]) == "\"C:\\my files\\\\\"");
        Check("several arguments are separated by one space",
            NativeShellService.BuildCommandLine(["a.txt", "b.txt"]) == "a.txt b.txt");
        Check("an embedded quote is escaped",
            NativeShellService.BuildCommandLine(["say \"hi\""]) == "\"say \\\"hi\\\"\"");

        return Task.CompletedTask;
    }
}
