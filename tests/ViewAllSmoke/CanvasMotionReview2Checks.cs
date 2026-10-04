using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the canvas's camera, input and frame
/// loop, and what was done about it.  An arrow key held down keeps the item
/// it moves to on screen at the zoom it had, rather than restarting a flight
/// from a standstill on every repeat; and when the camera comes to rest,
/// the loop lets go of WPF's frames only once the settled picture is on its
/// way, so WPF does not block the thread to show it.
/// </summary>
internal static partial class Program
{
    private static Task CanvasMotionReview2Checks()
    {
        RunOnSta("canvas motion: an arrow key held down", CanvasMotionKeyRepeatAsync);
        RunOnSta("canvas motion: the loop letting go at rest", CanvasMotionRestLetGoAsync);
        RunOnSta("canvas motion: the arrows after the focused item went", CanvasMotionFocusAfterRemovalAsync);
        RunOnSta("canvas motion: a middle click that does not move", CanvasMotionStillMiddleClickAsync);
        RunOnSta("canvas motion: the hover after the wheel", CanvasMotionHoverAfterWheelAsync);
        RunOnSta("canvas motion: the folder in view after a move", CanvasMotionFolderInViewAsync);
        RunOnSta("canvas motion: a camera put back from the last session", CanvasMotionRestoreAsync);
        return Task.CompletedTask;
    }

    // ---- the arrows after the focused item went (J053) ----------------------------------------

    /// <summary>
    /// A file clicked, then deleted: the window lets go of it and moves only
    /// the focus to its folder, which is not selected (I004).  The arrows -
    /// and Shift with them - then move within that folder, as they did when
    /// the focus stayed on the item that went (I118): never to the folder's
    /// neighbour, nor taking the folder itself into a range, which the next
    /// Delete would recycle whole.
    /// </summary>
    private static async Task CanvasMotionFocusAfterRemovalAsync()
    {
        Section("canvas motion: the arrows after the focused file was deleted stay in its folder");
        var disk = BuildSafetyWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "1 TB free")
        ]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();

        // The window's side, as a pane has it (see SelectionSafetyCanvasAsync).
        var shared = new ItemSelection();
        canvas.SelectionCommitted += edit =>
        {
            shared.Apply(edit);
            if (canvas.SelectedCount != shared.Count)
            {
                canvas.LoadSelection(shared);
            }
            else
            {
                canvas.AcknowledgeSelection(shared.Version);
            }
        };

        var docs = tree.Find(SafetyDocs)!;
        try
        {
            foreach (var (key, modifiers) in new[] { (Key.Down, ModifierKeys.None), (Key.Right, ModifierKeys.None), (Key.Down, ModifierKeys.Shift) })
            {
                canvas.FlyTo(docs, 0.9, animated: false);
                Render(canvas);
                canvas.Pointer.Click(TilePoint(canvas.ScreenRectOf(docs)!.Value, docs, 9));
                var name = docs.Files[9].Name;
                var gone = docs.PathOf(docs.Files[9]);
                disk.Folder(SafetyDocs).Files.RemoveAll(file => file.Name == name);
                await tree.RefreshAsync(docs);

                // What the window does once the file is gone
                // (ViewAllViewModel.ReleaseRemovedFocus): the file is let go,
                // and only the focus moves, to its folder.
                shared.Apply(new SelectionEdit { Removed = [gone], Source = SelectionSource.Command });
                shared.Apply(new SelectionEdit { Focus = docs.FullPath, Source = SelectionSource.Command });
                canvas.LoadSelection(shared);
                Render(canvas);
                canvas.HandleKey(key, modifiers);
                var picked = shared.Paths.ToList();
                var label = modifiers == ModifierKeys.Shift ? $"Shift+{key}" : key.ToString();
                Check($"{label} after the focused file was deleted selects within its folder, never the folder or one beside it ({string.Join(", ", picked)})",
                    picked.Count > 0 && picked.All(path => ItemSelection.ParentOf(path).Equals(docs.FullPath, StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- a middle click that does not move (J107) ----------------------------------------------

    /// <summary>
    /// A flight to a folder still reading its way there, and meanwhile a
    /// middle click that never moves - capturing the mouse raises a move at
    /// the very point it went down - and a wheel notch of nothing.  Neither
    /// moves the camera, so neither is the user's move: the flight still
    /// lands, and nobody is told the user moved the view.
    /// </summary>
    private static async Task CanvasMotionStillMiddleClickAsync()
    {
        Section("canvas motion: a middle click that does not move leaves the camera alone");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\slow\child");
        disk.Folder(@"Q:\ready");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        using var gate = new SemaphoreSlim(0);
        var entered = 0;
        disk.Hook = (path, token) =>
        {
            if (path == @"Q:\slow")
            {
                Interlocked.Increment(ref entered);
                gate.Wait(token);
            }

            return null;
        };

        var userMoves = 0;
        canvas.UserCameraMoved += () => userMoves++;
        try
        {
            var before = canvas.CaptureCamera();
            var going = canvas.FlyToPathAsync(@"Q:\slow\child", animated: false);
            await WaitUntil(() => Volatile.Read(ref entered) == 1, 3000);
            var point = new Point(500, 350);
            canvas.Pointer.Down(MouseButton.Middle, point);
            canvas.Pointer.Move(point);
            canvas.Pointer.Up(MouseButton.Middle, point);
            canvas.Pointer.Wheel(point, 0);
            var after = canvas.CaptureCamera();
            gate.Release();
            var arrived = await going;
            Check($"a middle click and a wheel notch that move nothing are not the user moving the view ({userMoves} moves, camera {(after == before ? "unchanged" : "changed")})",
                userMoves == 0 && after == before);
            Check($"so the flight asked for before them still lands ({arrived}, at {canvas.CaptureCamera()?.AnchorPath})",
                arrived && canvas.CaptureCamera()?.AnchorPath == @"Q:\slow\child");
        }
        finally
        {
            disk.Hook = null;
            gate.Release();
            canvas.Tree = null;
        }
    }

    // ---- the hover after the wheel (J081) ------------------------------------------------------

    /// <summary>
    /// The pointer resting on a folder's name pill, and the wheel turned
    /// under it: the outline and the tag go to what is under the pointer
    /// once the view has moved, not to the folder whose name was there
    /// before - which the names drawn for the old view still said.
    /// </summary>
    private static async Task CanvasMotionHoverAfterWheelAsync()
    {
        Section("canvas motion: the hover after the wheel is what is under the pointer");
        var disk = new FakeDisk();
        for (var index = 0; index < 160; index++)
        {
            disk.AddFiles($@"Q:\top\f{index:D3}", 2, "x");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.UpdateLayout();
        var hoverField = typeof(NestedCanvas).GetField("_hover", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var grabsField = typeof(NestedCanvas).GetField("_labelHotspots", BindingFlags.Instance | BindingFlags.NonPublic)!;
        NestedHit? Hover() => (NestedHit?)hoverField.GetValue(canvas);
        static string Name(NestedHit? hit) => hit is { } shown ? $"{shown.Folder.Name}{(shown.FileIndex >= 0 ? $" #{shown.FileIndex}" : string.Empty)}" : "nothing";
        var time = TimeSpan.FromSeconds(1000);
        try
        {
            canvas.FlyTo(tree.Find(@"Q:\top")!, 0.92, animated: false);
            time = await MotionFramesAsync(canvas, time, 2);

            // A name pill well inside the view, with room below it for the
            // view to move up past it.
            Point? on = null;
            NestedFolder? named = null;
            foreach (var grab in (System.Collections.IEnumerable)grabsField.GetValue(canvas)!)
            {
                var bounds = (Rect)grab.GetType().GetProperty("Bounds")!.GetValue(grab)!;
                var folder = (NestedFolder)grab.GetType().GetProperty("Folder")!.GetValue(grab)!;
                var centre = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                if (folder.Parent?.Name == "top" && centre.X > 100 && centre.X < 900 && centre.Y > 100 && centre.Y < 400)
                {
                    on = centre;
                    named = folder;
                    break;
                }
            }

            if (on is not { } point || named is null)
            {
                Check("a folder's name pill is drawn to rest the pointer on", false);
                return;
            }

            canvas.Pointer.Move(point);
            var first = Hover();
            canvas.Pointer.Wheel(point, -240);
            time = await MotionFramesAsync(canvas, time, 1);
            var afterWheel = Hover();
            canvas.Pointer.Move(point);
            var underPointer = Hover();
            Console.WriteLine($"        on {named.Name}'s name: hover {Name(first)}; after the wheel and a frame {Name(afterWheel)}; what is under the pointer {Name(underPointer)}");
            Check($"the pointer on a folder's name hovers that folder ({Name(first)})", first is { } was && ReferenceEquals(was.Folder, named));
            Check($"after the wheel, once the view is drawn, the hover is what is under the pointer ({Name(afterWheel)}, under it {Name(underPointer)})",
                !(underPointer is { } under && ReferenceEquals(under.Folder, named) && under.FileIndex < 0)
                && afterWheel is { } now && underPointer is { } truth
                && ReferenceEquals(now.Folder, truth.Folder) && now.FileIndex == truth.FileIndex);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- the folder in view after a move (J082) ----------------------------------------------

    /// <summary>
    /// The view inside a folder, on one of its sub-folders, then panned
    /// until the folder's edge comes into it: asked straight after the move -
    /// as the sort headers ask on every move of the camera - the folder in
    /// view is the one the view is in now, not the one the last picture had.
    /// </summary>
    private static async Task CanvasMotionFolderInViewAsync()
    {
        Section("canvas motion: the folder in view straight after a move is where the view is now");
        var disk = new FakeDisk();
        for (var index = 0; index < 9; index++)
        {
            disk.Folder($@"Q:\outer\c{index}");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.UpdateLayout();
        var outer = tree.Find(@"Q:\outer")!;
        var time = TimeSpan.FromSeconds(1000);
        string? atMove = null;
        var moving = false;
        try
        {
            // The sub-folder nearest the middle of the folder, two thirds of the view wide.
            canvas.FlyTo(outer, 0.92, animated: false);
            time = await MotionFramesAsync(canvas, time, 2);
            var whole = canvas.ScreenRectOf(outer)!.Value;
            var centre = new Point(whole.X + whole.Width / 2, whole.Y + whole.Height / 2);
            var inner = outer.Children.OrderBy(child => canvas.ScreenRectOf(child) is { } cell
                ? Math.Pow(cell.X + cell.Width / 2 - centre.X, 2) + Math.Pow(cell.Y + cell.Height / 2 - centre.Y, 2)
                : double.MaxValue).First();
            canvas.FlyTo(inner, 0.65, animated: false);
            time = await MotionFramesAsync(canvas, time, 2);
            var inside = canvas.FolderInView;
            var around = canvas.ScreenRectOf(outer)!.Value;

            // As the sort headers do: asked on the camera's move.
            canvas.CameraChanged += () =>
            {
                if (moving)
                {
                    atMove ??= canvas.FolderInView?.FullPath ?? "This PC";
                }
            };

            // Panned until the folder's left edge is a little way into the view.
            moving = true;
            canvas.Pan(new Vector(60 - around.Left, 0));
            moving = false;
            time = await MotionFramesAsync(canvas, time, 1);
            var drawn = canvas.FolderInView?.FullPath ?? "This PC";
            Console.WriteLine($"        inside {inside?.FullPath} on {inner.Name}; panned its edge in: at the move {atMove}, once drawn {drawn}");
            Check($"inside the folder, it is the folder in view ({inside?.FullPath})", ReferenceEquals(inside, outer));
            Check($"straight after the move the folder in view is the one drawn next ({atMove}, drawn {drawn})",
                atMove is not null && atMove == drawn && drawn != outer.FullPath);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- a camera put back from the last session (J023) ----------------------------------------

    /// <summary>
    /// A camera saved fifteen folders deep, each folder on the way holding
    /// forty others, put back on a tree that has read nothing yet.  It is
    /// back as soon as the folders on the way are found by name - none of
    /// them listed first, a frame or more each - and the folders around it
    /// are listed afterwards, so the view ends as it always did.  Moved by
    /// the user before it is back, it lets go of the reads it was waiting
    /// for rather than reading the whole way down regardless.
    /// </summary>
    private static async Task CanvasMotionRestoreAsync()
    {
        Section("canvas motion: a camera put back from the last session is back at once");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerCameraRestore", Guid.NewGuid().ToString("N"));
        var levels = new List<string>();
        var current = root;
        for (var level = 0; level < 15; level++)
        {
            for (var sibling = 0; sibling < 40; sibling++)
            {
                Directory.CreateDirectory(Path.Combine(current, $"s{sibling:D2}"));
            }

            current = Path.Combine(current, $"L{level:D2}");
            Directory.CreateDirectory(current);
            levels.Add(current);
        }

        var target = levels[^1];
        var listed = 0;
        NestedListing Read(string path, CancellationToken token)
        {
            Interlocked.Increment(ref listed);
            return NestedDirectoryReader.Read(path, token);
        }

        // Every folder on the way: the root, and each level above the one saved.
        var onTheWay = levels.Take(levels.Count - 1).Prepend(root).ToList();
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.UpdateLayout();
        var state = new NestedCameraState(target, -0.3, -0.25, 0.6);
        try
        {
            // Left alone: back before anything is listed, and the folders
            // around it listed after.
            using (var tree = new NestedTree(Read) { IsReadingOnDemand = false })
            {
                tree.SetRoots([new NestedRoot(root, "N", NestedFolderKind.Drive)]);
                canvas.Tree = tree;
                canvas.FitAll(animated: false);
                var listedWhenBack = -1;
                var watch = Stopwatch.StartNew();
                var backAfter = 0.0;
                void OnCamera()
                {
                    if (listedWhenBack < 0 && canvas.CaptureCamera() is { } camera && camera.AnchorPath == target)
                    {
                        listedWhenBack = Volatile.Read(ref listed);
                        backAfter = watch.Elapsed.TotalMilliseconds;
                    }
                }

                canvas.CameraChanged += OnCamera;
                await canvas.RestoreCameraAsync(state);
                canvas.CameraChanged -= OnCamera;
                var back = canvas.CaptureCamera();
                await WaitUntil(() => onTheWay.All(path => tree.Find(path) is { IsLoaded: true }), 10_000);
                var around = onTheWay.Count(path => tree.Find(path) is { IsLoaded: true });
                Console.WriteLine($"        15 deep: back after {backAfter:0} ms with {listedWhenBack} folders listed first; then {around} of the {onTheWay.Count} folders on the way listed");
                Check($"the camera is back before any folder on the way is listed ({listedWhenBack} listed first)", listedWhenBack == 0);
                Check($"where it was saved ({back?.AnchorPath})",
                    back is { } kept && kept.AnchorPath == target && Math.Abs(kept.Width - state.Width) < 1e-9 && Math.Abs(kept.X - state.X) < 1e-9 && Math.Abs(kept.Y - state.Y) < 1e-9);
                Check($"and the folders on the way are listed afterwards, as they always were ({around} of {onTheWay.Count})", around == onTheWay.Count);
                Check("without moving it", canvas.CaptureCamera() == back);
            }

            // Moved before it is back: nothing more is read for it.
            using (var tree = new NestedTree(Read) { IsReadingOnDemand = false })
            {
                tree.SetRoots([new NestedRoot(root, "N", NestedFolderKind.Drive)]);
                canvas.Tree = tree;
                canvas.FitAll(animated: false);
                Volatile.Write(ref listed, 0);
                var restore = canvas.RestoreCameraAsync(state);
                canvas.Pan(new Vector(30, 0));
                await restore;
                await Task.Delay(1_500);
                var afterMove = Volatile.Read(ref listed);
                Console.WriteLine($"        moved at once: {afterMove} folders listed for it afterwards");
                Check($"moved before it is back, the folders on the way are not read for it ({afterMove} listed)", afterMove == 0);
                Check($"and the camera stays where the user put it ({canvas.CaptureCamera()?.AnchorPath ?? "This PC"})", canvas.CaptureCamera()?.AnchorPath != target);
            }
        }
        finally
        {
            canvas.Tree = null;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder.
            }
        }
    }

    // ---- the loop letting go at rest (J006) -------------------------------------------------

    /// <summary>
    /// A canvas in a window on the secondary monitor - shown, never active -
    /// zoomed in a burst of wheel steps a dozen times, each followed by a
    /// second of rest.  The loop must not let go of WPF's Rendering in the
    /// frame that drew the settled picture: WPF then leaves its interlocked
    /// presentation with that frame's commit still waiting, and blocks the
    /// UI thread until the render thread has shown it.  Every dispatcher
    /// operation is timed - the longest one just after each letting go is
    /// that block - and an Input-priority probe posted every few milliseconds
    /// from another thread says how long input waited.
    /// </summary>
    private static async Task CanvasMotionRestLetGoAsync()
    {
        Section("canvas motion: at rest the loop lets go of WPF's frames without blocking the thread");
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  (no secondary monitor: skipped)");
            return;
        }

        var disk = new FakeDisk();
        for (var index = 0; index < 160; index++)
        {
            disk.Folder($@"Q:\big\s{index:D3}\a");
            disk.AddFiles($@"Q:\big\s{index:D3}", 12, "f");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        var window = new Window
        {
            Content = canvas,
            Title = "UltraExplorer canvas motion: rest",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1000,
            Height = 700
        };

        var dispatcher = canvas.Dispatcher;
        var hookedField = typeof(NestedCanvas).GetField("_hookedToRendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        var started = new Dictionary<DispatcherOperation, double>();
        var letGo = new List<(double At, bool InDrawingFrame)>();
        var operations = new List<(double At, double Ms)>();
        var wasHooked = false;
        var lastLoop = 0L;
        DispatcherHookEventHandler onStarted = (_, e) => started[e.Operation] = clock.Elapsed.TotalMilliseconds;
        DispatcherHookEventHandler onCompleted = (_, e) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            var hooked = (bool)hookedField.GetValue(canvas)!;
            var loop = canvas.LoopFrameCount;
            if (started.Remove(e.Operation, out var begun))
            {
                operations.Add((begun, now - begun));
                if (wasHooked && !hooked)
                {
                    // Let go of in this operation: in the frame that drew,
                    // if the loop ran a frame in it and that frame drew.
                    letGo.Add((now, loop != lastLoop && !canvas.LastFrameStats.Skipped));
                }
            }

            wasHooked = hooked;
            lastLoop = loop;
        };

        var probes = new List<(double At, double Waited)>();
        var probing = true;
        var probePending = 0;
        long probePosted = 0;
        Action onProbe = () =>
        {
            var waited = (Stopwatch.GetTimestamp() - Volatile.Read(ref probePosted)) * 1000.0 / Stopwatch.Frequency;
            lock (probes)
            {
                probes.Add((clock.Elapsed.TotalMilliseconds - waited, waited));
            }

            Volatile.Write(ref probePending, 0);
        };

        var prober = new Thread(() =>
        {
            while (Volatile.Read(ref probing))
            {
                Thread.Sleep(4);
                if (Interlocked.Exchange(ref probePending, 1) == 0)
                {
                    Volatile.Write(ref probePosted, Stopwatch.GetTimestamp());
                    dispatcher.BeginInvoke(DispatcherPriority.Input, onProbe);
                }
            }
        }) { IsBackground = true, Name = "input probe" };

        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                SetWindowPos(handle, 0, target.Work.Left + 40, target.Work.Top + 40, 1000, 700, 0x0004 | 0x0010); // no z-order change, no activation
                window.Show();
            }

            canvas.FlyTo(tree.Find(@"Q:\big")!, 0.92, animated: false);
            await Until(() => canvas.IsIdle && !canvas.IsFrameHooked, 10_000);
            await Task.Delay(1_500);
            dispatcher.Hooks.OperationStarted += onStarted;
            dispatcher.Hooks.OperationCompleted += onCompleted;
            dispatcher.Hooks.OperationAborted += onCompleted;
            prober.Start();

            const int Rests = 12;
            var stops = new List<double>();
            for (var rest = 0; rest < Rests; rest++)
            {
                // A burst of wheel steps, in and then out again, a frame apart.
                var zoomIn = rest % 2 == 0;
                for (var step = 0; step < 12; step++)
                {
                    canvas.ZoomAt(new Point(500, 350), zoomIn ? 1.06 : 1 / 1.06);
                    await Task.Delay(16);
                }

                stops.Add(clock.Elapsed.TotalMilliseconds);
                await Task.Delay(1_200);
            }

            Volatile.Write(ref probing, false);
            await Task.Delay(50);
            var quiet = !canvas.IsFrameHooked && !(bool)hookedField.GetValue(canvas)!;
            var worst = new List<double>();
            lock (probes)
            {
                foreach (var stop in stops)
                {
                    worst.Add(probes.Where(probe => probe.At >= stop && probe.At < stop + 800).Select(probe => probe.Waited).DefaultIfEmpty(0).Max());
                }
            }

            // The thread blocked just after letting go: the longest operation
            // that began within a fifth of a second of it.
            var blocked = letGo
                .Select(entry => operations.Where(op => op.At >= entry.At && op.At < entry.At + 200).Select(op => op.Ms).DefaultIfEmpty(0).Max())
                .Order()
                .ToList();
            worst.Sort();
            var restsLetGo = letGo.Count;
            var inDrawing = letGo.Count(entry => entry.InDrawingFrame);
            Console.WriteLine($"        {Rests} rests: the loop let go {restsLetGo} times, {inDrawing} of them in the frame that drew; the thread was blocked just after letting go "
                + $"{(blocked.Count == 0 ? 0 : blocked[blocked.Count / 2]):0} ms (median), {(blocked.Count == 0 ? 0 : blocked[^1]):0} ms at most, 50 ms or more in {blocked.Count(ms => ms >= 50)} of {blocked.Count}");
            Console.WriteLine($"        input waited at most, per rest: median {worst[worst.Count / 2]:0.0} ms, max {worst[^1]:0.0} ms, over 100 ms in {worst.Count(wait => wait > 100)} of {worst.Count}");
            Check($"the loop let go at every rest ({restsLetGo} of {Rests}), and lets go of WPF's frames altogether at the end ({quiet})",
                restsLetGo >= Rests && quiet);
            Check($"never in the frame that drew the settled picture, while WPF still had that frame to show ({inDrawing} of {restsLetGo})",
                inDrawing == 0);
            Check($"so letting go no longer blocks the thread for 50 ms or more, but at a rare rest ({blocked.Count(ms => ms >= 50)} of {blocked.Count})",
                blocked.Count(ms => ms >= 50) <= Rests / 4);
        }
        finally
        {
            Volatile.Write(ref probing, false);
            dispatcher.Hooks.OperationStarted -= onStarted;
            dispatcher.Hooks.OperationCompleted -= onCompleted;
            dispatcher.Hooks.OperationAborted -= onCompleted;
            window.Close();
            canvas.Tree = null;
        }
    }

    // ---- an arrow key held down (J002) ---------------------------------------------------------

    private const double MotionViewWidth = 1200;
    private const double MotionViewHeight = 800;

    /// <summary>
    /// A folder of fifteen thousand files, a file clicked and flown to where
    /// its name can be read, then Down held for three seconds and Right for
    /// one, at the keyboard's repeat rate.  Each repeat brings the next tile
    /// into view: it may run a row past the edge between two repeats, never
    /// more, and the tiles keep the height the first press gave them.
    /// </summary>
    private static async Task CanvasMotionKeyRepeatAsync()
    {
        Section("canvas motion: an arrow key held down keeps the item on screen, at its zoom");
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\many", 15_000, "f");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };
        canvas.Measure(new Size(MotionViewWidth, MotionViewHeight));
        canvas.Arrange(new Rect(0, 0, MotionViewWidth, MotionViewHeight));
        canvas.UpdateLayout();
        var many = tree.Find(@"Q:\many")!;
        var time = TimeSpan.FromSeconds(1000);
        string? selected = null;
        canvas.SelectRequested += (path, _) => selected = path;
        try
        {
            foreach (var (key, seconds) in new[] { (Key.Down, 3.0), (Key.Right, 1.0) })
            {
                canvas.FlyTo(many, 0.92, animated: false);
                time = await MotionFramesAsync(canvas, time, 1);
                var cell = canvas.ScreenRectOf(many)!.Value;
                canvas.Pointer.Click(TilePoint(cell, many, 0));
                selected = null;

                // The first press flies to where the next file can be read.
                canvas.HandleKey(key, ModifierKeys.None);
                time = await MotionSettleAsync(canvas, time);
                var first = MotionTile(canvas, many, selected);
                if (first is not { } start)
                {
                    Check($"{key}: the first press selects a file ({selected ?? "nothing"})", false);
                    continue;
                }

                var rowStep = key == Key.Down
                    ? many.FileGrid.StepY / many.FileGrid.TileHeight * start.Height
                    : many.FileGrid.StepX / many.FileGrid.TileWidth * start.Width;
                var worstOutside = 0.0;
                var smallest = start.Height;
                var presses = 0;
                var held = Stopwatch.StartNew();
                var nextPress = 0.0;
                while (held.Elapsed.TotalSeconds < seconds)
                {
                    if (held.Elapsed.TotalMilliseconds >= nextPress)
                    {
                        // Just before the repeat: how far the tile the last one
                        // moved to has got from the view.
                        if (presses > 0 && MotionTile(canvas, many, selected) is { } before)
                        {
                            worstOutside = Math.Max(worstOutside, Outside(before));
                            smallest = Math.Min(smallest, before.Height);
                        }

                        canvas.HandleKey(key, ModifierKeys.None);
                        presses++;
                        nextPress += 33;
                    }

                    canvas.RunFrameForTests(time += Frame);
                    await Task.Delay(1);
                }

                time = await MotionSettleAsync(canvas, time);
                var end = MotionTile(canvas, many, selected);
                Console.WriteLine($"        {key} held {seconds:0} s, {presses} repeats: the tile was at most {worstOutside:0} DIPs outside the view (a step is {rowStep:0}), "
                    + $"its height {start.Height:0.0} -> {smallest:0.0} while held, {end?.Height:0.0} at rest; at rest it is at {end?.X:0},{end?.Y:0}");
                Check($"{key} held: between repeats the selected tile is never more than a step outside the view ({worstOutside:0} DIPs, a step {rowStep:0})",
                    worstOutside <= rowStep + 1);
                Check($"{key} held: the tiles keep the height the first press gave them ({start.Height:0.0} DIPs, smallest {smallest:0.0})",
                    smallest >= start.Height * 0.97 && end is { } last && Math.Abs(last.Height - start.Height) <= start.Height * 0.03);
                Check($"{key} let go: the view comes to rest with the selected tile inside it ({end?.X:0},{end?.Y:0} {end?.Width:0}x{end?.Height:0})",
                    end is { } rest && rest.Left >= -1 && rest.Top >= -1 && rest.Right <= MotionViewWidth + 1 && rest.Bottom <= MotionViewHeight + 1);
            }
        }
        finally
        {
            canvas.Tree = null;
        }

        static double Outside(Rect tile) => Math.Max(
            Math.Max(-tile.Top, tile.Bottom - MotionViewHeight),
            Math.Max(-tile.Left, tile.Right - MotionViewWidth));
    }

    /// <summary>The tile of the file at <paramref name="path"/> on screen, or null.</summary>
    private static Rect? MotionTile(NestedCanvas canvas, NestedFolder folder, string? path)
    {
        if (path is null || canvas.Resolve(path) is not { FileIndex: >= 0 } found || !ReferenceEquals(found.Folder, folder)
            || canvas.ScreenRectOf(folder) is not { } cell)
        {
            return null;
        }

        var grid = folder.FileGrid;
        var (x, y) = grid.Origin(found.FileIndex);
        return new Rect(cell.X + x * cell.Width, cell.Y + y * cell.Width, grid.TileWidth * cell.Width, grid.TileHeight * cell.Width);
    }

    /// <summary>Frames until the camera has stopped and settled: returns the time of the last.</summary>
    private static async Task<TimeSpan> MotionSettleAsync(NestedCanvas canvas, TimeSpan time)
    {
        var watch = Stopwatch.StartNew();
        while (canvas.IsCameraMoving && watch.ElapsedMilliseconds < 5_000)
        {
            canvas.RunFrameForTests(time += Frame);
            await Task.Delay(1);
        }

        return await MotionFramesAsync(canvas, time, 2);
    }

    private static async Task<TimeSpan> MotionFramesAsync(NestedCanvas canvas, TimeSpan time, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            canvas.RunFrameForTests(time += Frame);
            await Task.Delay(1);
        }

        return time;
    }
}
