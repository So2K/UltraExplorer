using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task FavoriteLinkChecks()
    {
        RunOnSta("favorite navigation circles", FavoriteLinksOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task FavoriteLinksOnStaAsync()
    {
        Section("favorite navigation circles: optional, lazy and additive");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\Users\User\Downloads");
        disk.Folder(@"Q:\Users\User\Pictures");
        disk.AddFiles(@"Q:\Users\User\Downloads", 8, "download-");
        disk.Folder(@"R:\Work");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Local Disk (Q:)", NestedFolderKind.Drive),
            new NestedRoot(@"R:\", "Work (R:)", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1600, 1000));
        canvas.Arrange(new Rect(0, 0, 1600, 1000));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        var originalRoot = canvas.ScreenRectOf(tree.Root)!.Value;
        var reads = disk.Reads;
        var originalChildren = tree.Root.Children.ToArray();
        var links = new List<NestedFavoriteLink>
        {
            new(@"Q:\Users\User\Downloads", "Downloads", Color.FromRgb(230, 178, 83)),
            new(@"Q:\Users\User\Pictures", "Pictures", Color.FromRgb(176, 134, 232)),
            new(@"R:\Work", "Work", Color.FromRgb(226, 112, 140)),
            new(@"Z:\missing", "Unavailable", Color.FromRgb(154, 190, 170)),
            new(@"\\offline\share\large", "Offline share", Color.FromRgb(126, 170, 216)),
            new(@"q:\users\user\downloads\", "Duplicate", Colors.Red),
            new("", "Empty", Colors.Red)
        };
        canvas.SetFavoriteLinks(links);
        Shoot(canvas, "favorite-links-disabled.png");
        Check("links are off by default and preserve the old overview",
            !canvas.ShowFavoriteLinks && canvas.FavoriteLinkPositions().Count == 0
            && SameRect(canvas.ScreenRectOf(tree.Root), originalRoot, 1e-8));

        canvas.ShowFavoriteLinks = true;
        Shoot(canvas, "favorite-links-overview.png");
        var positions = canvas.FavoriteLinkPositions();
        Check("valid destinations are deduplicated without a directory read", positions.Count == 5 && disk.Reads == reads);
        Check("favorite shortcuts add no folders to This PC", tree.Root.Children.SequenceEqual(originalChildren));
        var overview = canvas.ScreenRectOf(tree.Root)!.Value;
        Check("overview fits both circles and This PC inside the view", overview.Bottom <= 982.00001
            && positions.All(position => position.Centre.Y - position.Radius >= 18
                && position.Centre.Y + position.Radius < overview.Top));
        Check("links are not physical folders for file hit testing", positions.All(position => canvas.HitTest(position.Centre) is null));

        var pointer = new NestedPointer(canvas);
        var file = @"Q:\Users\User\Downloads\download-000.txt";
        canvas.SetSelection([file], file);
        Shoot(canvas, "favorite-links-selection.png");
        var changes = 0;
        var opens = 0;
        string? destination = null;
        canvas.SelectionCommitted += _ => changes++;
        canvas.OpenRequested += _ => opens++;
        canvas.FavoriteLinkRequested += path => destination = path;
        pointer.Click(positions[0].Centre);
        Check("a single link click preserves file selection and does not navigate", changes == 0 && destination is null && opens == 0);
        pointer.Down(MouseButton.Left, positions[0].Centre, clickCount: 2);
        pointer.Up(MouseButton.Left, positions[0].Centre);
        Check("a double click requests the exact folder path, never file Open", destination == links[0].Path && opens == 0 && changes == 0);
        Check("even unavailable links stay lazy until their navigation request", disk.Reads == reads);

        var beforePan = positions[0].Centre;
        canvas.Pan(new Vector(13, 19));
        Shoot(canvas, "favorite-links-panned.png");
        var afterPan = canvas.FavoriteLinkPositions()[0].Centre;
        Check("circles follow world pan with the drives", SamePoint(afterPan, beforePan + new Vector(13, 19), 1e-8));
        canvas.FitAll(animated: false);
        var zoomBefore = canvas.FavoriteLinkPositions()[0];
        canvas.ZoomAt(zoomBefore.Centre, 1.2);
        Shoot(canvas, "favorite-links-zoomed.png");
        var zoomAfter = canvas.FavoriteLinkPositions()[0];
        Check("zooming at a circle keeps its destination under the pointer",
            SamePoint(zoomBefore.Centre, zoomAfter.Centre, 1e-8)
            && Near(zoomAfter.Radius, zoomBefore.Radius * 1.2, 1e-8));
        destination = null;
        pointer.Drag(zoomAfter.Centre, zoomAfter.Centre + new Vector(30, 20));
        Check("dragging a circle pans without selection or a file drag",
            destination is null && opens == 0 && changes == 0 && pointer.State == "idle");
        var target = tree.Find(@"Q:\Users\User\Pictures")!;
        canvas.FlyTo(target, .92, animated: false);
        var folderView = canvas.ScreenRectOf(target)!.Value;
        canvas.ShowFavoriteLinks = false;
        Check("turning links off preserves a focused folder camera", ReferenceEquals(canvas.Anchor, target)
            && SameRect(canvas.ScreenRectOf(target), folderView, 1e-8));
        canvas.FitAll(animated: false);
        Shoot(canvas, "favorite-links-off-again.png");
        Check("switching off restores the original overview exactly", SameRect(canvas.ScreenRectOf(tree.Root), originalRoot, 1e-8));

        for (var index = 0; index < 18; index++)
            links.Add(new NestedFavoriteLink($@"R:\favorite-{index}", $"Favorite {index + 1}", Color.FromRgb(130, 186, 206)));
        canvas.SetFavoriteLinks(links);
        canvas.ShowFavoriteLinks = true;
        canvas.FitAll(animated: false);
        Shoot(canvas, "favorite-links-many.png");
        positions = canvas.FavoriteLinkPositions();
        overview = canvas.ScreenRectOf(tree.Root)!.Value;
        Check("many favorites wrap into rows above This PC", positions.Count == 23
            && positions.Select(position => position.Centre.Y).Distinct().Count() == 3
            && positions.All(position => position.Centre.Y - position.Radius >= 18
                && position.Centre.Y + position.Radius < overview.Top));
        Check("wrapping and rendering still perform no folder reads", disk.Reads == reads);
    }
}
