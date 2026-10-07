using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task Pr4ShelfSafetyChecks()
    {
        RunOnSta("PR4 shelf safety", Pr4ShelfSafetyOnStaAsync);
        return Task.CompletedTask;
    }

    private static Task Pr4ShelfSafetyOnStaAsync()
    {
        Section("PR4 shelf safety: owned copies, malformed state and live copy action");
        var fixture = Path.Combine(Path.GetTempPath(), "UltraExplorerShelfSafety", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixture, "Shelf");
        var sibling = Path.Combine(fixture, "ShelfSibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        var real = Path.Combine(sibling, "real.txt");
        File.WriteAllText(real, "keep the original");
        var sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep shelf root");
        var owned = Path.Combine(root, "copies", "012345abcdef");
        Directory.CreateDirectory(owned);
        var copy = Path.Combine(owned, "copy.txt");
        File.WriteAllText(copy, "expired cache bytes");
        try
        {
            Check("temporary-directory detection respects a separator boundary",
                ShelfStore.IsWithin(Path.Combine(Path.GetTempPath(), "test", "file.txt"), Path.GetTempPath())
                && !ShelfStore.IsWithin(sibling, root));
            Check("positions containing infinity are never accepted as layout rectangles",
                !new ShelfEntry(real, DateTime.UtcNow, false, double.PositiveInfinity, 5).HasPosition);

            var entries = new[]
            {
                new ShelfEntry(Path.Combine(sibling, "missing.txt"), DateTime.UtcNow, true),
                new ShelfEntry(Path.Combine(root, "missing.txt"), DateTime.UtcNow, true),
                new ShelfEntry(copy, DateTime.UtcNow.AddDays(-8), true),
                new ShelfEntry(real, DateTime.UtcNow, false, 12, 34),
                new ShelfEntry(real, DateTime.UtcNow, false, 99, 99)
            };
            File.WriteAllText(Path.Combine(root, "shelf.json"), JsonSerializer.Serialize(new { Width = double.NaN, Entries = entries },
                new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            var store = new ShelfStore(root);
            Check("invalid persisted width falls back and duplicate paths do not break card synchronization",
                store.Width == 520 && store.Count == 4);
            Check("expired copies and missing links are pruned", store.Prune() && store.Count == 1);
            Check("expired cache cleanup removes its exact owned container", !Directory.Exists(owned));
            Check("a malformed copy flag cannot delete a prefix sibling's real files", File.ReadAllText(real) == "keep the original");
            Check("a malformed copy flag cannot recursively delete the shelf root", File.ReadAllText(sentinel) == "keep shelf root");
            var reopened = new ShelfStore(root);
            Check("the preserved card keeps its position across reopening", reopened.Entries.Single() is { X: 12, Y: 34 });
            reopened.Clear();
            Check("clearing a link leaves its real source untouched", File.Exists(real));
            Check("a missing file on an available fixture volume is conclusive", ShelfStore.DefinitelyMissing(Path.Combine(fixture, "definitely-missing.txt")));
            var logical = Directory.GetLogicalDrives();
            var unavailableDrive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\")
                .FirstOrDefault(drive => !logical.Contains(drive, StringComparer.OrdinalIgnoreCase));
            if (unavailableDrive is not null)
            {
                var offline = Path.Combine(unavailableDrive, "UltraExplorer-owned-unavailable-fixture", "keep.txt");
                Check("an unavailable drive is not evidence that its source was deleted", !ShelfStore.DefinitelyMissing(offline));
                File.WriteAllText(Path.Combine(root, "shelf.json"), JsonSerializer.Serialize(new[]
                {
                    new ShelfEntry(offline, DateTime.UtcNow, false),
                    new ShelfEntry(offline + ".expired", DateTime.UtcNow.AddDays(-8), false)
                }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                var offlineStore = new ShelfStore(root);
                Check("an offline card is retained but seven-day expiry still applies", offlineStore.Prune()
                    && offlineStore.Entries is [{ Path: var kept }] && kept == offline);
            }
            Check("move-only sources are refused and shelf reports one allowed effect",
                DropShelf.ShelfEffect(DragDropEffects.Move) == DragDropEffects.None
                && DropShelf.ShelfEffect(DragDropEffects.Copy | DragDropEffects.Link) == DragDropEffects.Link
                && DropShelf.ShelfEffect(DragDropEffects.Copy) == DragDropEffects.Copy);

            var board = new ShelfBoard();
            board.Measure(new Size(240, 640));
            board.Arrange(new Rect(0, 0, 240, 640));
            var first = new ShelfCard(new ShelfEntry(root, DateTime.UtcNow, false), null);
            var firstSpot = board.FreePlace();
            first.X = firstSpot.X;
            first.Y = firstSpot.Y;
            board.AddCard(first);
            var nextSpot = board.FreePlace();
            Check("initial placement reserves room for a folder's pending children preview",
                !first.ReservedBounds.IntersectsWith(new Rect(nextSpot.X, nextSpot.Y, 210, 210)));
            board.ClearCards();
            Check("disabling the board releases cards and gesture state", board.Cards.Count == 0);

            var canvas = new NestedCanvas();
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var target = typeof(NestedCanvas).GetField("_copyPath", fields)!;
            var spot = typeof(NestedCanvas).GetField("_copySpot", fields)!;
            var press = typeof(NestedCanvas).GetMethod("TryPressCopyButton", fields)!;
            var calls = 0;
            canvas.CopyPathRequested += _ => calls++;
            Check("the hover copy action defaults to off", !canvas.ShowCopyPathButton);
            canvas.ShowCopyPathButton = true;
            target.SetValue(canvas, real);
            spot.SetValue(canvas, new Rect(0, 0, 24, 24));
            Check("an enabled hit copies once", (bool)press.Invoke(canvas, [new Point(10, 10)])! && calls == 1);
            canvas.ShowCopyPathButton = false;
            Check("disabling immediately removes the old clickable target",
                !(bool)press.Invoke(canvas, [new Point(10, 10)])! && calls == 1 && target.GetValue(canvas) is null);
        }
        finally { TryDelete(fixture); }
        return Task.CompletedTask;
    }
}
