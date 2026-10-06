using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>Plain F frames one exact selected leaf, without turning it into navigation or touching another pane.</summary>
internal static partial class Program
{
    private const string FocusSelectionChild = "ULTRAEXPLORER_FOCUS_SELECTION_CHILD";

    private static async Task FocusSelectionChecks()
    {
        if (Environment.GetEnvironmentVariable(FocusSelectionChild) == "1")
        {
            RunOnSta("focus selection", FocusSelectionOnStaAsync);
            return;
        }

        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerFocusSelectionChild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(nameof(FocusSelectionChecks));
            start.Environment[FocusSelectionChild] = "1";
            start.Environment["ULTRAEXPLORER_STATE_DIR"] = Path.Combine(owned, "state");
            start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
            start.Environment["TEMP"] = Path.Combine(owned, "temp");
            start.Environment["TMP"] = Path.Combine(owned, "temp");
            Directory.CreateDirectory(start.Environment["TEMP"]!);
            child = Process.Start(start)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var finished = false;
            while (await child.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line.EndsWith(" checks passed", StringComparison.Ordinal)) finished = true;
                else if (line.Length > 0) Console.WriteLine(line);
            }
            await child.WaitForExitAsync(limit.Token);
            // Every child FAIL was already counted above; this checks only
            // that its output reached the normal end, never counts one twice.
            Check("the focus-selection checks finish in their isolated process", finished);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            Check("the focus-selection checks finish within five minutes", false);
        }
        finally
        {
            child?.Dispose();
            TryDelete(owned);
        }
    }

    private static async Task FocusSelectionOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerFocusSelection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        try
        {
            await FocusCanvasChecksAsync();
            await FocusWindowChecksAsync(owned);
        }
        finally
        {
            TryDelete(owned);
        }
    }

    private static async Task FocusCanvasChecksAsync()
    {
        Section("focus selection: exact nested leaf and stale-request guards");
        var disk = new FakeDisk();
        const string docsPath = @"Q:\docs";
        const string otherPath = @"Q:\other";
        const string folderPath = @"Q:\docs\folder";
        const string hiddenFolderPath = @"Q:\hidden-folder";
        const string capPath = @"Q:\cap";
        const string unloadedPath = @"Q:\unloaded";
        disk.Folder(folderPath);
        disk.Folder(otherPath);
        disk.Folder(hiddenFolderPath).IsHidden = true;
        disk.Folder(unloadedPath);
        disk.AddFile(unloadedPath, "late.txt", 19);
        disk.AddFile(docsPath, "selected.txt", 41);
        disk.AddFile(docsPath, "other.txt", 42);
        disk.AddFile(docsPath, "visible.png", 43);
        disk.AddFile(docsPath, "hidden.txt", 44, hidden: true);
        disk.AddFiles(otherPath, 8, "elsewhere-");
        var capDisk = disk.Folder(capPath);
        capDisk.UnlistedFiles = 6;
        disk.AddFile(capPath, "alpha.txt", 1);
        disk.AddFile(capPath, "zulu.txt", 2);

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var drive = tree.Find(@"Q:\")!;
        await tree.LoadAsync(drive);
        var docs = tree.Find(docsPath)!;
        var other = tree.Find(otherPath)!;
        var cap = tree.Find(capPath)!;
        await tree.LoadAsync(docs);
        await tree.LoadAsync(other);
        await tree.LoadAsync(cap);

        var canvas = FocusCanvas(tree);
        var selected = Path.Combine(docsPath, "selected.txt");
        var focused = await canvas.FocusPathAsync(selected, isDirectory: false, animated: false);
        Check("F frames a selected file from the This PC overview", focused);
        Check("the selected file card is centred and fills one 75% viewport constraint",
            FocusFileRect(canvas, tree, selected) is { } selectedRect && FocusCentred(selectedRect, canvas, 0.75));

        canvas.FlyTo(other, 0.2, animated: false);
        Check("F brings the same file back after the user zoomed elsewhere",
            await canvas.FocusPathAsync(selected, false, animated: false)
            && FocusFileRect(canvas, tree, selected) is { } returned && FocusCentred(returned, canvas, 0.75));

        var readsBeforeFolder = disk.Reads;
        var folder = tree.Find(folderPath)!;
        Check("a selected folder is centred at 88% without enumerating its contents",
            await canvas.FocusPathAsync(folderPath, true, animated: false)
            && canvas.ScreenRectOf(folder) is { } folderRect && FocusCentred(folderRect, canvas, 0.88)
            && !folder.IsLoaded && disk.Reads == readsBeforeFolder);

        var hiddenFolder = tree.Find(hiddenFolderPath)!;
        Check("an explicitly focused hidden folder is made visible rather than framing its parent",
            await canvas.FocusPathAsync(hiddenFolderPath, true, animated: false)
            && NestedTree.IsOnCanvas(hiddenFolder) && ReferenceEquals(canvas.Anchor, hiddenFolder));

        var hiddenFile = Path.Combine(docsPath, "hidden.txt");
        Check("an explicitly focused hidden file gets its own centred tile without enabling every hidden file",
            await canvas.FocusPathAsync(hiddenFile, false, animated: false)
            && FocusFileRect(canvas, tree, hiddenFile) is { } hiddenRect && FocusCentred(hiddenRect, canvas, 0.75));

        tree.FileNameFilter = name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        var filteredOther = Path.Combine(docsPath, "other.txt");
        Check("explicit focus overrides the file-type filter for exactly the requested file",
            await canvas.FocusPathAsync(selected, false, animated: false)
            && tree.FindFileIndex(docs, "selected.txt") >= 0 && tree.FindFileIndex(docs, "other.txt") < 0);
        Check("that filter override still centres the exact requested tile",
            FocusFileRect(canvas, tree, selected) is { } filteredRect && FocusCentred(filteredRect, canvas, 0.75)
            && FocusFileRect(canvas, tree, filteredOther) is null);
        tree.FileNameFilter = null;

        canvas.FlyTo(other, 0.88, animated: false);
        var beforeMissing = canvas.CaptureCamera();
        Check("a missing selected leaf is a no-op, never a silent parent fallback",
            !await canvas.FocusPathAsync(Path.Combine(docsPath, "missing.txt"), false, animated: false)
            && canvas.CaptureCamera() == beforeMissing);

        var described = 0;
        var capTarget = Path.Combine(capPath, "middle.txt");
        tree.NamedFileDescribeForChecks = (path, _) =>
        {
            described++;
            return Task.FromResult(FocusDescriptor(path, 73));
        };
        var capCount = cap.FileCount;
        var capUnlisted = cap.UnlistedFileCount;
        var capTruncated = cap.IsTruncated;
        Check("a selected file omitted past the listing cap is described by its exact name and focused",
            await canvas.FocusPathAsync(capTarget, false, animated: false) && described == 1
            && FocusFileRect(canvas, tree, capTarget) is { } capRect && FocusCentred(capRect, canvas, 0.75));
        Check("the cap injection keeps name order, total count and unlisted count honest",
            cap.AllFiles.Select(file => file.Name).SequenceEqual(["alpha.txt", "middle.txt", "zulu.txt"], StringComparer.OrdinalIgnoreCase)
            && cap.FileCount == capCount && cap.UnlistedFileCount == capUnlisted - 1);
        var nextCap = Path.Combine(capPath, "november.txt");
        Check("a capped folder retains only one weak exact-leaf slot rather than growing forever",
            await canvas.FocusPathAsync(nextCap, false, animated: false)
            && NestedTree.HoldsFile(cap, "november.txt") && !NestedTree.HoldsFile(cap, "middle.txt")
            && cap.AllFiles.Length == 3 && cap.FileCount == capCount);

        var interleavedPath = Path.Combine(capPath, "oscar.txt");
        var descriptorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptorRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptorCalls = 0;
        tree.NamedFileDescribeForChecks = async (path, token) =>
        {
            if (Interlocked.Increment(ref descriptorCalls) == 1)
            {
                descriptorEntered.TrySetResult();
                await descriptorRelease.Task.WaitAsync(token);
            }
            return FocusDescriptor(path, 91);
        };
        var beforePatchLength = cap.AllFiles.Length;
        var interleaved = tree.EnsureNamedFileAsync(cap, interleavedPath);
        await descriptorEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        tree.OnFolderChanged(cap, new FolderChange(
            cap.FullPath,
            ChangeKinds.Content,
            0,
            ReadOnlyMemory<RenamePair>.Empty,
            new[] { new FileDelta("alpha.txt", 999, 638900000000000001) }));
        descriptorRelease.TrySetResult();
        Check("a live metadata patch during exact-leaf lookup preserves the patch and replaces, rather than grows, the capped slot",
            await interleaved && descriptorCalls == 2 && cap.AllFiles.Length == beforePatchLength
            && NestedTree.HoldsFile(cap, "oscar.txt") && !NestedTree.HoldsFile(cap, "november.txt")
            && cap.AllFiles.First(file => file.Name == "alpha.txt").Length == 999
            && cap.FileCount == capCount && cap.IsTruncated == capTruncated);

        tree.NamedFileDescribeForChecks = null;
        Check("an initially unloaded selected file loads only its parent and then frames its tile",
            await canvas.FocusPathAsync(Path.Combine(unloadedPath, "late.txt"), false, animated: false)
            && tree.Find(unloadedPath) is { IsLoaded: true } unloaded
            && FocusFileRect(canvas, tree, Path.Combine(unloadedPath, "late.txt")) is { } lateRect
            && FocusCentred(lateRect, canvas, 0.75));

        await FocusCancellationChecksAsync(canvas, tree, cap, selected);
        canvas.Tree = null;
    }

    private static async Task FocusCancellationChecksAsync(NestedCanvas canvas, NestedTree tree, NestedFolder cap, string readyPath)
    {
        var sequence = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ViewAllEntryDescriptor> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.NamedFileDescribeForChecks = async (path, token) =>
        {
            entered.TrySetResult();
            return await answer.Task.WaitAsync(token);
        };
        string Pending(string name)
        {
            cap.UnlistedFileCount = Math.Max(1, cap.UnlistedFileCount);
            cap.FileCount = Math.Max(cap.FileCount, cap.AllFiles.Length + 1);
            return Path.Combine(cap.FullPath, name);
        }
        void NextGate()
        {
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sequence++;
        }

        var oldPath = Pending("late-old.txt");
        var old = canvas.FocusPathAsync(oldPath, false, animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var latest = await canvas.FocusPathAsync(readyPath, false, animated: false);
        answer.TrySetResult(FocusDescriptor(oldPath, sequence));
        Check("a newer focus request cancels an older metadata wait and is the camera that lands",
            !await old && latest && FocusFileRect(canvas, tree, readyPath) is { } latestRect && FocusCentred(latestRect, canvas, 0.75));

        NextGate();
        var panPath = Pending("late-pan.txt");
        var panning = canvas.FocusPathAsync(panPath, false, animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        canvas.Pan(new Vector(23, -11));
        var handCamera = canvas.CaptureCamera();
        answer.TrySetResult(FocusDescriptor(panPath, sequence));
        Check("a user camera move prevents a late focus from jumping back",
            !await panning && canvas.CaptureCamera() == handCamera);

        NextGate();
        var current = true;
        var stalePath = Pending("late-selection.txt");
        var stale = canvas.FocusPathAsync(stalePath, false, () => current, animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var beforeStale = canvas.CaptureCamera();
        current = false;
        answer.TrySetResult(FocusDescriptor(stalePath, sequence));
        Check("a request-current guard rejects a focus whose selection or pane changed meanwhile",
            !await stale && canvas.CaptureCamera() == beforeStale && !NestedTree.HoldsFile(cap, "late-selection.txt"));
        tree.NamedFileDescribeForChecks = null;
    }

    private static async Task FocusWindowChecksAsync(string owned)
    {
        Section("focus selection: window target, active pane and keyboard arbitration");
        var drive = Path.Combine(owned, "drive");
        var left = Path.Combine(drive, "left");
        var right = Path.Combine(drive, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var firstPath = Path.Combine(left, "first.txt");
        var focusedPath = Path.Combine(left, "focused.txt");
        var rightPath = Path.Combine(right, "right.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(focusedPath, "focused");
        File.WriteAllText(rightPath, "right");

        var main = new MainWindow(null, Path.Combine(owned, "window.workspace.json"));
        var shell = (MainViewModel)main.DataContext;
        try
        {
            shell.Layout = CanvasLayout.Nested;
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "Owned", NestedFolderKind.Drive)]);
            LayOutWindow(main, 1400, 900);
            var first = main.FirstPane;
            await FocusLoadAsync(first, drive, left);
            var selection = shell.Tree.Selection;
            selection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(firstPath, false, 5), new SelectionItem(focusedPath, false, 7)],
                Anchor = firstPath,
                Focus = focusedPath,
                Source = SelectionSource.List
            });
            first.SyncSelection();
            first.Canvas.FitAll(animated: false);
            var paths = selection.Paths.ToArray();
            var version = selection.Version;
            var anchor = selection.Anchor;
            var focus = selection.Focus;
            var historyCount = first.History.Count;
            var historyCurrent = first.History.Current;
            Check("window F chooses the contained selection focus, not its parent or active graph node",
                await main.FocusSelectionAsync(animated: false)
                && FocusFileRect(first.Canvas, first.Tree, focusedPath) is { } exact && FocusCentred(exact, first.Canvas, 0.75));
            Check("focusing changes no selected path, version, anchor, focus or history",
                selection.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) && selection.Version == version
                && selection.Anchor == anchor && selection.Focus == focus
                && first.History.Count == historyCount && first.History.Current == historyCurrent);

            selection.Apply(new SelectionEdit { Focus = Path.Combine(left, "not-selected.txt"), Source = SelectionSource.Command });
            first.Canvas.FitAll(animated: false);
            Check("when Focus is not selected, F deterministically frames the first selected item",
                await main.FocusSelectionAsync(animated: false)
                && FocusFileRect(first.Canvas, first.Tree, firstPath) is { } fallback && FocusCentred(fallback, first.Canvas, 0.75));

            shell.Layers = CanvasLayer.All & ~CanvasLayer.Files;
            selection.ReplaceSingle(focusedPath, false, 7, SelectionSource.Canvas);
            first.SyncSelection();
            Check("with Files off, F locally reveals and frames the selected file without changing the persisted layer preference",
                !shell.Layers.HasFlag(CanvasLayer.Files) && await main.FocusSelectionAsync(animated: false)
                && first.Canvas.ShownLayers.HasFlag(CanvasLayer.Files) && !shell.Layers.HasFlag(CanvasLayer.Files)
                && FocusFileRect(first.Canvas, first.Tree, focusedPath) is { } layerRect && FocusCentred(layerRect, first.Canvas, 0.75));

            shell.IsSplit = true;
            var second = main.SecondPane;
            Check("the focus fixture opens a second pane", second is not null);
            if (second is null) return;
            await FocusLoadAsync(second, drive, right);
            main.ActivatePane(first);
            first.Canvas.FlyTo(first.Tree.Find(left)!, 0.4, animated: false);
            var firstCamera = first.Canvas.CaptureCamera();
            main.ActivatePane(second);
            selection.ReplaceSingle(rightPath, false, 5, SelectionSource.Canvas);
            second.SyncSelection();
            Check("F frames only the active pane and leaves the inactive pane camera untouched",
                await main.FocusSelectionAsync(animated: false) && first.Canvas.CaptureCamera() == firstCamera
                && FocusFileRect(second.Canvas, second.Tree, rightPath) is { } rightRect && FocusCentred(rightRect, second.Canvas, 0.75));

            await FocusWindowStaleChecksAsync(main, shell, first, second, right);
            await FocusKeyboardChecksAsync(main, shell, first, focusedPath);
        }
        finally
        {
            shell.Dispose();
        }
    }

    private static async Task FocusWindowStaleChecksAsync(MainWindow main, MainViewModel shell,
        NestedPane first, NestedPane second, string parentPath)
    {
        main.ActivatePane(second);
        var parent = second.Tree.Find(parentPath)!;
        parent.UnlistedFileCount = Math.Max(1, parent.UnlistedFileCount);
        parent.FileCount = Math.Max(parent.FileCount, parent.AllFiles.Length + 2);
        var selection = shell.Tree.Selection;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ViewAllEntryDescriptor> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Tree.NamedFileDescribeForChecks = async (path, token) =>
        {
            entered.TrySetResult();
            return await answer.Task.WaitAsync(token);
        };
        var staleSelectionPath = Path.Combine(parentPath, "selection-changed.txt");
        selection.ReplaceSingle(staleSelectionPath, false, 1, SelectionSource.Canvas);
        var before = second.Canvas.CaptureCamera();
        var focusing = main.FocusSelectionAsync(animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        selection.ReplaceSingle(Path.Combine(parentPath, "right.txt"), false, 1, SelectionSource.Canvas);
        answer.TrySetResult(FocusDescriptor(staleSelectionPath, 1));
        Check("changing the selection during an async focus prevents the stale camera jump and leaf injection",
            !await focusing && second.Canvas.CaptureCamera() == before
            && !NestedTree.HoldsFile(parent, "selection-changed.txt"));

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalePanePath = Path.Combine(parentPath, "pane-changed.txt");
        selection.ReplaceSingle(stalePanePath, false, 1, SelectionSource.Canvas);
        before = second.Canvas.CaptureCamera();
        focusing = main.FocusSelectionAsync(animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        main.ActivatePane(first);
        answer.TrySetResult(FocusDescriptor(stalePanePath, 2));
        Check("switching panes during an async focus keeps the new pane active and cancels the old pane jump",
            !await focusing && ReferenceEquals(main.ActivePane, first) && second.Canvas.CaptureCamera() == before
            && !NestedTree.HoldsFile(parent, "pane-changed.txt"));
        second.Tree.NamedFileDescribeForChecks = null;
    }

    private static async Task FocusKeyboardChecksAsync(MainWindow main, MainViewModel shell, NestedPane pane, string selectedPath)
    {
        main.ActivatePane(pane);
        shell.Tree.Selection.ReplaceSingle(selectedPath, false, 7, SelectionSource.Canvas);
        pane.SyncSelection();
        pane.Canvas.FramesByHandForTests = true;
        pane.Canvas.FitAll(animated: false);
        var takeoff = pane.Canvas.CaptureCamera();
        var marked = 0;
        var handled = main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: pane.Canvas,
            markHandled: () => marked++);
        Check("plain F on the selection surface is synchronously claimed before its async focus starts", handled && marked == 1);
        var flightField = typeof(NestedCanvas).GetField("_flight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await WaitUntil(() => flightField.GetValue(pane.Canvas) is not null, 3_000);
        Check("plain F starts the existing smooth camera flight instead of jumping at once",
            flightField.GetValue(pane.Canvas) is not null && pane.Canvas.CaptureCamera() == takeoff);
        var flight = flightField.GetValue(pane.Canvas)!;
        var startedField = flight.GetType().GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var durationField = flight.GetType().GetField("_duration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var duration = (double)durationField.GetValue(flight)!;
        var frameTime = TimeSpan.FromSeconds(100);
        startedField.SetValue(flight, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * duration * 0.5));
        pane.Canvas.RunFrameForTests(frameTime);
        var midway = pane.Canvas.CaptureCamera();
        Check("an intermediate F frame advances along the flight without already cutting to the final card",
            midway is not null && midway != takeoff
            && !(FocusFileRect(pane.Canvas, pane.Tree, selectedPath) is { } middleRect && FocusCentred(middleRect, pane.Canvas, 0.75)));
        startedField.SetValue(flight, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * duration * 2));
        pane.Canvas.RunFrameForTests(frameTime += TimeSpan.FromMilliseconds(16));
        Check("the smooth F flight settles on the exact selected leaf",
            flightField.GetValue(pane.Canvas) is null
            && FocusFileRect(pane.Canvas, pane.Tree, selectedPath) is { } rect && FocusCentred(rect, pane.Canvas, 0.75));

        pane.Canvas.Pan(new Vector(31, -17));
        var repeatedCamera = pane.Canvas.CaptureCamera();
        marked = 0;
        Check("a repeated F is claimed but does not start another focus",
            main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, isRepeat: true, inputOrigin: pane.Canvas,
                markHandled: () => marked++) && marked == 1 && pane.Canvas.CaptureCamera() == repeatedCamera
            && flightField.GetValue(pane.Canvas) is null);
        Check("Ctrl+F and Ctrl+Shift+F remain available to search and the pane filter",
            !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.Control, inputOrigin: pane.Canvas)
            && !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.Control | ModifierKeys.Shift, inputOrigin: pane.Canvas));
        Check("other modified F chords and unrelated controls do not invoke focus selection",
            !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.Shift, inputOrigin: pane.Canvas)
            && !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.Alt, inputOrigin: pane.Canvas)
            && !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: new Button()));

        var textInputs = new DependencyObject[]
        {
            new TextBox(), new RichTextBox(), new PasswordBox(), new ComboBox { IsEditable = true }
        };
        Check("F remains typing input in TextBox, RichTextBox, PasswordBox and an editable ComboBox",
            textInputs.All(MainWindow.IsFocusTextInput)
            && textInputs.All(input => !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: input)));

        var list = shell.Tree.FolderList;
        var rows = typeof(FolderListViewModel).GetField("_rowsVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var folder = typeof(FolderListViewModel).GetField("_folderVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        rows.SetValue(list, 0L);
        folder.SetValue(list, 1L);
        Check("F from stale folder-list rows is refused rather than framing an obsolete selection",
            !list.HasCurrentRows && !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: main.FolderListItems));
        rows.SetValue(list, 1L);

        shell.Tree.Selection.Clear(SelectionSource.Command);
        var noSelectionCamera = pane.Canvas.CaptureCamera();
        Check("with no selection, both the command and F gesture are no-ops",
            !await main.FocusSelectionAsync(animated: false)
            && !main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: pane.Canvas)
            && pane.Canvas.CaptureCamera() == noSelectionCamera);
    }

    private static NestedCanvas FocusCanvas(NestedTree tree)
    {
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        return canvas;
    }

    private static async Task FocusLoadAsync(NestedPane pane, string root, string folder)
    {
        var drive = pane.Tree.Find(root) ?? throw new InvalidOperationException("The owned focus root was not added.");
        await pane.Tree.LoadAsync(drive);
        var target = pane.Tree.Find(folder) ?? throw new InvalidOperationException("The owned focus folder was not listed.");
        await pane.Tree.LoadAsync(target);
        pane.Tree.EnsureLayout(target);
    }

    private static Rect? FocusFileRect(NestedCanvas canvas, NestedTree tree, string path)
    {
        if (Path.GetDirectoryName(path) is not { } parentPath || tree.Find(parentPath) is not { } folder) return null;
        tree.EnsureLayout(folder);
        var index = tree.FindFileIndex(folder, Path.GetFileName(path));
        if (index < 0 || index >= folder.FileGrid.Count || canvas.ScreenRectOf(folder) is not { } cell) return null;
        var grid = folder.FileGrid;
        var (x, y) = grid.Origin(index);
        return new Rect(cell.X + x * cell.Width, cell.Y + y * cell.Width,
            grid.TileWidth * cell.Width, grid.TileHeight * cell.Width);
    }

    private static bool FocusCentred(Rect rect, NestedCanvas canvas, double fill)
    {
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var extent = Math.Max(rect.Width / canvas.ActualWidth, rect.Height / canvas.ActualHeight);
        return Math.Abs(centre.X - canvas.ActualWidth / 2) < 0.05
            && Math.Abs(centre.Y - canvas.ActualHeight / 2) < 0.05
            && Math.Abs(extent - fill) < 0.005;
    }

    private static ViewAllEntryDescriptor FocusDescriptor(string path, long size) => new(
        path, Path.GetFileName(path), ViewAllEntryKind.File, false, false, size,
        new DateTime(638900000000000000, DateTimeKind.Utc));
}
