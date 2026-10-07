using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The shelf: links kept, not copies - except for what lives in the
/// temporary folder, which would vanish by itself; each card's place on the
/// board and the shelf's width kept across a restart; what went from where
/// it was dropping off; and its board drawn, cards where they were put.
/// </summary>
internal static partial class Program
{
    private static Task ShelfChecks()
    {
        RunOnSta("shelf", ShelfOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ShelfOnStaAsync()
    {
        Section("shelf");
        var fixture = Path.Combine(AppContext.BaseDirectory, "shelf-fixture-" + Guid.NewGuid().ToString("N")[..6]);
        var project = Path.Combine(fixture, "project");
        Directory.CreateDirectory(Path.Combine(project, "src", "deep"));
        File.WriteAllText(Path.Combine(project, "README.md"), "readme");
        File.WriteAllText(Path.Combine(project, "src", "main.cs"), "class A {}");
        File.WriteAllText(Path.Combine(project, "build.ps1"), "x");
        var notes = Path.Combine(fixture, "notes.txt");
        File.WriteAllText(notes, new string('n', 3000));
        var zip = Path.Combine(fixture, "bundle.zip");
        ZipFile.CreateFromDirectory(project, zip);
        var temporary = Path.Combine(Path.GetTempPath(), "shelf-temp-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(temporary, "from temp");

        var store = new ShelfStore();
        store.Clear();
        try
        {
            var (added, _) = await store.AddAsync([project, notes, zip, temporary]);
            Check("four things go on the shelf", added == 4 && store.Count == 4);
            Check("a folder on the shelf is a link to it, not a copy", store.Entries.Any(entry => entry.Path == project && !entry.IsCopy));
            var tempEntry = store.Entries.FirstOrDefault(entry => entry.IsCopy);
            Check("what lives in the temporary folder is copied, so it outlives it",
                tempEntry is not null && tempEntry.Path.StartsWith(store.Root, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(tempEntry.Path) == "from temp");
            var again = await store.AddAsync([project]);
            Check("the same thing dropped again is not put on twice, but is handed back to be placed", again.Added == 0 && again.Paths.SequenceEqual([project]) && store.Count == 4);

            // The board, as the shelf shows it.
            var board = new ShelfBoard();
            board.Measure(new Size(520, 640));
            board.Arrange(new Rect(0, 0, 520, 640));
            foreach (var entry in store.Entries)
            {
                var card = new ShelfCard(entry, null);
                var spot = entry.HasPosition ? new Point(entry.X, entry.Y) : board.FreePlace();
                card.X = spot.X;
                card.Y = spot.Y;
                board.AddCard(card);
                board.UpdateLayout();
            }

            await Task.Delay(400);
            board.UpdateLayout();
            Check("cards without a place are laid out where none overlaps",
                board.Cards.SelectMany((a, i) => board.Cards.Skip(i + 1).Select(b => (a, b))).All(pair => !pair.a.Bounds.IntersectsWith(pair.b.Bounds)));
            Check("a folder and an archive are cards that show what is inside",
                board.Cards.Count(card => card.IsFolder || card.IsArchive) == 2);
            // Eight files dropped at once onto a board with cards on it: laid out from the drop, none on another.
            var many = Enumerable.Range(0, 8).Select(index => new ShelfCard(new ShelfEntry(Path.Combine(fixture, $"TilesSquarePoolMixed001_{index}_4K_Normal.png"), DateTime.UtcNow, false), null)).ToList();
            foreach (var card in many)
            {
                board.AddCard(card);
            }

            board.PlaceGroup(many, new Point(20, 20));
            board.UpdateLayout();
            Check("cards dropped together never lie on one another, nor on the cards already there",
                board.Cards.SelectMany((a, i) => board.Cards.Skip(i + 1).Select(b => (a, b))).All(pair => !pair.a.Bounds.IntersectsWith(pair.b.Bounds)));
            Check("…and start where they were dropped", many[0].X == 20 && many[0].Y >= 20);
            Check("a long name keeps its end", ShelfCard.MiddleTrim("TilesSquarePoolMixed001_7_4K_Normal.png").EndsWith("4K_Normal.png", StringComparison.Ordinal));
            board.FitAll();
            ShootElement(board, "shelf.png");

            store.SetPositions([(zip, 33, 44)]);
            store.SetWidth(640);
            var reopened = new ShelfStore();
            Check("each card's place and the shelf's width are kept across a restart",
                reopened.Entries.First(entry => entry.Path == zip) is { X: 33, Y: 44 } && reopened.Width == 640);

            File.Delete(notes);
            Check("a link to something deleted drops off", store.Prune() && store.Count == 3);
            store.Remove([tempEntry!.Path]);
            Check("a copy taken off the shelf is deleted with it", !File.Exists(tempEntry.Path) && store.Count == 2);
            store.Remove([project]);
            Check("…while a link taken off leaves the real thing alone", Directory.Exists(project) && store.Count == 1);
        }
        finally
        {
            store.Clear();
            store.SetWidth(520);
            TryDelete(fixture);
            File.Delete(temporary);
        }
    }

    private static void ShootElement(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("NESTED_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1A)), null, bounds);
            context.DrawRectangle(new VisualBrush(element), null, bounds);
        }

        bitmap.Render(visual);
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
