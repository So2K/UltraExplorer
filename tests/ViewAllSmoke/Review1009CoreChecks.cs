using System.Diagnostics;
using System.IO;
using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The core fixes of the review of 2026-10-09 (docs/REVIEW_2026-10-09.md):
/// a first read thrown away left its folder on the change hub for good (M9);
/// a root made while nothing watched kept no watch once a hub came (M10); a
/// stale arm shared the root's records with the live watch (L13a); a subst
/// letter's spelling stayed on the root it no longer stood for (L13b); a
/// canvas sharing the icon texture kept drawing a slot another canvas gave to
/// another icon (M12); a program whose own icon is its type's was waited for
/// for ever, and a type with no icon too (L12a); and a selection handed to
/// the canvas by SetSelection was taken for an echo of the window's (L12f).
/// </summary>
internal static partial class Program
{
    private static async Task Review1009CoreChecks()
    {
        Section("review 2026-10-09: tree, watch and icon atlas");
        await Review1009FirstReadChecks();
        await Review1009RootWatchChecks();
        await Review1009ArmRecordsChecks();
        Review1009SubstChecks();
        Review1009SlotReuseChecks();
        Review1009IconWaitChecks();
        RunOnSta("review 2026-10-09 selection handed by SetSelection", Review1009SetSelectionChecks);
    }

    /// <summary>A hub whose letters are all unused, so nothing it makes is armed on a real volume.</summary>
    private static ChangeHub Review1009Hub() =>
        new(new ManualTime(), new VolumeResolver(_ => new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null)));

    /// <summary>
    /// M9: the worker registers a folder read for the first time before its
    /// listing is taken; when the result is thrown away, or the hub is taken
    /// away while the read is under way, that registration goes too.
    /// </summary>
    private static async Task Review1009FirstReadChecks()
    {
        using var hub = Review1009Hub();
        using var disk = new GatedDisk();
        var (tree, folders) = await QueueTreeAsync(disk, "kept", "unhooked", "dropped");
        var (kept, unhooked, dropped) = (folders[0], folders[1], folders[2]);
        tree.Changes = hub;
        bool Registered(NestedFolder folder) => hub.Registry.TryGet(WatchAlias.KeyOf(folder.FullPath), out var interest) && interest.Nested is not null;

        // Read and applied: the worker's registration is the tree's own.
        disk.Gating = true;
        tree.Request(kept, 1);
        await WaitUntil(() => disk.ReadsOf(kept.FullPath) > 0, 3_000);
        var heardWhileReading = Registered(kept) && tree.FirstReadRegistrationsInFlight == 1;
        disk.Gate.Release();
        await WaitUntil(() => tree.HasWork, 3_000);
        var budget = FrameBudget.Unlimited;
        tree.DrainResults(ref budget);
        Check($"a first read is heard from before its listing is taken, and stays registered once applied ({tree.FirstReadRegistrationsInFlight} in flight)",
            heardWhileReading && kept.IsLoaded && Registered(kept) && tree.FirstReadRegistrationsInFlight == 0);

        // The hub taken away while the read is under way - a pane closed, a
        // picker hidden: the folder goes off it at once, though the result
        // may never be applied.
        tree.Request(unhooked, 1);
        await WaitUntil(() => disk.ReadsOf(unhooked.FullPath) > 0, 3_000);
        var registeredMidRead = Registered(unhooked);
        tree.Changes = null;
        Check("a hub taken away mid-read takes the folder the worker registered off with the rest",
            registeredMidRead && !Registered(unhooked) && !Registered(kept) && hub.Registry.Count == 0 && tree.FirstReadRegistrationsInFlight == 0);
        disk.Gate.Release();
        await WaitUntil(() => tree.HasWork, 3_000);
        budget = FrameBudget.Unlimited;
        tree.DrainResults(ref budget);
        Check("and applying the read later registers nothing with the hub it no longer has", hub.Registry.Count == 0);

        // A read whose result is thrown away: the tree going while it reads.
        tree.Changes = hub;
        var before = hub.Registry.Count;
        tree.Request(dropped, 1);
        await WaitUntil(() => disk.ReadsOf(dropped.FullPath) > 0, 3_000);
        var registeredBeforeDispose = Registered(dropped);
        tree.Dispose();
        await WaitUntil(() => tree.HasWork, 3_000);
        budget = FrameBudget.Unlimited;
        tree.DrainResults(ref budget);
        Check($"a first read thrown away takes its folder off the hub again ({hub.Registry.Count} paths, {before} before it)",
            registeredBeforeDispose && !Registered(dropped) && hub.Registry.Count == before && tree.FirstReadRegistrationsInFlight == 0);
        disk.Gating = false;
        disk.Gate.Release(4);
    }

    /// <summary>M10: a root made while nothing watched is made again with its watch once a hub comes; one with a watch is kept.</summary>
    private static async Task Review1009RootWatchChecks()
    {
        using var hub = Review1009Hub();
        var disk = new FakeDisk();
        disk.Folder(@"Q:\inside");
        using var tree = new NestedTree(disk.Read);
        NestedRoot[] roots = [new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)];
        tree.SetRoots(roots);
        var unwatched = tree.Find(@"Q:\")!;
        await tree.LoadAsync(unwatched);
        var hadNone = unwatched.Watch is null;

        tree.Changes = hub;
        tree.SetRoots(roots);
        var watched = tree.Find(@"Q:\")!;
        Check("a root made before the hub is made again with the hub's watch when the roots are set again",
            hadNone && !ReferenceEquals(watched, unwatched) && watched.Watch is not null
            && ReferenceEquals(watched.Watch, hub.RootFor(@"Q:\")));

        await tree.LoadAsync(watched);
        var inside = tree.Find(@"Q:\inside");
        tree.SetRoots(roots);
        Check("its folders take the watch from it, and a root with its watch is kept as it was",
            inside is not null && ReferenceEquals(inside.Watch, watched.Watch) && ReferenceEquals(tree.Find(@"Q:\"), watched) && watched.IsLoaded);
    }

    /// <summary>L13a: each watch opened parses into records of its own, which the root takes when the arm wins, counts carried on.</summary>
    private static async Task Review1009ArmRecordsChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerReview1009", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sinks = new List<object?>();
            var sinkField = typeof(DirectoryChangeWatcher).GetField("_sink", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            using var hub = new ChangeHub(TimeProvider.System);
            hub.OpenedForTests = opened =>
            {
                lock (sinks)
                {
                    sinks.Add(sinkField?.GetValue(opened));
                }
            };
            var watch = hub.AddRootForTests(folder);
            var rootRecords = watch.Records;
            await WaitUntil(() => watch.State == WatchState.Armed, 5_000);
            object? sink;
            lock (sinks)
            {
                sink = sinks.Count == 1 ? sinks[0] : null;
            }

            Check("an armed watch parses into records of its own, not the root's that another arm's watch could share",
                watch.State == WatchState.Armed && sink is RootRecords && rootRecords is not null && !ReferenceEquals(sink, rootRecords));

            hub.Register(ChangeConsumer.Nested, folder, new object());
            File.WriteAllText(Path.Combine(folder, "one.txt"), "1");
            await WaitUntil(() => Interlocked.Read(ref watch.Records!.Records) > 0, 5_000);
            Check($"and what the watch hears is counted on the root's records ({watch.Records!.Records} records, {watch.Records.Hits} hits)",
                ReferenceEquals(watch.Records, rootRecords) && watch.Records.Records > 0 && watch.Records.Hits > 0
                && sink is RootRecords own && own.Records == 0);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>L13b: a subst letter that stands for another folder after the volumes are looked at again takes its spelling and its paths along.</summary>
    private static void Review1009SubstChecks()
    {
        var target = @"\\server\one\dir";
        var volumes = new VolumeResolver(letter => letter == 'S'
            ? new VolumeResolver.Letter(WatchNative.DriveFixed, null, target)
            : new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null));
        using var hub = new ChangeHub(new ManualTime(), volumes) { ArmFailureForTests = _ => WatchNative.ErrorNotReady };
        var folder = new object();
        hub.Register(ChangeConsumer.Nested, @"S:\work", folder);
        var one = hub.RootFor(@"\\server\one")!;
        Check("a subst letter's path is on the root of the folder it stands for",
            hub.Registry.TryGet(@"S:\work", out var first) && ReferenceEquals(first.Root, one)
            && one.Prefixes.Contains(@"S:\", StringComparer.OrdinalIgnoreCase) && one.InterestCount == 1);

        target = @"\\server\two\dir";
        hub.InvalidateVolumes();
        var two = hub.RootFor(@"S:\")!;
        Check($"subst'ed elsewhere, the letter's spelling and paths leave the old root ({one.InterestCount} there, {two.InterestCount} on the new)",
            ReferenceEquals(two, hub.RootFor(@"\\server\two")) && !ReferenceEquals(one, two)
            && !one.Prefixes.Contains(@"S:\", StringComparer.OrdinalIgnoreCase) && two.Prefixes.Contains(@"S:\", StringComparer.OrdinalIgnoreCase)
            && hub.Registry.TryGet(@"S:\work", out var moved) && ReferenceEquals(moved.Root, two)
            && one.InterestCount == 0 && two.InterestCount == 1 && one.NestedInterest == 0 && two.NestedInterest == 1
            && one.InnerPathOf(@"S:\work") is null && two.InnerPathOf(@"S:\work") == @"dir\work");

        target = @"\\server\two\other";
        hub.InvalidateVolumes();
        var again = hub.RootFor(@"S:\")!;
        Check($"subst'ed to another folder on the same root, the spelling points there ({again.InnerPathOf(@"S:\work")})",
            ReferenceEquals(again, two) && two.InnerPathOf(@"S:\work") == @"other\work" && two.InterestCount == 1
            && two.Prefixes.Count(prefix => prefix.Equals(@"S:\", StringComparison.OrdinalIgnoreCase)) == 1);

        hub.Unregister(ChangeConsumer.Nested, @"S:\work", folder);
        Check("and the path is taken off the root it moved to", two.InterestCount == 0 && two.NestedInterest == 0 && hub.Registry.Count == 0);
    }

    /// <summary>M12: a slot given to another picture moves the atlas's count on, and with another canvas listening wakes it to draw its names again.</summary>
    private static void Review1009SlotReuseChecks()
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            PerFileCapacity = 2,
            MaximumSlices = 16,
            InitialSlices = 4,
            Source = source.Find
        });
        var wakes = 0;
        atlas.ArrivalsPending += () => Interlocked.Increment(ref wakes);
        var paths = Enumerable.Range(1, 4).Select(index => $@"C:\review1009\program-{index}.exe").ToArray();
        atlas.SlotFor(paths[0], "exe");
        atlas.SlotFor(paths[1], "exe");
        IconSettle(atlas, null, null, () => atlas.ExtractionCount >= 3);
        var reused = atlas.SlotsReused;
        Check("filling free slots reuses none", reused == 0 && atlas.FileSlotCount == 2);

        // Two frames drawing nothing: the next program takes a slot back.
        atlas.ProcessArrivals(null, null);
        atlas.ProcessArrivals(null, null);
        atlas.SlotFor(paths[2], "exe");
        IconWaitFor(() => atlas.ExtractionCount >= 4 && atlas.HasPendingArrivals);
        Thread.Sleep(50);
        var wakesBefore = Volatile.Read(ref wakes);
        atlas.ProcessArrivals(null, null);
        Check($"a slot taken back for another program moves the count on ({atlas.SlotsReused}), with no wake for a canvas alone",
            atlas.SlotsReused == reused + 1 && Volatile.Read(ref wakes) == wakesBefore && atlas.SlotFor(paths[2], "exe") >= 0);

        // A second canvas listens: the slot it held must not stay drawn.
        var second = 0;
        atlas.ArrivalsPending += () => Interlocked.Increment(ref second);
        Thread.Sleep(600);
        atlas.ProcessArrivals(null, null);
        atlas.ProcessArrivals(null, null);
        atlas.SlotFor(paths[3], "exe");
        IconWaitFor(() => atlas.ExtractionCount >= 5 && atlas.HasPendingArrivals);
        Thread.Sleep(50);
        var secondBefore = Volatile.Read(ref second);
        var reusedBefore = atlas.SlotsReused;
        atlas.ProcessArrivals(null, null);
        Check($"with two canvases, a slot taken back wakes them to draw their names again ({Volatile.Read(ref second) - secondBefore} wake)",
            atlas.SlotsReused == reusedBefore + 1 && Volatile.Read(ref second) == secondBefore + 1);

        atlas.ProcessArrivals(null, null);
        Check("a frame that reuses nothing wakes nobody", Volatile.Read(ref second) == secondBefore + 1 && atlas.SlotsReused == reusedBefore + 1);
    }

    /// <summary>L12a: whether a file's icon is still to come is asked of the atlas by state, not by comparing slots.</summary>
    private static void Review1009IconWaitChecks()
    {
        var pictures = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            Source = request =>
                request.Key.EndsWith("zzq", StringComparison.OrdinalIgnoreCase) ? new IconFound(-1, null)
                : request.Kind == IconKeyKind.File && !request.Key.Contains("plain", StringComparison.OrdinalIgnoreCase)
                    ? new IconFound(2, pictures.PixelsFor(request.Key))
                    : new IconFound(1, pictures.PixelsFor("the type's icon"))
        });
        const string folder = @"C:\review1009";
        atlas.SlotFor(folder, "plain.exe", "exe");
        atlas.SlotFor(folder, "own.exe", "exe");
        atlas.SlotFor(folder, "thing.zzq", "zzq");
        var waitingAtFirst = atlas.IconStillToCome(folder, "plain.exe", "exe") && atlas.IconStillToCome(folder, "thing.zzq", "zzq");
        IconSettle(atlas, null, null, () => atlas.ExtractionCount >= 4 && atlas.PendingKeyCount == 0);
        var plain = atlas.SlotFor(folder, "plain.exe", "exe");
        Check("icons asked for and not arrived are still to come", waitingAtFirst);
        Check("a program whose own icon is its type's has the type's slot, and waits for nothing",
            plain >= 0 && plain == atlas.SlotFor("exe") && !atlas.IconStillToCome(folder, "plain.exe", "exe"));
        Check("a program with its own icon waits for nothing once it has it",
            atlas.SlotFor(folder, "own.exe", "exe") != plain && !atlas.IconStillToCome(folder, "own.exe", "exe"));
        Check("a type the Shell has nothing for draws no icon, and waits for nothing",
            atlas.SlotFor(folder, "thing.zzq", "zzq") < 0 && !atlas.IconStillToCome(folder, "thing.zzq", "zzq"));
    }

    /// <summary>L12f: a selection handed by SetSelection is taken in, whatever version the window's last one had.</summary>
    private static async Task Review1009SetSelectionChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\a");
        disk.Folder(@"Q:\b");
        disk.Folder(@"Q:\c");
        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };

        var window = new ItemSelection();
        window.Apply(new SelectionEdit { Clear = true, Added = [new SelectionItem(@"Q:\a", true, 0)], Source = SelectionSource.Command });
        canvas.LoadSelection(window);
        var loaded = canvas.SelectedCount;

        // A selection made afresh has the same first version as the window's.
        canvas.SetSelection([@"Q:\b", @"Q:\c"], @"Q:\b");
        var handed = canvas.SelectedCount;
        Check($"a selection handed by SetSelection replaces one loaded at the version it starts at ({loaded} then {handed})",
            loaded == 1 && handed == 2);
    }
}
