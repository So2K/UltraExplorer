using System.Diagnostics;
using System.IO;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Every window of the process - the everyday window, an Explorer window
/// taken over, the prepared picker - had a copy of the marks of its own, read
/// once at its start: a colour set in one never showed in another, and a
/// rename followed in one wrote its older note, or a colour cleared since in
/// another, under the new name.  They share one now (review 2, J061).  Every
/// file this touches is in the isolated state folder, put back as it was at
/// the end.
/// </summary>
internal static partial class Program
{
    private static Task SharedMarksChecks()
    {
        RunWithIsolatedState("shared marks", SharedMarksChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task SharedMarksChecksAsync(string fixture)
    {
        Section("marks: every window of the process shares one set of marks (J061)");
        var folder = Path.Combine(fixture, "shared");
        var elsewhere = Path.Combine(fixture, "elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(elsewhere);
        var coloured = Path.Combine(folder, "coloured.txt");
        var noted = Path.Combine(folder, "noted.txt");
        var notedRenamed = Path.Combine(folder, "noted, renamed.txt");
        var cleared = Path.Combine(folder, "cleared.txt");
        var clearedRenamed = Path.Combine(folder, "cleared, renamed.txt");
        foreach (var file in new[] { coloured, noted, cleared })
        {
            File.WriteAllText(file, "x");
        }

        await ResetIsolatedStateAsync();
        using var first = new MainViewModel();
        await first.InitializeAsync(folder);
        var secondWorkspace = Path.Combine(fixture, "shell-windows", Guid.NewGuid().ToString("N") + ".workspace.json");
        using var second = new MainViewModel(secondWorkspace + ".tree.json", false, secondWorkspace);
        await second.InitializeAsync(elsewhere);

        // As the replacement prepares one, long before a dialog comes.
        var picker = new MainViewModel(Path.Combine(fixture, "shared.picker.tree.json"), nestedPicker: true) { SuppressShellWrites = true };
        picker.Tree.SuppressWrites = true;
        var pickerTree = picker.Tree;
        try
        {
            await picker.InitializeAsync(folder);

            var heard = 0;
            Action<string, FolderMark> listener = (_, _) => heard++;
            second.Marks.MarkChanged += listener;
            try
            {
                first.Tree.ApplyAccent([coloured], "#EF5A68");
            }
            finally
            {
                second.Marks.MarkChanged -= listener;
            }

            Check("a colour set in one window is the other window's at once", second.Marks.Get(coloured).AccentHex == "#EF5A68");
            Check("which hears of it, to draw it", heard > 0);
            Check("and the prepared picker has it too", picker.Marks.Get(coloured).AccentHex == "#EF5A68");

            // A note written in the second window after the first saved its own.
            first.Tree.ApplyAccent([noted], "#3FA34D");
            first.Tree.ApplyNote(noted, "the first note");
            await first.SaveNowAsync();
            second.Tree.ApplyNote(noted, "the newer note");
            await second.SaveNowAsync();
            File.Move(noted, notedRenamed);
            SharedMarksFollowRename(first, folder, Path.GetFileName(noted), Path.GetFileName(notedRenamed));
            await first.SaveNowAsync();
            await second.SaveNowAsync();
            var disk = new FolderMarkService();
            await disk.LoadAsync();
            Check($"a rename followed in one window carries the newer note written in the other ({disk.Get(notedRenamed).Note})",
                disk.Get(notedRenamed) is { AccentHex: "#3FA34D", Note: "the newer note" } && disk.Get(noted).IsEmpty);

            // A colour cleared in the second window, then the file renamed.
            first.Tree.ApplyAccent([cleared], "#4A7BD0");
            await first.SaveNowAsync();
            second.Tree.ApplyAccent([cleared], null);
            await second.SaveNowAsync();
            File.Move(cleared, clearedRenamed);
            SharedMarksFollowRename(first, folder, Path.GetFileName(cleared), Path.GetFileName(clearedRenamed));
            await first.SaveNowAsync();
            await second.SaveNowAsync();
            disk = new FolderMarkService();
            await disk.LoadAsync();
            Check($"a colour cleared in one window does not come back with a rename followed in the other ({disk.Get(clearedRenamed).AccentHex})",
                disk.Get(clearedRenamed).IsEmpty && disk.Get(cleared).IsEmpty);

            // The prepared picker of the dialog worker is another process,
            // with marks of its own read when it was prepared: a note written
            // since by another copy of the app is the file's, and the picker
            // must have it - and draw it - once it is bound to a new dialog.
            var other = new FolderMarkService();
            await other.LoadAsync();
            other.SetNote(coloured, "written in another process");
            await other.SaveAsync();
            var pickerHeard = 0;
            Action<string, FolderMark> pickerListener = (path, _) =>
            {
                if (UltraExplorer.Models.ViewAllPath.Equals(path, coloured))
                {
                    pickerHeard++;
                }
            };
            picker.Marks.MarkChanged += pickerListener;
            try
            {
                await picker.RefreshPickerNavigationPreferencesAsync();
            }
            finally
            {
                picker.Marks.MarkChanged -= pickerListener;
            }

            Check($"bound to a new dialog, the prepared picker has a note written in another process since ({picker.Marks.Get(coloured).Note})",
                picker.Marks.Get(coloured) is { AccentHex: "#EF5A68", Note: "written in another process" });
            Check("and hears of it, to draw it", pickerHeard > 0);
        }
        finally
        {
            picker.Dispose();
        }

        // A dialog's tree listens to the marks for a change made in it; let
        // go of with its window, it must not be kept alive by what every
        // window of the process shares.
        var listening = typeof(FolderMarkService)
            .GetField(nameof(FolderMarkService.MarkChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .GetValue(first.Marks) as Delegate;
        Check("a closed dialog's tree no longer listens to the marks",
            listening?.GetInvocationList().All(handler => !ReferenceEquals(handler.Target, pickerTree)) ?? true);
    }

    /// <summary>What a window's change hub hands its tree when a file in <paramref name="folder"/> is renamed.</summary>
    private static void SharedMarksFollowRename(MainViewModel window, string folder, string oldName, string newName) =>
        ((IChangeSink)window.Tree).FolderChanged(
            ChangeConsumer.Nested,
            folder,
            new FolderChange(folder, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair(oldName, newName) }, default));
}
