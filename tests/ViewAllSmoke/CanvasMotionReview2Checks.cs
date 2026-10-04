using System.Diagnostics;
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
