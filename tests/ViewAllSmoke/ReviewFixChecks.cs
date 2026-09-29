using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review of the last round of fixes found, and what was done about
/// it: a search result or a list row that has gone selects nothing rather
/// than its folder; a navigation asked for while an earlier one waits is the
/// one that wins, from the address bar too, and a step back or forward keeps
/// only its own selection out of the history; the canvas workspace is never
/// saved before it has been read, and the shares it lists are asked for once
/// the window is up, all at once, kept while they are out of reach and
/// forgotten once they have been for long enough; a frame that keeps failing
/// is logged and lets go of the loop until input or a pause; an overflow
/// storm is put off longer and longer; an ended watch is retried, never
/// polled for good; registrations moving roots take their counts along; a
/// folder list's first poll reads its folder once more; a batch deletes or
/// moves nothing twice; and a search result's date outside the calendar is
/// still written.
/// </summary>
internal static partial class Program
{
    private static async Task ReviewFixChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ReviewNestedBatchChecks(root);
            ReviewSearchDateChecks();
            await OnDispatcher(() => ReviewNavigationChecksAsync(root));
            await OnDispatcher(() => ReviewAddressRaceChecksAsync(root));
            await OnDispatcher(() => ReviewWorkspaceChecksAsync(root));
            await OnDispatcher(() => ReviewListPollChecksAsync(root));
            RunOnSta("frame containment", ReviewFrameContainmentAsync);
            ReviewOverflowChecks(root);
            ReviewEndedWatchChecks(root);
            ReviewRootMoveChecks();
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a batch of shell operations ---------------------------------------------------

    private static void ReviewNestedBatchChecks(string root)
    {
        Section("review: a batch holds nothing twice");
        var folder = Path.Combine(root, "batch");
        var inside = Path.Combine(folder, "inner", "deep.txt");
        var twin = Path.Combine(root, "batch-twin");
        var kept = NativeShellService.WithoutNested([inside, twin, folder, Path.Combine(folder, "inner")]);
        Check($"an item inside another of the batch is left to go with it ({string.Join(" | ", kept.Select(Path.GetFileName))})",
            kept.Length == 2 && kept[0] == twin && kept[1] == folder);
        Check("a name that only begins like a folder of the batch is not inside it",
            NativeShellService.WithoutNested([folder, twin]).Length == 2);
        Check("a separator at the end changes nothing",
            NativeShellService.WithoutNested([folder + Path.DirectorySeparatorChar, inside]) is [var only] && only.StartsWith(folder, StringComparison.Ordinal));
    }

    // ---- a search result's date --------------------------------------------------------

    private static void ReviewSearchDateChecks()
    {
        Section("review: a search result's date");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            SearchResultViewModel Hit(DateTime modified) => new("a.txt", @"C:\a.txt", @"C:\", false, 1, modified, SearchPlace.Here, [], @"C:\");
            Check("a result dated outside what the culture's calendar can say is still dated",
                Hit(new DateTime(1850, 3, 1)).DateText.Length > 0 && Hit(new DateTime(2107, 6, 1)).DateText.Length > 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            Check("a result dated outside what the culture's calendar can say is still dated", false);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ---- navigations -----------------------------------------------------------------

    private static async Task ReviewNavigationChecksAsync(string root)
    {
        Section("review: navigations");
        var folder = Path.Combine(root, "nav");
        var alpha = Path.Combine(folder, "alpha");
        var beta = Path.Combine(folder, "beta");
        var scratch = Path.Combine(root, "nav-state");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(beta, "kept.txt"), "kept");

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);

        // A result or a row whose item went: nothing is selected, not its folder.
        var kept = Path.Combine(beta, "kept.txt");
        await tree.RevealPathAsync(kept, focus: false);
        var messages = new List<string>();
        tree.MessageRequested += (message, _) => messages.Add(message);
        var gone = await tree.RevealAsync(Path.Combine(beta, "gone.txt"), focus: false, exact: true);
        Check("revealing only the exact path, a path that went gets as far as its folder",
            gone.Node is { } reached && ViewAllPath.Equals(reached.FullPath, beta) && !gone.IsExact);
        Check("and selects nothing new - the folder least of all - and says it went",
            tree.Selection.Count == 1 && tree.Selection.Contains(kept) && !tree.Selection.Contains(beta)
            && messages.LastOrDefault() == "gone.txt is no longer inside beta.");
        var there = await tree.RevealAsync(alpha, focus: false, exact: true);
        Check("a path that is there is selected as ever", there.IsExact && tree.Selection.Contains(alpha));

        // A ticket taken before an earlier wait loses to anything asked for since.
        var early = tree.BeginNavigation();
        await tree.RevealPathAsync(beta, focus: false);
        Check("a navigation begun earlier is no longer the latest once another starts", !tree.IsLatestNavigation(early));
        var late = await tree.RevealAsync(alpha, focus: false, ticket: early);
        Check("and, arriving later, selects nothing", late.Superseded && ViewAllPath.Equals(tree.ActivePath, beta));

        // A step back or forward keeps only its own selection out of the history.
        var recorded = new List<(string Path, bool Records)>();
        tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewAllViewModel.ActivePath))
            {
                recorded.Add((tree.ActivePath, tree.FocusRecordsNavigation));
            }
        };
        await tree.RevealAsync(alpha, focus: false, records: false);
        await tree.RevealAsync(beta, focus: false);
        Check($"a history step's selection is not a step of its own; the next navigation is ({string.Join(", ", recorded.Select(item => $"{Path.GetFileName(item.Path)}:{item.Records}"))})",
            recorded.Count == 2
            && ViewAllPath.Equals(recorded[0].Path, alpha) && !recorded[0].Records
            && ViewAllPath.Equals(recorded[1].Path, beta) && recorded[1].Records);
    }

    // ---- the address bar -------------------------------------------------------------

    private static async Task ReviewAddressRaceChecksAsync(string root)
    {
        Section("review: the address bar's Enter");
        var folder = Path.Combine(root, "address");
        var beta = Path.Combine(folder, "beta");
        Directory.CreateDirectory(beta);

        var clock = 0;
        var navigated = new List<(string Path, int Ticket)>();
        var messages = new List<string>();
        using var address = new AddressBarViewModel(
            (path, ticket) =>
            {
                navigated.Add((path, ticket));
                return Task.CompletedTask;
            },
            () => ++clock,
            ticket => ticket == clock,
            () => [],
            (message, _) => messages.Add(message));
        address.SetPath(folder);

        address.BeginEdit();
        address.Text = beta;
        address.GoCommand.Execute(null);
        var mine = clock;
        clock++;
        await Task.Delay(300);
        Check("an Enter overtaken by another navigation while its path was checked goes nowhere",
            navigated.Count == 0 && address.IsEditing && address.Text == beta && messages.Count == 0);

        address.GoCommand.Execute(null);
        address.Text = beta + "-and-more";
        await Task.Delay(300);
        Check("one answered after more was typed goes nowhere, and leaves the typing alone",
            navigated.Count == 0 && address.IsEditing && address.Text == beta + "-and-more");

        address.Text = beta;
        address.GoCommand.Execute(null);
        var ticket = clock;
        Check("the ticket is taken at Enter, before the path is checked", ticket > mine + 1);
        Check("otherwise it goes there, as the navigation it began",
            await AddressWaitFor(() => navigated.Count == 1) && navigated[0] == (beta, ticket) && !address.IsEditing);
    }

    // ---- the canvas workspace --------------------------------------------------------

    private static async Task ReviewWorkspaceChecksAsync(string root)
    {
        Section("review: the canvas workspace and its shares");
        var scratch = Path.Combine(root, "workspace-state");
        var extra = Path.Combine(root, "extra-share");
        var extraInner = Path.Combine(extra, "inner");
        var offline = Path.Combine(root, $"offline-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(Path.Combine(extraInner, "deeper"));

        // ---- the graph: shares asked for later, all at once ---------------------------
        var saved = new ViewAllWorkspaceState
        {
            ExtraRoots = [extra, offline],
            Nodes =
            [
                new ViewAllNodeState(extra, 0, 0, false, true),
                new ViewAllNodeState(extraInner, 0, 0, false, true),
                new ViewAllNodeState(Path.Combine(offline, "photos"), 40, 50, true, true)
            ]
        };

        using (var graph = new ViewAllGraphService())
        {
            await graph.InitializeAsync(saved, deferExtraRoots: true);
            Check("put off, the saved shares are not asked for while the graph is restored",
                !graph.TryGetNode(extra, out _) && graph.PendingExtraRoots.Count == 2);
            var before = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
            Check("saved meanwhile, they are written back, with the folders saved under them",
                before.ExtraRoots.Contains(extra, StringComparer.OrdinalIgnoreCase)
                && before.ExtraRoots.Contains(offline, StringComparer.OrdinalIgnoreCase)
                && before.Nodes.Any(node => ViewAllPath.Equals(node.Path, extraInner) && node.IsExpanded)
                && before.Nodes.Any(node => node.Path == Path.Combine(offline, "photos") && node.HasManualPosition));

            var added = new List<string>();
            var count = await graph.ProbeExtraRootsAsync(TimeSpan.FromSeconds(10), added: node => added.Add(node.FullPath));
            Check("asked for, the one that answers becomes a root, and says so",
                count == 1 && added.Count == 1 && ViewAllPath.Equals(added[0], extra) && graph.PendingExtraRoots.Count == 0);
            Check("with what was open in it opened again",
                graph.TryGetNode(extraInner, out var inner) && inner.IsExpanded && graph.TryGetNode(Path.Combine(extraInner, "deeper"), out _));
            var after = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
            Check("the one out of reach is kept, with one start missed, and the folders saved under it",
                after.ExtraRoots.Contains(offline, StringComparer.OrdinalIgnoreCase)
                && after.ExtraRootMisses.GetValueOrDefault(offline) == 1
                && !after.ExtraRootMisses.ContainsKey(extra)
                && after.Nodes.Any(node => node.Path == Path.Combine(offline, "photos")));
        }

        using (var tired = new ViewAllGraphService())
        {
            await tired.InitializeAsync(
                new ViewAllWorkspaceState
                {
                    ExtraRoots = [offline],
                    ExtraRootMisses = new Dictionary<string, int> { [offline] = ViewAllGraphService.MaximumRootMisses - 1 }
                });
            Check($"one out of reach at {ViewAllGraphService.MaximumRootMisses} starts in a row is taken for gone",
                !tired.CaptureState(new ViewAllViewportState(new Point(0, 0), 1)).ExtraRoots.Contains(offline, StringComparer.OrdinalIgnoreCase));
        }

        // ---- the view model: nothing written before the workspace is back -----------
        var statePath = Path.Combine(scratch, "tree.json");
        var workspace = new ViewAllWorkspaceState
        {
            ExtraRoots = [offline],
            ActivePath = Path.Combine(offline, "photos"),
            Nodes = [new ViewAllNodeState(Path.GetPathRoot(root)!, 0, 0, false, false), new ViewAllNodeState(Path.Combine(offline, "photos"), 10, 20, true, true)]
        };
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(workspace, options));
        var original = await File.ReadAllBytesAsync(statePath);

        using var icons = new ShellIconService();
        var marks = new FolderMarkService(Path.Combine(scratch, "marks.json"));
        using var tree = new ViewAllViewModel(marks, icons, statePath) { PreferLightReveal = true, IsCanvasShown = false };
        var restoring = tree.InitializeAsync();
        await tree.SaveAsync();
        Check("a save while the workspace is being read writes nothing over it",
            (await File.ReadAllBytesAsync(statePath)).AsSpan().SequenceEqual(original));
        await restoring;
        var written = await AddressWaitFor(() => !File.ReadAllBytes(statePath).AsSpan().SequenceEqual(original));
        var reloaded = await new ViewAllWorkspaceStore(statePath).LoadAsync();
        Check("the save it asked for is made once it is back, keeping the share not asked for yet, its folders and the folder selected in it",
            written && reloaded is not null
            && reloaded.ExtraRoots.Contains(offline, StringComparer.OrdinalIgnoreCase)
            && reloaded.Nodes.Any(node => node.Path == Path.Combine(offline, "photos") && node.HasManualPosition)
            && reloaded.ActivePath == Path.Combine(offline, "photos"));

        await tree.ProbeExtraRootsAsync();
        await tree.SaveAsync();
        reloaded = await new ViewAllWorkspaceStore(statePath).LoadAsync();
        Check("asked for and out of reach, it is still kept, with a start missed",
            reloaded is not null && reloaded.ExtraRoots.Contains(offline, StringComparer.OrdinalIgnoreCase)
            && reloaded.ExtraRootMisses.GetValueOrDefault(offline) == 1);
    }

    // ---- the folder list's first poll --------------------------------------------------

    private static async Task ReviewListPollChecksAsync(string root)
    {
        Section("review: the folder list's first poll");
        var folder = Path.Combine(root, "polled");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.txt"), "one");

        var files = new ViewAllFileSystemService();
        var options = new ViewAllGraphOptions();
        var reads = 0;
        using var icons = new ShellIconService();
        var list = new FolderListViewModel(
            (path, cancellation) =>
            {
                Interlocked.Increment(ref reads);
                return files.GetChildrenAsync(path, options, cancellation);
            },
            (_, _) => Task.CompletedTask,
            _ => false,
            icons)
        {
            IsVisible = true
        };
        await list.NavigateAsync(folder);
        var first = reads;

        using var hub = new ChangeHub(TimeProvider.System);
        var polled = hub.AddRootForTests(folder, WatchKind.Polling, arm: false);
        list.OnPollDue(polled);
        Check("the first poll after coming to a folder reads it once more - a change made since the list's read would otherwise never show",
            await AddressWaitFor(() => reads == first + 1));
        list.OnPollDue(polled);
        await Task.Delay(200);
        Check("and a poll that finds the folder's time as it was reads nothing", reads == first + 1);
    }

    // ---- frame containment -----------------------------------------------------------

    private static async Task ReviewFrameContainmentAsync()
    {
        Section("review: frames that keep failing");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\a");
        disk.AddFiles(@"Q:\a", 5, "file");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);

        var logged = new List<string>();
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };
        canvas.FrameFailureLogForTests = (where, _) => logged.Add(where);
        canvas.Measure(new Size(600, 400));
        canvas.Arrange(new Rect(0, 0, 600, 400));
        canvas.UpdateLayout();
        var time = await RunUntilStillAsync(canvas, TimeSpan.FromSeconds(100));
        var folder = tree.Find(@"Q:\a")!;

        canvas.FailFramesForTests = 1_000;
        canvas.FlyTo(folder, 0.5);
        for (var frame = 0; frame < 3 && canvas.IsFrameHooked; frame++)
        {
            canvas.RunContainedFrameForTests(time += Frame);
        }

        Check($"three failing frames in a row let the loop go, logged twice - the first, and the letting go ({logged.Count})",
            canvas.FramesFailingForTests && !canvas.IsFrameHooked && logged.Count == 2);

        canvas.FlyTo(tree.Root, 0.9);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check("something asking for a frame meanwhile does not run another that fails", !canvas.IsFrameHooked);

        canvas.RetryFramesForTests();
        Check("the back-off timer tries one more frame", canvas.IsFrameHooked);
        canvas.RunContainedFrameForTests(time += Frame);
        Check("which, failing again, lets go at once and is not logged again",
            canvas.FramesFailingForTests && !canvas.IsFrameHooked && logged.Count == 2);

        canvas.FailFramesForTests = 0;
        canvas.RetryFramesForTests();
        canvas.RunContainedFrameForTests(time += Frame);
        Check("the first frame that works puts the loop back", !canvas.FramesFailingForTests);

        // The flight asked for meanwhile lands, and the view settles.
        for (var frame = 0; frame < 400 && canvas.IsFrameHooked; frame++)
        {
            canvas.RunFrameForTests(time += Frame);
            if (frame % 20 == 19)
            {
                await Task.Delay(50);
            }
        }

        var still = !canvas.IsFrameHooked;
        canvas.FlyTo(folder, 0.5);
        Check("and asking for a frame hooks it again", still && canvas.IsFrameHooked);
    }

    // ---- overflows -------------------------------------------------------------------

    private static void ReviewOverflowChecks(string root)
    {
        Section("review: an overflow storm");
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "storm"));
        using var _ = hub;
        hub.Register(ChangeConsumer.Nested, Path.Combine(watch.Key, "shown"), new object());
        var epoch = watch.Epoch;

        hub.OnOverflow(watch, null);
        DrainHub(hub, sink);
        Check("an overflow moves the epoch on at once", watch.Epoch == epoch + 1 && sink.Bumps.Count == 1);

        time.Advance(100);
        hub.OnOverflow(watch, null);
        DrainHub(hub, sink);
        Check("a second one 100 ms later is put off, not dropped", watch.Epoch == epoch + 1 && sink.Bumps.Count == 1);
        time.Advance(400);
        DrainHub(hub, sink);
        Check("and moves it on when the 500 ms are up", watch.Epoch == epoch + 2 && sink.Bumps.Count == 2);
        Check($"overflows coming on inside the gap double it ({hub.OverflowGapMillisecondsOf(watch):F0} ms)",
            Math.Abs(hub.OverflowGapMillisecondsOf(watch) - 1_000) < 1);

        // A share answering every read with one: the epoch moves ever less often.
        var gaps = new List<double>();
        for (var round = 0; round < 6; round++)
        {
            var epochBefore = watch.Epoch;
            var start = time.Milliseconds;
            while (watch.Epoch == epochBefore && time.Milliseconds - start < 10_000)
            {
                hub.OnOverflow(watch, null);
                time.Advance(50);
                DrainHub(hub, sink);
            }

            gaps.Add(time.Milliseconds - start);
        }

        Check($"a storm of them moves the epoch every 1, 2, 4 s, then every {ChangeHub.PollMilliseconds / 1000:F0} s at most ({string.Join(", ", gaps.Select(gap => $"{gap:F0}"))} ms)",
            gaps.Count == 6 && Math.Abs(gaps[0] - 1_000) <= 50 && Math.Abs(gaps[1] - 2_000) <= 50 && Math.Abs(gaps[2] - 4_000) <= 50
            && gaps.Skip(3).All(gap => Math.Abs(gap - ChangeHub.PollMilliseconds) <= 50));

        time.Advance(ChangeHub.PollMilliseconds + 1_000);
        DrainHub(hub, sink);
        var quiet = watch.Epoch;
        hub.OnOverflow(watch, null);
        DrainHub(hub, sink);
        Check("once it is over, the next overflow moves the epoch at once and the gap is 500 ms again",
            watch.Epoch == quiet + 1 && Math.Abs(hub.OverflowGapMillisecondsOf(watch) - ChangeHub.OverflowGapMilliseconds) < 1);

        Check("the watch asks again 10 ms after one overflow, twice as late after each more in a row, a second at most",
            DirectoryChangeWatcher.OverflowWaitMilliseconds(1) == DirectoryChangeWatcher.GatherMilliseconds
            && DirectoryChangeWatcher.OverflowWaitMilliseconds(2) == 2 * DirectoryChangeWatcher.GatherMilliseconds
            && DirectoryChangeWatcher.OverflowWaitMilliseconds(50) == DirectoryChangeWatcher.LongestOverflowWaitMilliseconds);
    }

    // ---- a watch that ended before its arm took it in ----------------------------------

    private static void ReviewEndedWatchChecks(string root)
    {
        Section("review: a watch ended before it was taken in");
        Check("ended with no error, it counts as a failure that is retried, never as a refusal",
            ChangeHub.EndedError(0) == WatchNative.ErrorNotReady && !WatchNative.IsRefusal(ChangeHub.EndedError(0))
            && ChangeHub.EndedError(WatchNative.ErrorNetworkNameDeleted) == WatchNative.ErrorNetworkNameDeleted);

        var folder = Path.Combine(root, "ended");
        Directory.CreateDirectory(folder);
        using var hub = new ChangeHub(TimeProvider.System) { OpenedForTests = watcher => watcher.Stop() };
        var watch = hub.AddRootForTests(folder);
        Check($"a watch stopped before its arm takes it in leaves the root off, to be tried again - not polled for good ({watch.Kind}, {watch.State})",
            WaitFor(() => Volatile.Read(ref watch.ArmAttempts) > 0)
            && watch.State == WatchState.Off && Volatile.Read(ref watch.RetryAt) != 0 && watch.Kind == WatchKind.Local
            && watch.LastError == WatchNative.ErrorNotReady);
    }

    // ---- a path's registrations moving roots -------------------------------------------

    private static void ReviewRootMoveChecks()
    {
        Section("review: registrations moving roots");
        var volumes = new VolumeResolver(letter => letter == 'E'
            ? new VolumeResolver.Letter(WatchNative.DriveRemovable, null, null)
            : new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null));
        using var hub = new ChangeHub(new ManualTime(), volumes) { ArmFailureForTests = _ => WatchNative.ErrorNotReady };
        var list = new object();
        var nested = new object();
        hub.Register(ChangeConsumer.List, @"E:\photos", list);
        hub.Register(ChangeConsumer.Nested, @"E:\photos", nested);
        var first = hub.RootFor(@"E:\")!;
        var counted = (Volatile.Read(ref first.NestedInterest), Volatile.Read(ref first.OtherInterest));
        hub.Drop(first);
        var second = hub.RootFor(@"E:\")!;
        hub.Register(ChangeConsumer.List, @"E:\photos", list);
        Check($"a path moving to the volume put back takes its registrations' counts along (before {counted}, after {second.NestedInterest}/{second.OtherInterest}, left {first.NestedInterest}/{first.OtherInterest})",
            counted == (1, 1) && !ReferenceEquals(first, second)
            && Volatile.Read(ref second.NestedInterest) == 1 && Volatile.Read(ref second.OtherInterest) == 1
            && Volatile.Read(ref first.NestedInterest) == 0 && Volatile.Read(ref first.OtherInterest) == 0);
        hub.Unregister(ChangeConsumer.List, @"E:\photos", list);
        hub.Unregister(ChangeConsumer.Nested, @"E:\photos", nested);
        Check("and letting them go takes them off the new root, and nothing off anyone else",
            Volatile.Read(ref second.NestedInterest) == 0 && Volatile.Read(ref second.OtherInterest) == 0);
    }
}
