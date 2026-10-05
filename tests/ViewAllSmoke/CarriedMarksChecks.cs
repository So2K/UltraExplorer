using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Cut and paste or a drag that moves, and F2 in a folder no window has
/// read: nothing on disk pairs the old name with the new, and the colour and
/// the note stayed behind on a name nothing has any more.  The window that
/// did it carries them along itself - a folder's, and those of everything
/// inside it (review 2, J142).  Every file this touches is in the isolated
/// state folder, put back as it was at the end.
/// </summary>
internal static partial class Program
{
    private static Task CarriedMarksChecks()
    {
        RunWithIsolatedState("carried marks", CarriedMarksChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task CarriedMarksChecksAsync(string fixture)
    {
        Section("marks: a moved or renamed item takes its colour and note along (J142)");
        var source = Path.Combine(fixture, "carried", "source");
        var target = Path.Combine(fixture, "carried", "target");
        var unread = Path.Combine(fixture, "carried", "unread");
        var elsewhere = Path.Combine(fixture, "carried", "elsewhere");
        var report = Path.Combine(source, "report.txt");
        var project = Path.Combine(source, "project");
        var inside = Path.Combine(project, "inside.txt");
        var original = Path.Combine(source, "original.txt");
        var plan = Path.Combine(unread, "plan.txt");
        var planFinal = Path.Combine(unread, "plan, final.txt");
        foreach (var directory in new[] { project, target, unread, elsewhere })
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var file in new[] { report, inside, plan })
        {
            File.WriteAllText(file, "x");
        }

        await ResetIsolatedStateAsync();
        using var window = new MainViewModel();
        await window.InitializeAsync(elsewhere);
        window.Marks.SetAccent(report, "#EF5A68");
        window.Marks.SetNote(report, "carried along");
        window.Marks.SetAccent(project, "#3FA34D");
        window.Marks.SetNote(inside, "inside the folder");
        window.Marks.SetAccent(plan, "#4A7BD0");

        var moved = await window.DropIntoPathWithResultAsync([report, project], target, move: true);
        var movedReport = Path.Combine(target, "report.txt");
        var movedProject = Path.Combine(target, "project");
        var movedInside = Path.Combine(movedProject, "inside.txt");
        Check("the move is done", moved && File.Exists(movedReport) && File.Exists(movedInside) && !File.Exists(report) && !Directory.Exists(project));
        Check("the moved file keeps its colour and note, and the old name has none",
            window.Marks.Get(movedReport) is { AccentHex: "#EF5A68", Note: "carried along" } && window.Marks.Get(report).IsEmpty);
        Check("the moved folder keeps its colour, and the file inside it its note",
            window.Marks.Get(movedProject).AccentHex == "#3FA34D" && window.Marks.Get(movedInside).Note == "inside the folder"
            && window.Marks.Get(project).IsEmpty && window.Marks.Get(inside).IsEmpty);

        // Copied, not moved: the original keeps its mark, and the copy has none.
        File.WriteAllText(original, "x");
        window.Marks.SetAccent(original, "#D9A13B");
        var copied = await window.DropIntoPathWithResultAsync([original], target, move: false);
        Check("a copy leaves the mark on the original",
            copied && File.Exists(Path.Combine(target, "original.txt")) && window.Marks.Get(original).AccentHex == "#D9A13B"
            && window.Marks.Get(Path.Combine(target, "original.txt")).IsEmpty);

        window.Tree.Selection.ReplaceSingle(plan, false, 1, SelectionSource.Canvas);
        Func<string, string, string, bool, string?> prompt = (_, _, _, _) => Path.GetFileName(planFinal);
        window.PromptRequested += prompt;
        try
        {
            window.RenameCommand.Execute(null);
            await LiveWait(() => File.Exists(planFinal) && window.Toast.Message.StartsWith("Renamed", StringComparison.Ordinal), 10_000);
        }
        finally
        {
            window.PromptRequested -= prompt;
        }

        Check("renamed in a folder no window has read, a file keeps its colour",
            File.Exists(planFinal) && window.Marks.Get(planFinal).AccentHex == "#4A7BD0" && window.Marks.Get(plan).IsEmpty);

        await window.SaveNowAsync();
        var disk = new FolderMarkService();
        await disk.LoadAsync();
        Check("and the file of marks says the same",
            disk.Get(movedReport).Note == "carried along" && disk.Get(movedInside).Note == "inside the folder"
            && disk.Get(planFinal).AccentHex == "#4A7BD0" && disk.Get(report).IsEmpty && disk.Get(plan).IsEmpty);
    }
}
