using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// J108: with a dialog's file type chosen - Paint's Open on "Images" - a
/// folder of documents showed none of its files and said "37 hidden files",
/// and its title counted the 37 it did not show.  It now says that none of
/// its files is of the type, and the title counts what it shows; a folder
/// whose files are hidden says so as it always did.
/// </summary>
internal static partial class Program
{
    private static Task TypeFilterNoteChecks()
    {
        RunOnSta("type filter: what an empty-looking folder says", TypeFilterNoteAsync);
        return Task.CompletedTask;
    }

    private static async Task TypeFilterNoteAsync()
    {
        Section("type filter: a folder with no files of the chosen type says so, not that they are hidden (J108)");
        var disk = new FakeDisk();
        for (var index = 0; index < 37; index++)
        {
            disk.AddFile(@"Q:\docs", $"letter{index:D2}.docx", 10);
        }

        disk.AddFile(@"Q:\secret", "a.png", 10, hidden: true);
        disk.AddFile(@"Q:\secret", "b.png", 10, hidden: true);
        disk.AddFile(@"Q:\secret", "c.png", 10, hidden: true);
        disk.AddFile(@"Q:\mixed", "one.png", 10);
        disk.AddFile(@"Q:\mixed", "two.docx", 10);
        disk.AddFile(@"Q:\mixed", "three.docx", 10);
        disk.AddFile(@"Q:\hiddendocs", "x.docx", 10, hidden: true);
        disk.AddFile(@"Q:\hiddendocs", "y.docx", 10);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            List<string> TextsOf(string path)
            {
                canvas.FlyTo(tree.Find(path)!, 0.6, animated: false);
                canvas.RenderNow();
                return [.. canvas.RecordLabelCallsForTests().Texts.Select(text => text.Text)];
            }

            // Without a type: as it always was.
            var docs = tree.Find(@"Q:\docs")!;
            Check("without a file type, a folder of documents shows them and counts them",
                docs.Files.Count == 37 && canvas.TitleDetailForTests(docs) == "37 files");
            var hiddenOnly = TextsOf(@"Q:\secret");
            Check($"and a folder of hidden files says they are hidden ({string.Join(" | ", hiddenOnly.Where(text => text.Contains("file")))})",
                hiddenOnly.Contains("3 hidden files"));

            // Images only, as a picker's "Images" asks.
            tree.FileNameFilter = name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var notOfType = TextsOf(@"Q:\docs");
            Check($"with only images shown, the folder of documents says none of its files is of the type ({string.Join(" | ", notOfType.Where(text => text.Contains("file")))})",
                notOfType.Contains("No files of this type (37)") && !notOfType.Contains("37 hidden files"));
            Check($"and its title counts the files it shows ({canvas.TitleDetailForTests(docs)})",
                docs.Files.Count == 0 && canvas.TitleDetailForTests(docs) == string.Empty);
            var mixed = tree.Find(@"Q:\mixed")!;
            Check($"a folder with one image among documents counts the one ({canvas.TitleDetailForTests(mixed)})",
                mixed.Files.Count == 1 && canvas.TitleDetailForTests(mixed) == "1 file");

            var stillHidden = TextsOf(@"Q:\secret");
            Check($"a folder whose images are all hidden still says they are hidden ({string.Join(" | ", stillHidden.Where(text => text.Contains("file")))})",
                stillHidden.Contains("3 hidden files"));
            var partlyHidden = TextsOf(@"Q:\hiddendocs");
            Check($"and one with a hidden document and a shown one says the one shown is not of the type ({string.Join(" | ", partlyHidden.Where(text => text.Contains("file")))})",
                partlyHidden.Contains("No files of this type (1)"));

            tree.IncludeHidden = true;
            var shownHidden = TextsOf(@"Q:\hiddendocs");
            Check($"with hidden files shown, both documents count ({string.Join(" | ", shownHidden.Where(text => text.Contains("file")))})",
                shownHidden.Contains("No files of this type (2)"));
        }
        finally
        {
            tree.FileNameFilter = null;
            canvas.Tree = null;
        }
    }
}
