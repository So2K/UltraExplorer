using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The nested tree and the watches keeping up with the disk where they did
/// not: a folder's name taken again by another folder, a folder reached by
/// name whose unlisted parent is renamed away, a folder hidden or shown by its
/// attributes, a volume locked by Format or chkdsk, a drive put back at its
/// letter, and the bookkeeping that went with them.  Real folders under the
/// temp folder and a watch of their own wherever the disk is what matters; a
/// tree drawn by hand - a frame, then the folders a canvas would draw - stands
/// for the canvas.
/// </summary>
internal static partial class Program
{
    private static Task TreeLiveWatchChecks()
    {
        Section("tree live watch");
        RunOnSta("a folder's name taken again", LiveNameReuseChecks);
        RunOnSta("an unlisted parent renamed away", LivePartialAncestorChecks);
        RunOnSta("a folder hidden by its attributes", LiveHiddenAttributeChecks);
        RunOnSta("renames below a dropped folder", LiveDroppedRenamesChecks);
        RunOnSta("one rename heard by both panes", LiveSplitOrderChecks);
        WatchShortNameCacheChecks();
        WatchReplugChecks();
        RunOnSta("a volume locked", LiveVolumeLockChecks);
        RunOnSta("a failed watch asked to go", LiveFailedThenRemovedChecks);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A folder renamed or moved away and its name taken by another: the tree
    /// keeps the folder by its name, so what was read of the one that went
    /// must be read again - at once when its name is taken in the same
    /// moment, when the parent's listing next comes when it is taken later,
    /// and one level down as well when the newcomer has sub-folders of the
    /// same names.
    /// </summary>
    private static async Task LiveNameReuseChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerNameReuse", Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(baseDirectory, "P");
        var reusedPath = Path.Combine(parentPath, "R");
        var currentPath = Path.Combine(parentPath, "cur");
        var stagingPath = Path.Combine(parentPath, "staging");
        var laterPath = Path.Combine(parentPath, "L");
        Directory.CreateDirectory(Path.Combine(reusedPath, "inside"));
        File.WriteAllText(Path.Combine(reusedPath, "old.txt"), "old");
        Directory.CreateDirectory(Path.Combine(currentPath, "bin"));
        File.WriteAllText(Path.Combine(currentPath, "bin", "v1.dll"), "1");
        Directory.CreateDirectory(Path.Combine(stagingPath, "bin"));
        File.WriteAllText(Path.Combine(stagingPath, "bin", "v2.dll"), "2");
        Directory.CreateDirectory(laterPath);
        File.WriteAllText(Path.Combine(laterPath, "before.txt"), "before");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var watch = hub.AddRootForTests(baseDirectory);
            using var tree = new NestedTree();
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(baseDirectory, "W", NestedFolderKind.Drive)]);
            var parent = await tree.RevealAsync(parentPath);
            if (parent is null || !parentPath.Equals(parent.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                Check("the name-reuse fixture is in the tree", false);
                return;
            }

            await tree.LoadAsync(parent);
            foreach (var path in new[] { reusedPath, Path.Combine(reusedPath, "inside"), currentPath, Path.Combine(currentPath, "bin"), stagingPath, laterPath })
            {
                if (tree.Find(path) is { } folder)
                {
                    await tree.LoadAsync(folder);
                }
            }

            var armed = await LiveWait(() => watch.State == WatchState.Armed, 3_000) >= 0;
            NestedFolder? Child(NestedFolder folder, string name) => NestedTree.FindChild(folder, name);

            // What a canvas does each picture: a new frame, and the folders it
            // draws asked for, which reads one that is out of date.
            async Task<bool> DrawUntil(Func<bool> condition, int timeoutMilliseconds, params string[] drawn)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    tree.BeginFrame();
                    foreach (var path in drawn)
                    {
                        if (tree.Find(path) is { } folder)
                        {
                            tree.Request(folder, 400);
                        }
                    }

                    if (condition())
                    {
                        return true;
                    }

                    await Task.Delay(8);
                }

                return condition();
            }

            await DrawUntil(() => false, 100, parentPath, reusedPath, currentPath, Path.Combine(currentPath, "bin"));
            Check("the fixture is read and watched", armed && Child(parent, "R") is { IsLoaded: true } && Child(parent, "cur") is { IsLoaded: true }
                && tree.Find(Path.Combine(currentPath, "bin")) is { IsLoaded: true } && Child(parent, "L") is { IsLoaded: true });

            // ren R R.old && mkdir R: the new R is another folder, and an
            // empty one - nothing written in it to say so by its own path.
            Directory.Move(reusedPath, reusedPath + ".old");
            Directory.CreateDirectory(reusedPath);
            var renewed = await DrawUntil(
                () => Child(parent, "R") is { IsLoaded: true } folder && folder.AllFiles.Length == 0 && folder.AllChildren.Length == 0,
                3_000,
                parentPath,
                reusedPath);
            var shown = Child(parent, "R");
            Check($"a folder renamed away and made anew under its name shows the new, empty folder ({shown?.AllFiles.Length} files, {shown?.AllChildren.Length} sub-folders)",
                renewed);

            // An updater's swap: cur to old, staging to cur.  cur\bin is the
            // name of a sub-folder in both.
            var binPath = Path.Combine(currentPath, "bin");
            Directory.Move(currentPath, Path.Combine(parentPath, "old"));
            Directory.Move(stagingPath, currentPath);
            var swapped = await DrawUntil(
                () => tree.Find(binPath) is { IsLoaded: true } bin && bin.AllFiles.Any(file => file.Name == "v2.dll") && bin.AllFiles.All(file => file.Name != "v1.dll"),
                3_000,
                parentPath,
                currentPath,
                binPath);
            Check($"two folders swapped by renaming show the new one down to its sub-folders ({string.Join(", ", tree.Find(binPath)?.AllFiles.Select(file => file.Name) ?? [])})",
                swapped);

            // The name taken again only later, with nothing drawing the
            // parent in between: its listing, read when it is drawn again,
            // still has the name.
            var taken = tree.LiveChangesTaken;
            await DrawUntil(() => false, 60);
            Directory.Move(laterPath, laterPath + ".old");
            await LiveWait(() => tree.LiveChangesTaken >= taken + 2, 3_000);
            await Task.Delay(200);
            taken = tree.LiveChangesTaken;
            Directory.CreateDirectory(laterPath);
            await LiveWait(() => tree.LiveChangesTaken > taken, 3_000);
            await Task.Delay(100);
            var later = await DrawUntil(
                () => Child(parent, "L") is { IsLoaded: true } folder && folder.AllFiles.Length == 0,
                3_000,
                parentPath,
                laterPath);
            Check($"a folder whose name is taken again while nothing draws it shows the new folder once drawn ({string.Join(", ", Child(parent, "L")?.AllFiles.Select(file => file.Name) ?? [])})",
                later);

            // The parent read again after the change, before the folder's own
            // path is heard to have gone: nothing more is on its way to it.
            using var quiet = new NestedTree();
            quiet.SetRoots([new NestedRoot(baseDirectory, "W", NestedFolderKind.Drive)]);
            var quietParent = await quiet.RevealAsync(parentPath);
            var aheadPath = Path.Combine(parentPath, "ahead");
            Directory.CreateDirectory(aheadPath);
            File.WriteAllText(Path.Combine(aheadPath, "first.txt"), "first");
            if (quietParent is not null)
            {
                await quiet.LoadAsync(quietParent);
                if (NestedTree.FindChild(quietParent, "ahead") is { } ahead)
                {
                    await quiet.LoadAsync(ahead);
                    Directory.Move(aheadPath, aheadPath + ".old");
                    Directory.CreateDirectory(aheadPath);
                    await quiet.RefreshAsync(quietParent);
                    quiet.OnFolderChanged(ahead, new FolderChange(ahead.FullPath, ChangeKinds.Gone, Stopwatch.GetTimestamp(), default, default));
                    await LiveWait(() => ahead.IsLoaded && ahead.AllFiles.Length == 0, 3_000);
                }
            }

            Check("a folder's path heard gone after its parent's listing already has the name again is read as the new folder",
                quietParent is not null && NestedTree.FindChild(quietParent, "ahead") is { IsLoaded: true } kept && kept.AllFiles.Length == 0);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// A folder opened by name hangs below folders that were never listed -
    /// Users, the profile, Projects for C:\Users\me\Projects\Foo.  Renaming
    /// or recycling one of those takes the folder away, and the watch says so
    /// only by that folder's own path.
    /// </summary>
    private static async Task LivePartialAncestorChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerPartialGone", Guid.NewGuid().ToString("N"));
        var projects = Path.Combine(baseDirectory, "Users", "me", "Projects");
        var target = Path.Combine(projects, "Foo");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(baseDirectory, "Users", "me", "Other"));
        File.WriteAllText(Path.Combine(target, "file.txt"), "file");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var watch = hub.AddRootForTests(baseDirectory);
            using var tree = new NestedTree();
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(baseDirectory, "C:", NestedFolderKind.Drive)]);
            var folder = await tree.MaterializePathAsync(target);
            if (folder is null)
            {
                Check("the folder named is in the tree", false);
                return;
            }

            await tree.LoadAsync(folder);
            tree.CameraAnchor = folder;
            var projectsFolder = folder.Parent;
            var armed = await LiveWait(() => watch.State == WatchState.Armed, 3_000) >= 0;
            Check("the folder named is read, below parents never listed", armed && folder.IsLoaded
                && projectsFolder is { HasPartialListing: true, IsLoaded: false } && projectsFolder.Parent is { HasPartialListing: true, IsLoaded: false });

            Directory.Move(projects, projects + "2");
            var gone = await LiveWait(() => NestedTree.IsDetached(folder), 3_000) >= 0;
            Check("an unlisted parent renamed away takes the folder named below it off the canvas",
                gone && tree.Find(target) is null && projectsFolder is not null && NestedTree.IsDetached(projectsFolder));
            Check("and leaves the parent above it, still unlisted", tree.Find(Path.Combine(baseDirectory, "Users", "me")) is { IsLoaded: false });
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// attrib +h, or the Hidden box in Properties: the watch hears only the
    /// folder's own entry change, as if something had been written in it.
    /// </summary>
    private static async Task LiveHiddenAttributeChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerHiddenLive", Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(baseDirectory, "P");
        var visiblePath = Path.Combine(parentPath, "Visible");
        Directory.CreateDirectory(visiblePath);
        File.WriteAllText(Path.Combine(visiblePath, "a.txt"), "a");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var watch = hub.AddRootForTests(baseDirectory);
            using var tree = new NestedTree();
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(baseDirectory, "H", NestedFolderKind.Drive)]);
            var parent = await tree.RevealAsync(parentPath);
            if (parent is null)
            {
                Check("the hidden-attribute fixture is in the tree", false);
                return;
            }

            await tree.LoadAsync(parent);
            var armed = await LiveWait(() => watch.State == WatchState.Armed, 3_000) >= 0;
            Check("the folder is read with its sub-folder shown", armed && NestedTree.FindChild(parent, "Visible") is { IsHidden: false });

            async Task<bool> DrawUntil(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 3_000)
                {
                    tree.BeginFrame();
                    tree.Request(parent, 600);
                    if (condition())
                    {
                        return true;
                    }

                    await Task.Delay(8);
                }

                return condition();
            }

            await DrawUntil(() => false);
            File.SetAttributes(visiblePath, FileAttributes.Directory | FileAttributes.Hidden);
            var hidden = await DrawUntil(() => NestedTree.FindChild(parent, "Visible") is { IsHidden: true });
            Check("a sub-folder hidden by its attribute is hidden on the canvas", hidden);

            File.SetAttributes(visiblePath, FileAttributes.Directory);
            var shown = await DrawUntil(() => NestedTree.FindChild(parent, "Visible") is { IsHidden: false });
            Check("and shown again when the attribute is taken off", shown);

            var reads = 0;
            tree.FolderLoaded += folder => reads += ReferenceEquals(folder, parent) ? 1 : 0;
            File.WriteAllText(Path.Combine(visiblePath, "b.txt"), "b");
            await DrawUntil(() => false);
            Check($"a file written in the sub-folder still costs its parent no read ({reads})", reads == 0);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// A rename heard in a folder off screen waits for the folder's next
    /// listing; a folder above it removed meanwhile cuts it off, and the
    /// rename waiting must go with it rather than keep the whole cut-off
    /// branch alive.
    /// </summary>
    private static async Task LiveDroppedRenamesChecks()
    {
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\top\A\F", "x.txt", 1);
        disk.Folder(@"Q:\top\A\F\sub");
        disk.Folder(@"Q:\top\B");
        using var hub = new ChangeHub(new ManualTime());
        hub.AddRootForTests(@"Q:\top", arm: false);
        using var tree = new NestedTree(disk.Read);
        tree.Changes = hub;
        tree.SetRoots([new NestedRoot(@"Q:\top", "Q", NestedFolderKind.Drive)]);
        var top = tree.Root.Children.Single();
        await tree.LoadAsync(top);
        var a = NestedTree.FindChild(top, "A")!;
        await tree.LoadAsync(a);
        var f = NestedTree.FindChild(a, "F")!;
        await tree.LoadAsync(f);
        var renames = (ICollection)typeof(NestedTree).GetField("_renames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tree)!;

        for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++)
        {
            tree.BeginFrame();
        }

        tree.OnFolderChanged(f, new FolderChange(f.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair("x.txt", "y.txt") }, default));
        Check("a rename in a folder off screen waits for its next listing", renames.Count == 1 && f.NeedsRefresh);

        disk.Remove(@"Q:\top\A");
        await tree.RefreshAsync(top);
        await Task.Delay(100);
        Check($"a folder above it removed, the rename waiting goes with it ({renames.Count} left)", NestedTree.IsDetached(f) && renames.Count == 0);
    }

    /// <summary>
    /// The panes of a split view share their folders' orders, and each pane's
    /// tree hears every change: a rotation of names must move the orders once.
    /// </summary>
    private static async Task LiveSplitOrderChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\ord\A");
        disk.Folder(@"Q:\ord\B");
        var orders = new FolderOrders();
        using var left = new NestedTree(disk.Read) { Orders = orders };
        using var right = new NestedTree(disk.Read) { Orders = orders };
        left.SetRoots([new NestedRoot(@"Q:\ord", "Q", NestedFolderKind.Drive)]);
        right.SetRoots([new NestedRoot(@"Q:\ord", "Q", NestedFolderKind.Drive)]);
        var leftFolder = left.Root.Children.Single();
        var rightFolder = right.Root.Children.Single();
        await left.LoadAsync(leftFolder);
        await right.LoadAsync(rightFolder);

        var byDate = new ItemSort(SortColumn.Modified, true);
        var bySize = new ItemSort(SortColumn.Size, true);
        orders.SetFolder(@"Q:\ord\A", byDate);
        orders.SetFolder(@"Q:\ord\B", bySize);

        // B to C, then A to B: one change, as the hub hands it to each pane.
        RenamePair[] rotation = [new("B", "C"), new("A", "B")];
        var change = new FolderChange(@"Q:\ord", ChangeKinds.Structural, Stopwatch.GetTimestamp(), rotation, default);
        ((IChangeSink)left).FolderChanged(ChangeConsumer.Nested, leftFolder, change);
        ((IChangeSink)right).FolderChanged(ChangeConsumer.Nested, rightFolder, change);
        Check($"a rotation of names heard by both panes moves each order once (C {orders.SortOf(@"Q:\ord\C").Column}, B {orders.SortOf(@"Q:\ord\B").Column})",
            orders.SortOf(@"Q:\ord\C") == bySize && orders.SortOf(@"Q:\ord\B") == byDate && !orders.HasOwnOrder(@"Q:\ord\A"));

        // The next change is another one, and moves the orders again.
        RenamePair[] back = [new("C", "D")];
        var next = new FolderChange(@"Q:\ord", ChangeKinds.Structural, Stopwatch.GetTimestamp(), back, default);
        ((IChangeSink)right).FolderChanged(ChangeConsumer.Nested, rightFolder, next);
        ((IChangeSink)left).FolderChanged(ChangeConsumer.Nested, leftFolder, next);
        Check("and the next change moves them again", orders.SortOf(@"Q:\ord\D") == bySize && !orders.HasOwnOrder(@"Q:\ord\C"));
    }

    /// <summary>
    /// Records spelt with a folder's short 8.3 name are looked up under its
    /// long name, which asks the file system - a round trip on a share, under
    /// the watch's lock.  A buffer of records in one such folder asks once.
    /// </summary>
    private static void WatchShortNameCacheChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerShortCache", Guid.NewGuid().ToString("N"));
        var longFolder = Path.Combine(root, "Long Folder Name For Cache");
        Directory.CreateDirectory(longFolder);
        try
        {
            var shortPart = Path.GetFileName(ShortPathOf(longFolder));
            if (shortPart.Equals("Long Folder Name For Cache", StringComparison.OrdinalIgnoreCase) || !shortPart.Contains('~'))
            {
                Check("(8.3 names are not made on this volume; the short-name lookups are not exercised)", true);
                return;
            }

            using var hub = new ChangeHub(new ManualTime());
            var watch = hub.AddRootForTests(root, arm: false);
            var records = Enumerable.Range(0, 200)
                .Select(index => (WatchNative.ActionAdded, $@"{shortPart}\file{index:D3}.txt", 0L, 0u))
                .ToArray();
            hub.FeedForTests(watch, NotifyRecords(false, records), details: false);
            Check($"200 records in a short-named folder nobody registered ask for its long name once ({watch.Records!.LongNameLookups})",
                watch.Records.LongNameLookups == 1 && watch.Records.LongNameHits == 0 && hub.PendingCount == 0);

            var target = new object();
            hub.Register(ChangeConsumer.List, longFolder, target);
            hub.FeedForTests(watch, NotifyRecords(false, records), details: false);
            Check($"registered, they are all found under the long name, asking once more for the new buffer ({watch.Records.LongNameLookups}, {watch.Records.LongNameHits} found)",
                watch.Records.LongNameLookups == 2 && watch.Records.LongNameHits == 200 && hub.PendingCount == 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A drive taken out and put back at its letter is a new root: the folder
    /// list's folder on it, registered before, must be heard through the new
    /// root's watch without the list registering again.
    /// </summary>
    private static void WatchReplugChecks()
    {
        var volumes = new VolumeResolver(letter => letter == 'Q'
            ? new VolumeResolver.Letter(WatchNative.DriveRemovable, null, null)
            : new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null));
        var time = new ManualTime();
        using var hub = new ChangeHub(time, volumes);
        hub.ArmFailureForTests = _ => WatchNative.ErrorNotReady;
        var sink = new WatchSink();
        var first = hub.RootFor(@"Q:\");
        var listed = new object();
        hub.Register(ChangeConsumer.List, @"Q:\Photos", listed);
        Check("the list's folder on a removable drive counts on its root", first is { OtherInterest: 1 });
        if (first is null)
        {
            return;
        }

        hub.Drop(first);
        hub.InvalidateVolumes();
        var second = hub.RootFor(@"Q:\");
        Check("put back, the drive is a new root", second is { IsDropped: false } && !ReferenceEquals(second, first));
        if (second is null)
        {
            return;
        }

        hub.FeedForTests(second, NotifyRecords(false, (WatchNative.ActionAdded, @"Photos\new.jpg", 0, 0)), details: false);
        time.Advance(600);
        DrainHub(hub, sink);
        Check($"a change in the list's folder is heard through the new root's watch ({second.OtherInterest} counted on it)",
            sink.For(listed).Any() && second.OtherInterest == 1 && first.OtherInterest == 0);

        hub.Unregister(ChangeConsumer.List, @"Q:\Photos", listed);
        Check("and the list letting it go takes it off the new root", second.OtherInterest == 0 && second.InterestCount == 0);
    }

    /// <summary>
    /// Format, chkdsk /f, Eject on a card, BitLocker: each locks the volume,
    /// and fails with "in use" while anything holds it.  Windows says so on
    /// the watch's own handle first, as a custom event.
    /// </summary>
    private static async Task LiveVolumeLockChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerVolumeLock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);
        HwndSource? window = null;
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            using var notifications = new VolumeNotifications(hub, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            window = new HwndSource(new HwndSourceParameters("UltraExplorer volume lock check") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            notifications.SetWindow(window.Handle);
            var root = hub.RootFor(baseDirectory);
            if (root is null)
            {
                Check("a local volume gets a watch", false);
                return;
            }

            await LiveWait(() => root.Handle is { IsInvalid: false } && notifications.RegisteredCount == 1, 3_000);
            var notification = notifications.NotificationFor(root);
            var firstHandle = root.Handle;
            var registrations = notifications.Registrations;
            Check("armed, its handle is registered for the volume's events", notification != IntPtr.Zero && firstHandle is not null);

            using (var locking = new VolumeEventMessage(notification, VolumeEvents.Lock))
            {
                var outcome = notifications.OnDeviceChange(0x8006, locking.Pointer);
                Check("told the volume is being locked, the watch is suspended", outcome == DeviceChange.WatchSuspended);
                Check("and its handle is closed before the lock is tried", root.Handle is null && root.State == WatchState.Suspended && firstHandle!.IsClosed);
            }

            using (var unlocked = new VolumeEventMessage(notification, VolumeEvents.Unlock))
            {
                var outcome = notifications.OnDeviceChange(0x8006, unlocked.Pointer);
                Check("unlocked, the watch is armed again", outcome == DeviceChange.WatchRearmed);
            }

            await LiveWait(() => root.State == WatchState.Armed && notifications.RegisteredCount == 1 && notifications.Registrations > registrations, 10_000);
            Check("on a new handle, registered afresh", root.State == WatchState.Armed && notifications.RegisteredCount == 1
                && ReferenceEquals(notifications.RegisteredHandleFor(root), root.Handle) && !ReferenceEquals(root.Handle, firstHandle));

            // A lock that fails, and a volume mounted, while the watch is up
            // change nothing; a lock that fails after it was suspended arms it.
            var current = notifications.NotificationFor(root);
            var armedHandle = root.Handle;
            using (var mounted = new VolumeEventMessage(current, VolumeEvents.Mount))
            {
                var outcome = notifications.OnDeviceChange(0x8006, mounted.Pointer);
                Check("a mount heard while the watch is up leaves it, and its registration, as they are",
                    outcome == DeviceChange.None && ReferenceEquals(root.Handle, armedHandle) && notifications.NotificationFor(root) == current);
            }

            using (var locking = new VolumeEventMessage(current, VolumeEvents.Lock))
            using (var failed = new VolumeEventMessage(current, VolumeEvents.LockFailed))
            {
                notifications.OnDeviceChange(0x8006, locking.Pointer);
                var outcome = notifications.OnDeviceChange(0x8006, failed.Pointer);
                var rearmed = await LiveWait(() => root.State == WatchState.Armed && root.Handle is { IsClosed: false } && !ReferenceEquals(root.Handle, armedHandle), 10_000) >= 0;
                Check("a lock that fails has the watch armed again", outcome == DeviceChange.WatchRearmed && rearmed);
            }

            // Dismounted without a lock first, then mounted again.
            await LiveWait(() => notifications.RegisteredCount == 1 && ReferenceEquals(notifications.RegisteredHandleFor(root), root.Handle), 10_000);
            var latest = notifications.NotificationFor(root);
            var lastHandle = root.Handle;
            using (var dismounted = new VolumeEventMessage(latest, VolumeEvents.Dismount))
            using (var mountedAgain = new VolumeEventMessage(latest, VolumeEvents.Mount))
            {
                var outcome = notifications.OnDeviceChange(0x8006, dismounted.Pointer);
                var letGo = root.State == WatchState.Suspended && root.Handle is null;
                var again = notifications.OnDeviceChange(0x8006, mountedAgain.Pointer);
                var rearmed = await LiveWait(() => root.State == WatchState.Armed && root.Handle is { IsClosed: false } && !ReferenceEquals(root.Handle, lastHandle), 10_000) >= 0;
                Check("dismounted, the watch lets the volume go, and mounted again it is armed again",
                    outcome == DeviceChange.WatchSuspended && letGo && again == DeviceChange.WatchRearmed && rearmed);
            }
        }
        finally
        {
            window?.Dispose();
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// A watch that failed lets its device registration go a moment later, on
    /// the window's thread.  If Windows asks whether the drive may go before
    /// that moment, the watch is suspended and the registration is what brings
    /// the answer: it must stay, or the watch is suspended for good.
    /// </summary>
    private static async Task LiveFailedThenRemovedChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerFailedRemove", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);
        HwndSource? window = null;
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            using var notifications = new VolumeNotifications(hub, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            window = new HwndSource(new HwndSourceParameters("UltraExplorer failed removal check") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            notifications.SetWindow(window.Handle);
            var root = hub.RootFor(baseDirectory);
            if (root is null)
            {
                Check("a local volume gets a watch", false);
                return;
            }

            await LiveWait(() => root.Watcher is not null && notifications.RegisteredCount == 1, 3_000);
            var notification = notifications.NotificationFor(root);
            var watcher = root.Watcher;
            if (watcher is null || notification == IntPtr.Zero)
            {
                Check("the volume's watch is armed and registered", false);
                return;
            }

            // The watch fails on its own thread: its handle is closed, and the
            // registration is let go on this one, later.
            watcher.Stop();
            Task.Run(() => hub.OnWatchFailed(root, watcher, WatchNative.ErrorNotReady)).Wait();
            using var message = new HandleMessage(notification);
            var outcome = notifications.OnDeviceChange(0x8001, message.Pointer);
            await Task.Delay(100);
            Check($"asked whether the drive may go before the failure's registration was let go, it keeps the registration ({outcome}, {notifications.RegisteredCount} registered)",
                outcome == DeviceChange.WatchSuspended && notifications.RegisteredCount == 1 && notifications.NotificationFor(root) == notification);

            outcome = notifications.OnDeviceChange(0x8002, message.Pointer);
            var armed = await LiveWait(() => root.State == WatchState.Armed && notifications.RegisteredCount == 1, 10_000) >= 0;
            Check($"and the removal called off arms the watch again ({outcome}, {root.State})", outcome == DeviceChange.WatchRearmed && armed);
        }
        finally
        {
            window?.Dispose();
            TryDelete(baseDirectory);
        }
    }

    /// <summary>The volume events of ioevent.h a watch acts on.</summary>
    private static class VolumeEvents
    {
        public static readonly Guid Lock = new("50708874-c9af-11d1-8fef-00a0c9a06d32");
        public static readonly Guid LockFailed = new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32");
        public static readonly Guid Unlock = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32");
        public static readonly Guid Dismount = new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32");
        public static readonly Guid Mount = new("b5804878-1a96-11d2-8ffd-00a0c9a06d32");
    }

    /// <summary>A DEV_BROADCAST_HANDLE carrying a custom event, as Windows sends with DBT_CUSTOMEVENT.</summary>
    private sealed class VolumeEventMessage : IDisposable
    {
        public VolumeEventMessage(IntPtr notification, Guid kind)
        {
            var message = new VolumeNotifications.BroadcastHandle
            {
                Size = Marshal.SizeOf<VolumeNotifications.BroadcastHandle>(),
                DeviceType = 6,
                Notification = notification,
                EventGuid = kind,
                NameOffset = -1
            };
            Pointer = Marshal.AllocHGlobal(message.Size);
            Marshal.StructureToPtr(message, Pointer, fDeleteOld: false);
        }

        public IntPtr Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
