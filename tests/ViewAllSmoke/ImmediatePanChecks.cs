using System.Windows;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ImmediatePanChecks()
    {
        RunOnSta("immediate Space pan", ImmediatePanOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ImmediatePanOnStaAsync()
    {
        Section("Space pan: immediate pointer mapping and frame coalescing");
        var disk = new FakeDisk();
        const string folderPath = @"Q:\pan";
        disk.AddFiles(folderPath, 3, "sample");
        disk.Folder(folderPath + @"\child");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var folder = tree.Find(folderPath)!;
        var canvas = new NestedCanvas
        {
            Tree = tree, FramesByHandForTests = true,
            DpiOverride = new DpiScale(1, 1)
        };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        var pointer = canvas.Pointer;
        var selected = 0;
        var dragged = 0;
        var opened = 0;
        var cameraMoves = 0;
        var menus = 0;
        canvas.SelectionCommitted += _ => selected++;
        canvas.DragRequested += _ => dragged++;
        canvas.OpenRequested += _ => opened++;
        canvas.UserCameraMoved += () => cameraMoves++;
        canvas.ContextMenuRequested += (_, _, _) => menus++;
        try
        {
            canvas.FlyTo(folder, .9, animated: false);
            canvas.RunFrameForTests(TimeSpan.FromSeconds(1));
            var initial = canvas.ScreenRectOf(folder)!.Value;
            var origin = new Point(initial.X + initial.Width * .5, initial.Y + initial.Height * .5);
            canvas.IsSpacePanArmed = true;
            pointer.Down(MouseButton.Left, origin);
            Check("Space pan starts as an explicit moved pan before any drag threshold",
                pointer.State == "Left moved Pan" && canvas.Marquee is null);
            pointer.Move(origin);
            Check("a stationary captured pointer does not move the camera", cameraMoves == 0);
            pointer.Move(origin + new Vector(.125, .25));
            var tiny = canvas.ScreenRectOf(folder)!.Value;
            Check("the first sub-pixel Space movement changes the actual camera immediately",
                Near(tiny.X - initial.X, .125, 1e-9) && Near(tiny.Y - initial.Y, .25, 1e-9)
                && cameraMoves == 1);
            var frames = canvas.LoopFrameCount;
            pointer.Move(origin + new Vector(1, 1));
            pointer.Move(origin + new Vector(8, -3));
            pointer.Move(origin + new Vector(30.5, -12.25));
            var final = canvas.ScreenRectOf(folder)!.Value;
            Check("multiple pointer updates retain the full exact displacement without an initial jump",
                Near(final.X - initial.X, 30.5, 1e-9) && Near(final.Y - initial.Y, -12.25, 1e-9));
            Check("pointer updates request a frame without rendering once per mouse event",
                canvas.LoopFrameCount == frames && canvas.IsFrameHooked);
            canvas.RunFrameForTests(TimeSpan.FromSeconds(1.01));
            Check("one display frame consumes the coalesced pointer updates", canvas.LoopFrameCount == frames + 1);
            pointer.Move(origin);
            var restored = canvas.ScreenRectOf(folder)!.Value;
            Check("reversing Space movement returns to the same camera anchor",
                Near(restored.X, initial.X, 1e-9) && Near(restored.Y, initial.Y, 1e-9)
                && Near(restored.Width, initial.Width, 1e-9));
            pointer.Up(MouseButton.Left, origin);
            canvas.IsSpacePanArmed = false;
            var stopped = canvas.ScreenRectOf(folder)!.Value;
            pointer.Move(origin + new Vector(100, 100));
            canvas.RunFrameForTests(TimeSpan.FromSeconds(1.02));
            canvas.RunFrameForTests(TimeSpan.FromSeconds(2));
            Check("release stops immediately without coast or delayed camera movement",
                SameRect(canvas.ScreenRectOf(folder), stopped, 1e-9) && pointer.State == "idle");
            Check("Space panning never changes selection, opens a file or starts file dragging",
                selected == 0 && dragged == 0 && opened == 0);

            canvas.IsSpacePanArmed = true;
            pointer.Down(MouseButton.Left, origin);
            pointer.Move(origin + new Vector(.5, -.5));
            pointer.LoseCapture();
            canvas.IsSpacePanArmed = false;
            var lost = canvas.ScreenRectOf(folder)!.Value;
            pointer.Move(origin + new Vector(10, 20));
            canvas.RunFrameForTests(TimeSpan.FromSeconds(3));
            Check("lost capture leaves the camera exactly where the Space pan stopped",
                pointer.State == "idle" && SameRect(canvas.ScreenRectOf(folder), lost, 1e-9));

            canvas.IsSpacePanArmed = true;
            pointer.Down(MouseButton.Left, origin);
            pointer.Move(origin + new Vector(.5, .25));
            canvas.IsSpacePanArmed = false;
            var keyReleased = canvas.ScreenRectOf(folder)!.Value;
            pointer.Move(origin + new Vector(30, 20));
            Check("releasing Space stops its pan even while the left mouse button remains down",
                pointer.State == "idle" && SameRect(canvas.ScreenRectOf(folder), keyReleased, 1e-9));
            pointer.Up(MouseButton.Left, origin);

            canvas.FlyTo(folder, .9, animated: false);
            canvas.RunFrameForTests(TimeSpan.FromSeconds(4));
            var child = folder.Children[0];
            var title = TitlePoint(canvas.ScreenRectOf(child)!.Value);
            pointer.Down(MouseButton.Left, title);
            pointer.Move(title + new Vector(.125, 0));
            Check("ordinary left presses still retain their file drag threshold",
                pointer.State.StartsWith("Left pending", StringComparison.Ordinal) && dragged == 0);
            pointer.Up(MouseButton.Left, title + new Vector(.125, 0));
            Check("an ordinary sub-threshold left click still selects", selected > 0 && opened == 0);
            pointer.Drag(title, title + new Vector(20, 15));
            Check("ordinary folder title dragging still starts the file operation", dragged == 1);
            pointer.Down(MouseButton.Right, title);
            pointer.Move(title + new Vector(.125, 0));
            pointer.Up(MouseButton.Right, title + new Vector(.125, 0));
            Check("ordinary sub-threshold right clicks still open their menu", menus == 1);
        }
        finally { pointer.LoseCapture(); canvas.Tree = null; }
    }
}
