using System.IO;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task FolderInvocationChecks(string fixtureRoot)
        => OnDispatcher(async () =>
        {
            var ownRoot = Path.Combine(Path.GetTempPath(), "UltraExplorerFolderInvocation-" + Guid.NewGuid().ToString("N"));
            try { await FolderInvocationChecksAsync(ownRoot); }
            finally { TryDelete(ownRoot); }
        });

    private static async Task FolderInvocationChecksAsync(string fixtureRoot)
    {
        Section("normal folder launches and complete reveal selections");
        var scratch = Path.Combine(fixtureRoot, "folder-invocation-state");
        var folder = Path.Combine(fixtureRoot, "launch folder with spaces Ж");
        var subfolder = Path.Combine(folder, "selected folder");
        var other = Path.Combine(fixtureRoot, "other launch folder");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(subfolder);
        Directory.CreateDirectory(other);
        var first = Path.Combine(folder, "first file Ж.txt");
        var second = Path.Combine(folder, "second file.txt");
        var elsewhere = Path.Combine(other, "elsewhere.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        File.WriteAllText(elsewhere, "elsewhere");

        Check("an explicit normal folder launch is recognized",
            FolderCommandLine.IsInvocation(["--open-folder", folder]));
        Check("a reveal launch is recognized case insensitively",
            FolderCommandLine.IsInvocation(["--REVEAL", first]));
        Check("an ordinary startup does not become a filesystem invocation",
            !FolderCommandLine.IsInvocation(["--settings"]));
        Check("the Win+E home request opens the actual user profile as a normal folder",
            FolderCommandLine.IsInvocation(["--shell-request", "--home"])
            && FolderCommandLine.TryParse(["--shell-request", "--home"], out var home, out _)
            && home.Kind == FolderInvocationKind.OpenFolder && home.OriginIsShell
            && home.FolderPath == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Check("a folder launch accepts spaces and Unicode without reparsing the path",
            FolderCommandLine.TryParse(["--open-folder", folder], out var open, out _)
            && open.Kind == FolderInvocationKind.OpenFolder && open.FolderPath == folder
            && open.SelectedPaths.Count == 0 && !open.OriginIsShell);
        Check("repeated reveal switches preserve every item and the containing folder",
            FolderCommandLine.TryParse(["--reveal", first, "--reveal", second], out var reveal, out _)
            && reveal.Kind == FolderInvocationKind.Reveal && reveal.FolderPath == folder
            && reveal.SelectedPaths.SequenceEqual([first, second]));
        Check("case-insensitive duplicate paths do not duplicate the selection",
            FolderCommandLine.TryReveal([first, first.ToUpperInvariant()], out var unique, out _)
            && unique.SelectedPaths.SequenceEqual([first]));
        Check("the shell flag survives parsing and argument building",
            FolderCommandLine.TryParse(["--shell-request", "--reveal", first], out var shell, out _)
            && shell.OriginIsShell
            && FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(shell), out var roundtrip, out _)
            && roundtrip.OriginIsShell && roundtrip.SelectedPaths.SequenceEqual([first]));
        Check("a requested new window gets a stable destination through argument rebuilding",
            FolderCommandLine.TryParse(["--new-window", "--open-folder", folder], out var separate, out _)
            && separate.DestinationId != Guid.Empty
            && FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(separate), out var sameDestination, out _)
            && sameDestination.DestinationId == separate.DestinationId);
        Check("an empty or malformed destination cannot be accepted",
            !FolderCommandLine.TryParse(["--destination-id", Guid.Empty.ToString(), "--open-folder", folder], out _, out _)
            && !FolderCommandLine.TryParse(["--destination-id", "broken", "--open-folder", folder], out _, out _));
        Check("reveal argument building round-trips the entire selection",
            FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(reveal), out var rebuilt, out _)
            && rebuilt.FolderPath == folder && rebuilt.SelectedPaths.SequenceEqual([first, second]));
        Check("a directory reveal refers to its parent and selects the directory",
            FolderCommandLine.TryReveal([subfolder], out var directoryReveal, out _)
            && directoryReveal.FolderPath == folder && directoryReveal.SelectedPaths.SequenceEqual([subfolder]));
        Check("a missing path is refused rather than revealing an unrelated ancestor",
            !FolderCommandLine.TryParse(["--reveal", Path.Combine(folder, "missing.txt")], out _, out _));
        Check("opening a file as a folder is refused",
            !FolderCommandLine.TryParse(["--open-folder", first], out _, out _));
        Check("an incomplete reveal is refused",
            !FolderCommandLine.TryParse(["--reveal"], out _, out _));
        Check("an incomplete folder launch is refused",
            !FolderCommandLine.TryParse(["--open-folder"], out _, out _));
        Check("mixed open and reveal actions are refused",
            !FolderCommandLine.TryParse(["--open-folder", folder, "--reveal", first], out _, out _));
        Check("multiple open actions are refused",
            !FolderCommandLine.TryParse(["--open-folder", folder, "--open-folder", other], out _, out _));
        Check("unknown launch switches are refused",
            !FolderCommandLine.TryParse(["--reveal", first, "--pick"], out _, out _));
        Check("a shell namespace cannot masquerade as a filesystem path",
            !FolderCommandLine.TryOpenFolder("shell:AppsFolder", out _, out _));
        Check("literal quotes in an already parsed argument are refused",
            !FolderCommandLine.TryOpenFolder('"' + folder + '"', out _, out _));
        Check("a quoted folder ending in a backslash doubles the trailing slash",
            NativeShellService.BuildCommandLine([@"C:\folder with spaces\"]) == "\"C:\\folder with spaces\\\\\"");
        Check("a command string quotes each selected path separately",
            FolderCommandLine.BuildCommandLine(reveal) == "--reveal \"" + first + "\" --reveal \"" + second + "\"");

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(Path.GetPathRoot(folder));
        Check("opening a folder uses normal navigation and selects that exact folder",
            await FolderInvocationNavigation.ApplyAsync(tree, open)
            && tree.Selection.Paths.Count == 0 && tree.ActivePath == folder
            && tree.FolderList.FolderPath == folder);
        Check("revealing several files retains them all in the shared selection",
            await FolderInvocationNavigation.ApplyAsync(tree, reveal)
            && tree.Selection.Paths.SequenceEqual([first, second]) && tree.Selection.FileCount == 2
            && tree.Selection.Container == folder && tree.FolderList.FolderPath == folder);
        Check("the reveal focus points at the first actual file",
            tree.Selection.Focus == first && tree.ActivePath == first);
        Check("revealed file sizes use real filesystem entries",
            tree.Selection.TotalBytes == new FileInfo(first).Length + new FileInfo(second).Length);
        Check("revealing a directory selects it while keeping its parent's folder list",
            await FolderInvocationNavigation.ApplyAsync(tree, directoryReveal)
            && tree.Selection.Paths.SequenceEqual([subfolder]) && tree.ActivePath == subfolder
            && tree.FolderList.FolderPath == folder);
        Check("revealing items from different folders does not silently discard any",
            FolderCommandLine.TryReveal([first, elsewhere], out var spread, out _)
            && await FolderInvocationNavigation.ApplyAsync(tree, spread)
            && tree.Selection.Paths.SequenceEqual([first, elsewhere])
            && tree.FolderList.FolderPath == folder);
        File.Delete(second);
        var before = tree.Selection.Paths.ToArray();
        Check("a vanished item cannot replace the existing selection with its parent",
            !await FolderInvocationNavigation.ApplyAsync(tree, reveal)
            && tree.Selection.Paths.SequenceEqual(before));
        FolderCommandLine.TryOpenFolder(other, out var newerOpen, out _);
        var olderLaunch = FolderInvocationNavigation.ApplyAsync(tree, directoryReveal);
        var newerLaunch = FolderInvocationNavigation.ApplyAsync(tree, newerOpen);
        Check("a newer launch wins over a previous request still validating its paths",
            await newerLaunch && !await olderLaunch
            && tree.Selection.Paths.Count == 0 && tree.ActivePath == other && tree.FolderList.FolderPath == other);
    }
}
