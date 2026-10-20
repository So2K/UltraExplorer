using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task SearchMapChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(SearchMapChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(SearchMapChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Search map checks require isolated state and test-window mode.");
        SearchMapQueryChecks();
        SearchMapProjectionChecks();
        RunOnSta("search map: retained query context", SearchMapViewModelChecksAsync);
        RunOnSta("search map: paged Everything protocol", SearchMapPagingChecksAsync);
        RunOnSta("search map: complete and incomplete indexed sets", SearchMapIndexedChecksAsync);
        RunOnSta("search map: streamed membership and exact record identity", SearchMapStreamingChecksAsync);
        RunOnSta("search map: positions and gestures", SearchMapCanvasChecksAsync);
        RunOnSta("search map: uncapped scoped fallback", SearchMapFallbackChecksAsync);
        RunOnSta("search map: actual retained window lifecycle", SearchMapWindowChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task SearchMapWindowChecksAsync()
    {
        Section("search map: actual tab contexts ignore stale completion, retain positions and react to changed files");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
        var owned = Path.Combine(AppPaths.StateDirectory, "search-map-window-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(owned, "photos");
        var right = Path.Combine(owned, "documents");
        Directory.CreateDirectory(Path.Combine(left, "album", "deep"));
        Directory.CreateDirectory(Path.Combine(left, "empty.png"));
        Directory.CreateDirectory(Path.Combine(left, "notes"));
        Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "photo.png"), "fixture png match");
        await File.WriteAllTextAsync(Path.Combine(left, "readme.txt"), "fixture hidden text");
        await File.WriteAllTextAsync(Path.Combine(left, "album", "deep", "nested.png"), "fixture nested match");
        await File.WriteAllTextAsync(Path.Combine(left, "notes", "notes.txt"), "unmatched branch");
        await File.WriteAllTextAsync(Path.Combine(right, "document.txt"), "other tab text");
        await File.WriteAllTextAsync(Path.Combine(right, "photo.png"), "other tab png");
        MainWindow? host = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdFirst = true;
        try
        {
            var request = TabRequest(left);
            host = new MainWindow { Width = 1300, Height = 850, ShowActivated = false };
            var first = host.EnableFolderTabs(request);
            var firstShell = TabShell(first);
            firstShell.SuppressShellWrites = true;
            SearchMapInjectBuilder(first, new SearchMapProjectionBuilder(indexReady: () => false,
                scan: async (query, roots, _) =>
                {
                    if (holdFirst)
                    {
                        holdFirst = false;
                        started.TrySetResult();
                        await release.Task; // Deliberately ignores cancellation: stale replies must still be rejected.
                    }
                    return SearchMapOwnedScan(query, roots);
                }));
            host.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using (ActivationGuard.GuardWindowsCreated()) host.Show();
            Check("a real cloaked host initializes the search fixture without activation", await TabWait(() => first.IsFolderWindowReady));
            if (!first.IsFolderWindowReady) return;
            Check("the first context opens its requested folder", await first.ApplyFolderInvocationAsync(request));
            host.Width = 1300;
            host.Height = 850;
            first.UseNestedDrivesForChecks([new NestedRoot(left, "Photos", NestedFolderKind.Drive)]);
            firstShell.Layout = CanvasLayout.Nested;
            await first.FirstPane.FlyToAsync(left, gentle: false, animated: false);
            await SearchMapLoadAllAsync(first.FirstPane.Tree);
            await SplitSettleAsync(first);
            foreach (var folder in SearchMapKnownFolders(first.FirstPane.Tree.Root)) first.FirstPane.Tree.EnsureLayout(folder);
            foreach (var pane in first.Panes) pane.Canvas.RenderNow();
            var tree = first.FirstPane.Tree;
            await SearchMapSaveWindowAsync(host, "map-before.png");
            var folders = SearchMapKnownFolders(tree.Root).ToArray();
            foreach (var folder in folders) tree.EnsureLayout(folder);
            var camera = first.FirstPane.Canvas.CaptureCamera();
            var geometry = folders.ToDictionary(folder => folder, folder => (folder.Index, folder.OffsetX, folder.OffsetY, folder.Scale, folder.Grid, folder.FileGrid));

            firstShell.Search.ShowOnMap = true;
            firstShell.Search.Text = "*.png";
            Check("the map starts its scoped request", await TabWait(() => started.Task.IsCompleted && firstShell.Search.IsMapBusy));
            firstShell.Search.Text = string.Empty;
            Check("clear immediately removes the mask while a request is still running", first.FirstPane.Canvas.SearchMapProjection is null
                && !firstShell.Search.IsMapBusy && first.FirstPane.Canvas.CaptureCamera() == camera);
            release.TrySetResult();
            await first.SearchMapTaskForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check("a stale completed query cannot put its mask back after clearing", first.FirstPane.Canvas.SearchMapProjection is null
                && firstShell.Search.Text.Length == 0 && !firstShell.Search.IsMapBusy);

            firstShell.Search.Text = "*.png";
            Check("the next query completes and applies the expected file set", await TabWait(() => !firstShell.Search.IsMapBusy
                && first.FirstPane.Canvas.SearchMapProjection is { FileCount: 2 }));
            var mask = first.FirstPane.Canvas.SearchMapProjection;
            var maskedCamera = first.FirstPane.Canvas.CaptureCamera();
            var changedGeometry = folders.Where(folder => geometry[folder] !=
                (folder.Index, folder.OffsetX, folder.OffsetY, folder.Scale, folder.Grid, folder.FileGrid)).ToArray();
            if (maskedCamera != camera) Console.WriteLine($"        camera before {camera}; masked {maskedCamera}");
            foreach (var folder in changedGeometry)
                Console.WriteLine($"        geometry {folder.FullPath}: {geometry[folder]} -> {(folder.Index, folder.OffsetX, folder.OffsetY, folder.Scale, folder.Grid, folder.FileGrid)}");
            Check("actual window masking keeps the camera unchanged", maskedCamera == camera);
            Check("actual window masking keeps every grid and nested coordinate unchanged", changedGeometry.Length == 0);
            Check("actual window projection hides an unmatched directory named .png", mask is not null
                && mask.ContainsFolder(Path.Combine(left, "album", "deep")) && !mask.ContainsFolder(Path.Combine(left, "notes"))
                && !mask.ContainsFolder(Path.Combine(left, "empty.png")) && !mask.ContainsFile(left, "readme.txt"));
            await SearchMapSaveWindowAsync(host, "map-filtered.png");

            var second = host.NewExplorerTab(right);
            var secondShell = TabShell(second);
            secondShell.SuppressShellWrites = true;
            SearchMapInjectBuilder(second, new SearchMapProjectionBuilder(indexReady: () => false,
                scan: (query, roots, _) => Task.FromResult(SearchMapOwnedScan(query, roots))));
            Check("another retained tab initializes its own map", await TabWait(() => second.IsFolderWindowReady && !second.IsFolderInvocationPending));
            second.UseNestedDrivesForChecks([new NestedRoot(right, "Documents", NestedFolderKind.Drive)]);
            secondShell.Layout = CanvasLayout.Nested;
            await second.FirstPane.FlyToAsync(right, gentle: false, animated: false);
            await SearchMapLoadAllAsync(second.FirstPane.Tree);
            secondShell.Search.ShowOnMap = true;
            secondShell.Search.Text = "*.txt";
            bool FirstQueryStillCorrect() => firstShell.Search.Text == "*.png" && firstShell.Search.ShowOnMap
                && first.FirstPane.Canvas.SearchMapProjection is { FileCount: 2 } current
                && current.ContainsFile(left, "photo.png") && current.ContainsFile(Path.Combine(left, "album", "deep"), "nested.png")
                && !current.ContainsFile(left, "readme.txt") && !current.ContainsFolder(Path.Combine(left, "empty.png"));
            Check("a second tab applies an independent query and scope", await TabWait(() => !secondShell.Search.IsMapBusy
                && second.FirstPane.Canvas.SearchMapProjection is { FileCount: 1 })
                && second.FirstPane.Canvas.SearchMapProjection!.ContainsFile(right, "document.txt")
                && !second.FirstPane.Canvas.SearchMapProjection.ContainsFile(right, "photo.png")
                && FirstQueryStillCorrect());
            host.SelectTabContext(first, focus: false);
            await SplitSettleAsync(first);
            // A real change observer may replace an immutable mask with an
            // equivalent fresh one while another context initializes. Assert
            // user-visible state and membership rather than object identity.
            if (!FirstQueryStillCorrect() || first.FirstPane.Canvas.CaptureCamera() != camera)
                Console.WriteLine($"        retained tab query {firstShell.Search.Text}; files {first.FirstPane.Canvas.SearchMapProjection?.FileCount}; camera {first.FirstPane.Canvas.CaptureCamera()}; expected {camera}");
            Check("returning to the first tab restores its query, mask and camera together", FirstQueryStillCorrect()
                && first.FirstPane.Canvas.CaptureCamera() == camera);

            var newFile = Path.Combine(left, "arrived.png");
            await File.WriteAllTextAsync(newFile, "new match");
            Check("filesystem changes refresh the active map without editing the query", await TabWait(() => !firstShell.Search.IsMapBusy
                && first.FirstPane.Canvas.SearchMapProjection?.ContainsFile(left, "arrived.png") == true, 15000));
            firstShell.Search.Text = string.Empty;
            Check("the actual host clears the mask synchronously", first.FirstPane.Canvas.SearchMapProjection is null && !firstShell.Search.IsMapBusy);
            await SearchMapSaveWindowAsync(host, "map-cleared.png");
            host.Width = 960;
            await SplitSettleAsync(first);
            await SearchMapSaveWindowAsync(host, "map-960.png");
            Check("the search controls remain in a visible minimum-width host", host.ActualWidth >= 960 && first.ContextRoot.ActualWidth > 0);
            Check("closing the tab host disposes search contexts and pending work", await TabCloseHostAsync(host)
                && firstShell.IsDisposed && secondShell.IsDisposed);
        }
        finally
        {
            release.TrySetResult();
            if (host is { IsVisible: true }) await TabCloseHostAsync(host);
            TryDelete(owned);
        }
    }

    private static void SearchMapInjectBuilder(MainWindow window, SearchMapProjectionBuilder builder)
        => typeof(MainWindow).GetField("_mapSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, builder);

    private static SearchMapScanResult SearchMapOwnedScan(SearchQuery query, IReadOnlyList<string> roots)
    {
        var files = roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => query.Matches(Path.GetFileName(path), Path.GetDirectoryName(path)!, false)).Distinct(StringComparer.Ordinal).ToArray();
        var folders = roots.SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .Where(path => query.Matches(Path.GetFileName(path), Path.GetDirectoryName(path)!, true)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(files, folders, []);
    }

    private static IEnumerable<NestedFolder> SearchMapKnownFolders(NestedFolder folder)
    {
        yield return folder;
        foreach (var child in folder.Children)
            foreach (var below in SearchMapKnownFolders(child)) yield return below;
    }

    private static async Task SearchMapLoadAllAsync(NestedTree tree)
    {
        // A real canvas may already have started the read. Unlike the fake-disk
        // helper, await queued/running reads too before descending into them.
        var level = tree.Root.Children.ToArray();
        while (level.Length > 0)
        {
            await Task.WhenAll(level.Where(folder => folder.CanLoad).Select(folder => tree.LoadAsync(folder)));
            foreach (var folder in level) tree.EnsureLayout(folder);
            level = level.SelectMany(folder => folder.Children).ToArray();
        }
    }

    private static async Task SearchMapSaveWindowAsync(MainWindow host, string name)
    {
        await SettingsSettle();
        host.UpdateLayout();
        foreach (var pane in host.ActiveTabContext.Panes) pane.Canvas.RenderNow();
        var root = (FrameworkElement)host.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var directory = Environment.GetEnvironmentVariable("ULTRAEXPLORER_SEARCH_MAP_SCREENSHOTS")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "search-map");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, name);
        using var stream = File.Create(destination);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        Console.WriteLine("        actual map host screenshot: " + Path.GetFullPath(destination));
    }

    private static async Task SearchMapIndexedChecksAsync()
    {
        Section("search map: page every scoped indexed match and distinguish partial or unsupported answers");
        var indexed = Enumerable.Range(0, 5001).Select(index => new EverythingItem(
            $"image-{index:D5}.png", @"Q:\scope\deep", false, index, null, string.Empty)).ToArray();
        var calls = new List<(string Query, int Maximum, uint Offset, uint Sort)>();
        var scanned = 0;
        var builder = new SearchMapProjectionBuilder(
            queryPage: (query, maximum, offset, token, sort) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add((query, maximum, offset, sort));
                return Task.FromResult<EverythingPage?>(new EverythingPage(indexed.Length,
                    indexed.Skip(checked((int)offset)).Take(maximum).ToArray()) { Offset = offset });
            }, indexReady: () => true,
            scan: (_, _, _) => { scanned++; throw new InvalidOperationException("A complete index must need no filesystem fallback."); });
        var result = await builder.RunAsync("*.png", [@"Q:\scope"], CancellationToken.None);
        Check("an indexed map includes all 5,001 matches across actual pages", result.IsComplete && !result.Unsupported
            && result.Projection is { FileCount: 5001 } && result.Projection.ContainsFile(@"Q:\scope\deep", "image-05000.png"));
        Check("the indexed map pages with a stable order and independent scope", calls.Count > 1 && calls[0].Offset == 0
            && calls.All(call => call.Sort == EverythingClient.SortNameAscending && call.Query.Contains(@"path:""Q:\scope\""", StringComparison.Ordinal))
            && calls.Skip(1).Zip(calls, (next, previous) => next.Offset == previous.Offset + previous.Maximum).All(equal => equal)
            && scanned == 0);

        var fallbackCalls = 0;
        var partial = new SearchMapProjectionBuilder(
            queryPage: (_, _, offset, _, _) => Task.FromResult<EverythingPage?>(new EverythingPage(2000,
                [new EverythingItem("first.png", @"Q:\scope", false, 0, null, string.Empty)]) { Offset = offset, IsComplete = false }),
            indexReady: () => true,
            scan: (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                fallbackCalls++;
                return Task.FromResult(new SearchMapScanResult([@"Q:\scope\only-correct.png"], [], []));
            });
        var recovered = await partial.RunAsync("*.png", [@"Q:\scope"], CancellationToken.None);
        Check("a truncated indexed page falls back to a complete local evaluation", recovered.IsComplete && !recovered.Unsupported
            && recovered.Projection is { FileCount: 1 } && recovered.Projection.ContainsFile(@"Q:\scope", "only-correct.png") && fallbackCalls == 1);
        Check("partial indexed hits are not retained as authoritative matches after fallback", recovered.Projection?.ContainsFile(@"Q:\scope", "first.png") == false);

        var advanced = new SearchMapProjectionBuilder(
            queryPage: (_, _, offset, _, _) => Task.FromResult<EverythingPage?>(new EverythingPage(1,
                [new EverythingItem("large.png", @"Q:\scope", false, 20_000_000, null, string.Empty)]) { Offset = offset }),
            indexReady: () => true,
            scan: (_, _, _) => throw new InvalidOperationException("Advanced criteria cannot be guessed locally."));
        var precise = await advanced.RunAsync("size:>10mb", [@"Q:\scope"], CancellationToken.None);
        Check("a complete index can accurately apply advanced Everything criteria", precise.IsComplete && !precise.Unsupported
            && precise.Projection?.ContainsFile(@"Q:\scope", "large.png") == true
            && !precise.Projection.ContainsFile(@"Q:\scope", "small.png"));
        var unsupported = await partial.RunAsync("size:>10mb", [@"Q:\scope"], CancellationToken.None);
        Check("an incomplete advanced answer cannot silently drop its size condition", unsupported.Unsupported && unsupported.Projection is null
            && !unsupported.IsComplete && fallbackCalls == 1);

        var unknown = new SearchMapProjectionBuilder(indexReady: () => false,
            scan: (_, _, _) => Task.FromResult(new SearchMapScanResult([@"Q:\scope\known.png"], [], [@"Q:\scope\locked"])));
        var incomplete = await unknown.RunAsync("*.png", [@"Q:\scope"], CancellationToken.None);
        Check("an unread branch is reported as incomplete and retained for later inspection", !incomplete.IsComplete && !incomplete.Unsupported
            && incomplete.Projection?.ContainsFolder(@"Q:\scope\locked\deeper") == true
            && incomplete.Projection.ContainsFile(@"Q:\scope\locked", "pending.png")
            && !incomplete.Projection.ContainsFile(@"Q:\scope\locked", "unmatched.txt"));

        foreach (var problem in new[] { "offset", "total changed", "duplicate", "empty early" })
        {
            var recoveries = 0;
            var unstable = new SearchMapProjectionBuilder(indexReady: () => true,
                queryPage: (_, maximum, offset, _, _) =>
                {
                    var pageItems = indexed.Skip(checked((int)offset)).Take(maximum).ToArray();
                    var pageTotal = indexed.Length;
                    var pageOffset = offset;
                    if (problem == "offset") pageOffset++;
                    if (offset > 0 && problem == "total changed") pageTotal++;
                    if (offset > 0 && problem == "duplicate") pageItems[0] = indexed[0];
                    if (problem == "empty early") pageItems = [];
                    return Task.FromResult<EverythingPage?>(new EverythingPage(pageTotal, pageItems) { Offset = pageOffset });
                },
                scan: (_, _, _) =>
                {
                    recoveries++;
                    return Task.FromResult(new SearchMapScanResult([@"Q:\scope\verified.png"], [], []));
                });
            var stable = await unstable.RunAsync("*.png", [@"Q:\scope"], CancellationToken.None);
            Check($"an unstable index cannot publish an incorrect empty/partial mask ({problem})", stable.IsComplete && !stable.Unsupported
                && stable.Projection is { FileCount: 1 } && stable.Projection.ContainsFile(@"Q:\scope", "verified.png") && recoveries == 1);
        }

        var boundary = new SearchMapProjectionBuilder(indexReady: () => false,
            scan: (_, _, _) => Task.FromResult(new SearchMapScanResult([@"Q:\scope\inside.png", @"Q:\scope2\outside.png"],
                [@"Q:\scope\inside", @"Q:\scope2\outside"], [@"Q:\scope2\unknown"])));
        var bounded = await boundary.RunAsync("image", [@"Q:\scope"], CancellationToken.None);
        Check("the projection rechecks injected/indexed paths against the physical scope boundary", bounded.Projection is { FileCount: 1 }
            && !bounded.Projection.ContainsFolder(@"Q:\scope2") && !bounded.Projection.ContainsFolder(@"Q:\scope2\unknown")
            && bounded.IsComplete);
    }

    private static async Task SearchMapStreamingChecksAsync()
    {
        Section("search map: streamed pages preserve exact record identity and immutable membership");
        const string firstRoot = @"Q:\scope";
        // Disjoint roots on one synthetic volume avoid a real mapped drive
        // changing coverage and bypassing this injected index fixture.
        const string secondRoot = @"Q:\second-scope";
        var first = Enumerable.Range(0, 9003).Select(index => new EverythingItem($"image-{index:D5}.png",
            firstRoot + @"\deep", false, index, null, string.Empty)).Concat(new[]
        {
            new EverythingItem("Case.PNG", firstRoot + @"\case", false, 0, null, ""),
            new EverythingItem("Case.PNG", @"q:\SCOPE\CASE", false, 0, null, ""),
            new EverythingItem("case.PNG", firstRoot + @"\case", false, 0, null, ""),
            new EverythingItem("both", firstRoot, false, 0, null, ""),
            new EverythingItem("both", firstRoot, true, 0, null, ""),
            new EverythingItem("literal. ", firstRoot, false, 0, null, ""),
            new EverythingItem("", firstRoot, true, 0, null, ""),
            new EverythingItem("outside.png", @"Q:\scope2", false, 0, null, ""),
            new EverythingItem("bare", "", false, 0, null, ""),
            new EverythingItem("bare", @"\", false, 0, null, ""),
            new EverythingItem("", @"Q:\", true, 0, null, ""),
            new EverythingItem("", @"\\server\share\", true, 0, null, "")
        }).ToArray();
        EverythingItem[] second =
        [
            new("other.bin", secondRoot, false, 0, null, ""),
            new("other-folder", secondRoot, true, 0, null, "")
        ];
        var fallbacks = 0;
        var calls = new List<(string Query, uint Offset)>();
        var builder = new SearchMapProjectionBuilder(indexReady: () => true,
            queryPage: (query, maximum, offset, _, _) =>
            {
                calls.Add((query, offset));
                var items = query.Contains(secondRoot, StringComparison.Ordinal) ? second : first;
                return Task.FromResult<EverythingPage?>(new EverythingPage(items.Length,
                    items.Skip(checked((int)offset)).Take(maximum).ToArray()) { Offset = offset });
            }, scan: (_, _, _) =>
            {
                fallbacks++;
                return Task.FromResult(new SearchMapScanResult([firstRoot + @"\wrong-fallback.png"], [], []));
            });
        var answer = await builder.RunAsync("*", [firstRoot, secondRoot], CancellationToken.None);
        Check("streaming retains every page across two roots without using the result-panel caps",
            answer is { IsComplete: true, Source: SearchSource.Everything, Projection.FileCount: 9008 }
            && answer.Projection.ContainsFile(firstRoot + @"\deep", "image-09002.png")
            && answer.Projection.ContainsFile(secondRoot, "other.bin") && calls.Count == 4 && fallbacks == 0);
        Check("parent-case twins remain valid records while final parent lookup is case-insensitive and names stay ordinal",
            answer.Projection is { } names && names.ContainsFile(@"q:\scope\CASE", "Case.PNG")
            && names.ContainsFile(firstRoot + @"\case", "case.PNG") && !names.ContainsFile(firstRoot + @"\case", "CASE.PNG"));
        Check("same-path kind twins and literal file names keep their distinct map roles",
            answer.Projection is { } kinds && kinds.ContainsFile(firstRoot, "both") && kinds.IsFolderMatch(firstRoot + @"\both")
            && kinds.ContainsFile(firstRoot, "literal. ") && !kinds.ContainsFile(firstRoot, "literal")
            && kinds.IsFolderMatch(firstRoot) && !kinds.ContainsFolder(@"Q:\scope2"));
        Check("bare, root-relative, drive and share records are validated without inventing out-of-scope matches",
            answer.IsComplete && fallbacks == 0 && answer.Projection is { } scoped
            && !scoped.ContainsFolder(@"\\server\share") && !scoped.ContainsFile(@"Q:\scope2", "outside.png"));

        foreach (var problem in new[] { "in scope", "outside scope", "ignored folder", "drive root", "share root" })
        {
            var repeated = problem switch
            {
                "outside scope" => new EverythingItem("first.png", @"Q:\scope2", false, 0, null, ""),
                "ignored folder" => new EverythingItem("folder.png", firstRoot, true, 0, null, ""),
                "drive root" => new EverythingItem("", @"Q:\", true, 0, null, ""),
                "share root" => new EverythingItem("", @"\\server\share\", true, 0, null, ""),
                _ => new EverythingItem("first.png", firstRoot, false, 0, null, "")
            };
            var duplicate = problem == "share root" ? repeated with { Directory = @"\\?\UNC\server\share\" } : repeated;
            var recovered = 0;
            var unstable = new SearchMapProjectionBuilder(indexReady: () => true,
                queryPage: (_, maximum, offset, _, _) =>
                {
                    var items = offset == 0
                        ? new[] { repeated }.Concat(Enumerable.Range(0, maximum - 1).Select(index => new EverythingItem(
                            $"filler-{index:D5}.png", firstRoot, false, 0, null, ""))).ToArray()
                        : new[] { duplicate };
                    return Task.FromResult<EverythingPage?>(new EverythingPage(SearchMapProjectionBuilder.PageSize + 1, items) { Offset = offset });
                }, scan: (_, _, _) =>
                {
                    recovered++;
                    return Task.FromResult(new SearchMapScanResult([firstRoot + @"\verified.png"], [], [firstRoot + @"\unknown"]));
                });
            var result = await unstable.RunAsync("*.png", [firstRoot], CancellationToken.None);
            Check($"exact cross-page duplicates cannot hide a missing record even when ignored by projection ({problem})",
                recovered == 1 && result.Source == SearchSource.Walk && !result.IsComplete
                && result.Projection is { FileCount: 1 } mask && mask.ContainsFile(firstRoot, "verified.png")
                && mask.ContainsFolder(firstRoot + @"\unknown\deeper") && mask.ContainsFile(firstRoot + @"\unknown", "pending.png")
                && !mask.ContainsFile(firstRoot + @"\unknown", "pending.txt"));
        }

        var promotionFallbacks = 0;
        async Task<SearchMapProjectionResult> PromotedRecords(EverythingItem[] items)
        {
            var promoted = new SearchMapProjectionBuilder(indexReady: () => true,
                queryPage: (_, maximum, offset, _, _) => Task.FromResult<EverythingPage?>(new EverythingPage(items.Length,
                    items.Skip(checked((int)offset)).Take(maximum).ToArray()) { Offset = offset }),
                scan: (_, _, _) =>
                {
                    promotionFallbacks++;
                    return Task.FromResult(new SearchMapScanResult([firstRoot + @"\verified-promotion.png"], [], []));
                });
            return await promoted.RunAsync("*", [firstRoot], CancellationToken.None);
        }
        var firstRecord = new EverythingItem("first.png", firstRoot, false, 0, null, "");
        var duplicateAfterPromotion = await PromotedRecords(
        [
            firstRecord,
            new("second.png", firstRoot, false, 0, null, ""),
            firstRecord
        ]);
        Check("a duplicate of the first record remains detectable after its directory ledger is promoted",
            promotionFallbacks == 1 && duplicateAfterPromotion.Source == SearchSource.Walk
            && duplicateAfterPromotion.Projection is { FileCount: 1 } verified
            && verified.ContainsFile(firstRoot, "verified-promotion.png"));
        var kindPromotion = await PromotedRecords(
        [
            new("both", firstRoot, false, 0, null, ""),
            new("both", firstRoot, true, 0, null, ""),
            new("", @"Q:\", false, 0, null, ""),
            new("", @"Q:\", true, 0, null, "")
        ]);
        Check("same-name kind twins promote independently, including an empty drive-root leaf",
            promotionFallbacks == 1 && kindPromotion.Source == SearchSource.Everything && kindPromotion.IsComplete
            && kindPromotion.Projection is { FileCount: 1 } kindsAfterPromotion
            && kindsAfterPromotion.ContainsFile(firstRoot, "both") && kindsAfterPromotion.IsFolderMatch(firstRoot + @"\both"));

        using var canceled = new CancellationTokenSource();
        var canceledBuilder = new SearchMapProjectionBuilder(indexReady: () => true,
            queryPage: (_, maximum, offset, _, _) =>
            {
                if (offset > 0) canceled.Cancel();
                return Task.FromResult<EverythingPage?>(new EverythingPage(9003,
                    first.Skip(checked((int)offset)).Take(maximum).ToArray()) { Offset = offset });
            }, scan: (_, _, _) => throw new InvalidOperationException("Cancellation must not start a fallback."));
        var stopped = false;
        try { _ = await canceledBuilder.RunAsync("*.png", [firstRoot], canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check("canceling after a full streamed page never publishes its partial projection or starts a fallback", stopped);

        var accumulator = new SearchMapProjection.Accumulator(SearchQuery.Parse("*.png"));
        accumulator.AddFile(firstRoot, "frozen.png");
        var frozen = accumulator.Build();
        var consumed = false;
        try { accumulator.AddFile(firstRoot, "late.png"); }
        catch (InvalidOperationException) { consumed = true; }
        Check("completed membership owns its immutable data after the temporary accumulator is consumed",
            consumed && frozen.FileCount == 1 && frozen.ContainsFile(firstRoot, "frozen.png") && !frozen.ContainsFile(firstRoot, "late.png"));
        using var freezeCancellation = new CancellationTokenSource();
        var abandoned = new SearchMapProjection.Accumulator(SearchQuery.Parse("*.png"), freezeCancellation.Token);
        abandoned.AddFile(firstRoot, "never-published.png");
        freezeCancellation.Cancel();
        stopped = false;
        try { _ = abandoned.Build(); }
        catch (OperationCanceledException) { stopped = true; }
        Check("an accumulator canceled after collecting matches cannot publish an immutable result", stopped);
    }

    private static void SearchMapProjectionChecks()
    {
        Section("search map: matches retain their ancestor chain without retaining neighboring branches");
        var projection = SearchMapProjection.Create("*.png",
            [@"Q:\Photos\image.PNG", @"Q:\Photos\deep\second.png", @"\\server\share\shots\image.png"]);
        Check("matching files are found by parent and filename, including a differently cased parent and trailing separator",
            projection.ContainsFile(@"q:\photos\", "image.PNG")
            && projection.ContainsFile(@"Q:\Photos\deep", "second.png"));
        Check("every directory on a matching file's physical path remains visible", projection.ContainsFolder(@"Q:\")
            && projection.ContainsFolder(@"Q:\Photos") && projection.ContainsFolder(@"Q:\Photos\deep"));
        Check("an unmatched file, sibling and similarly prefixed folder stay hidden", !projection.ContainsFile(@"Q:\Photos", "notes.txt")
            && !projection.ContainsFolder(@"Q:\Photos\empty") && !projection.ContainsFolder(@"Q:\Photos2"));
        Check("UNC shares retain their ancestry and use the same matching rules", projection.ContainsFolder(@"\\server\share")
            && projection.ContainsFolder(@"\\server\share\shots")
            && projection.ContainsFile(@"\\SERVER\SHARE\shots", "image.png")
            && !projection.ContainsFolder(@"\\server\share2\shots"));
        Check("the projection counts every match rather than only displayed rows", projection.FileCount == 3);
        var folder = SearchMapProjection.Create("reports", [], [@"Q:\work\reports"]);
        Check("a directly matching directory retains itself and its parents", folder.ContainsFolder(@"Q:\work\reports") && folder.ContainsFolder(@"Q:\work"));
        Check("a directly matching directory does not make all its contents matches", !folder.ContainsFolder(@"Q:\work\reports\unrelated")
            && !folder.ContainsFile(@"Q:\work\reports", "unrelated.txt"));
        var unknown = SearchMapProjection.Create("*.png", [], unknownBranches: [@"Q:\pending"], isComplete: false);
        Check("a branch whose contents are unknown is not claimed to be empty", unknown.ContainsFolder(@"Q:\pending") && unknown.ContainsFolder(@"Q:\"));
        Check("unknown branch preservation does not leak into a neighboring known branch", !unknown.ContainsFolder(@"Q:\pending2"));
        Check("loaded names in an unknown branch still obey the supported query", unknown.ContainsFile(@"Q:\pending\deep", "future.png")
            && !unknown.ContainsFile(@"Q:\pending\deep", "notes.txt"));
        var caseSensitive = SearchMapProjection.Create("wfn:\"exact.png\"", [@"Q:\case\exact.png"]);
        Check("a case-sensitive folder's unreturned twin is not invented as a match", caseSensitive.ContainsFile(@"Q:\case", "exact.png")
            && !caseSensitive.ContainsFile(@"Q:\case", "EXACT.PNG"));
    }

    private static async Task SearchMapCanvasChecksAsync()
    {
        Section("search map: hide blocks in place, retain camera and exclude hidden items from gestures");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\map", "01-first.png", 1);
        disk.AddFile(@"Q:\map", "02-hidden.txt", 2);
        disk.AddFile(@"Q:\map", "03-second.png", 3);
        disk.AddFile(@"Q:\map\photos\deep", "nested.png", 4);
        disk.AddFile(@"Q:\map\photos", "notes.txt", 5);
        disk.AddFile(@"Q:\map\docs", "readme.txt", 6);
        disk.Folder(@"Q:\map\empty.png");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = FilterStepCanvas(tree);
        var secondCanvas = FilterStepCanvas(tree);
        var map = tree.Find(@"Q:\map")!;
        var docs = tree.Find(@"Q:\map\docs")!;
        var empty = tree.Find(@"Q:\map\empty.png")!;
        var photos = tree.Find(@"Q:\map\photos")!;
        var deep = tree.Find(@"Q:\map\photos\deep")!;
        var projection = SearchMapProjection.Create("*.png",
            [@"Q:\map\01-first.png", @"Q:\map\03-second.png", @"Q:\map\photos\deep\nested.png"]);
        var all = new[] { tree.Root, tree.Find(@"Q:\")!, map, docs, empty, photos, deep };
        try
        {
            canvas.FlyTo(map, 0.9, animated: false);
            canvas.RenderNow();
            var beforeCamera = canvas.CaptureCamera();
            var beforeGeometry = all.ToDictionary(folder => folder, folder =>
                (folder.Index, folder.OffsetX, folder.OffsetY, folder.Scale, folder.Grid, folder.FileGrid,
                    Files: string.Join('\0', folder.Files.Select(file => file.Name)),
                    Children: string.Join('\0', folder.Children.Select(child => child.FullPath))));
            var beforeRects = all.ToDictionary(folder => folder, folder => canvas.ScreenRectOf(folder));
            var original = FilterTilePixels(canvas);
            var mapRect = beforeRects[map]!.Value;
            var firstPoint = TilePoint(mapRect, map, 0);
            var hiddenPoint = TilePoint(mapRect, map, 1);
            var lastPoint = TilePoint(mapRect, map, 2);
            var docsPoint = TitlePoint(beforeRects[docs]!.Value);
            var emptyPoint = TitlePoint(beforeRects[empty]!.Value);
            Check("the fixture's hidden file and empty directory are initially reachable", canvas.HitTest(hiddenPoint)?.Path == @"Q:\map\02-hidden.txt"
                && canvas.HitTest(emptyPoint)?.Folder == empty);

            canvas.SetSearchMapProjection(projection);
            canvas.RenderNow();
            var filtered = FilterTilePixels(canvas);
            Check("applying a mask changes the picture without changing the camera", filtered != original && canvas.CaptureCamera() == beforeCamera);
            Check("applying a mask retains every grid slot, file order and folder coordinate", all.All(folder => beforeGeometry[folder] ==
                (folder.Index, folder.OffsetX, folder.OffsetY, folder.Scale, folder.Grid, folder.FileGrid,
                    string.Join('\0', folder.Files.Select(file => file.Name)), string.Join('\0', folder.Children.Select(child => child.FullPath)))));
            Check("every folder remains at exactly its original rectangle", all.All(folder => canvas.ScreenRectOf(folder) == beforeRects[folder]));
            Check("a matching tile stays reachable in its original slot", canvas.HitTest(firstPoint)?.Path == @"Q:\map\01-first.png"
                && canvas.HitTest(lastPoint)?.Path == @"Q:\map\03-second.png");
            Check("a hidden tile becomes the visible containing folder's space", canvas.HitTest(hiddenPoint) is { IsFile: false } hiddenHit && hiddenHit.Folder == map);
            Check("hidden branches are not selected as folders or file-drop destinations", canvas.HitTest(docsPoint)?.Folder != docs && canvas.HitTest(emptyPoint)?.Folder != empty);
            Check("another retained canvas keeps its own mask state", secondCanvas.SearchMapProjection is null);

            var keptSelection = new ItemSelection();
            keptSelection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(@"Q:\map\01-first.png", false, 1),
                    new SelectionItem(@"Q:\map\02-hidden.txt", false, 2), new SelectionItem(@"Q:\map\docs", true, 0)],
                Focus = @"Q:\map\01-first.png", Anchor = @"Q:\map\01-first.png",
            });
            canvas.LoadSelection(keptSelection);
            Check("a mask retains the underlying selection but reports hidden selected items to Delete safety", canvas.SelectedCount == 3
                && canvas.ShownOfSelection(keptSelection.Version) == 1);
            canvas.SetSearchMapProjection(null);
            Check("clearing restores the shown selection count without losing selected items", canvas.SelectedCount == 3
                && canvas.ShownOfSelection(keptSelection.Version) == 3);
            canvas.SetSearchMapProjection(projection);

            canvas.Pointer.Click(firstPoint);
            canvas.Pointer.Click(lastPoint, ModifierKeys.Shift);
            Check("Shift ranges skip hidden original slots", Picked(canvas).SetEquals([@"Q:\map\01-first.png", @"Q:\map\03-second.png"]));
            canvas.Pointer.Click(firstPoint);
            canvas.HandleKey(Key.Right, ModifierKeys.None);
            Check("keyboard navigation skips a hidden item in the same row", Picked(canvas).SetEquals([@"Q:\map\03-second.png"]));
            canvas.Pointer.Click(firstPoint);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check("Ctrl+A takes matching files and excludes ancestor folders that only provide context", Picked(canvas).SetEquals(
                [@"Q:\map\01-first.png", @"Q:\map\03-second.png"]));
            canvas.HandleKey(Key.Escape, ModifierKeys.None);
            var folderProjection = SearchMapProjection.Create("photos", [], [@"Q:\map\photos"]);
            canvas.SetSearchMapProjection(folderProjection);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check("bulk selection can still take a folder that directly matches the query", Picked(canvas).SetEquals([@"Q:\map\photos"]));
            canvas.HandleKey(Key.Escape, ModifierKeys.None);

            // New listings are reconciled without changing the projection into
            // a destructive tree filter. A still-matching file is always judged
            // against its original grid index.
            canvas.SetSearchMapProjection(null);
            canvas.RenderNow();
            Check("clearing restores the original camera and hit targets immediately", canvas.CaptureCamera() == beforeCamera
                && canvas.HitTest(hiddenPoint)?.Path == @"Q:\map\02-hidden.txt"
                && canvas.HitTest(docsPoint)?.Folder == docs && canvas.HitTest(emptyPoint)?.Folder == empty);
            Check("clearing restores the same scene pixels", FilterTilePixels(canvas).Scene == original.Scene);

            canvas.FlyTo(docs, 8, animated: false);
            canvas.RenderNow();
            var insideHidden = canvas.CaptureCamera();
            canvas.SetSearchMapProjection(projection);
            canvas.RenderNow();
            Check("a zoomed camera inside a hidden branch cannot hit or draw its contents", canvas.CaptureCamera() == insideHidden
                && canvas.HitTest(new Point(600, 400))?.Folder != docs);
        }
        finally
        {
            canvas.SetSearchMapProjection(null);
            canvas.Tree = null;
            secondCanvas.Tree = null;
        }
    }

    private static async Task SearchMapFallbackChecksAsync()
    {
        Section("search map: the scoped filesystem fallback finds all matches beyond display limits");
        var classes = (string[])typeof(EverythingClient).GetField("WindowClasses", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var kept = (string[])classes.Clone();
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSmoke", "search-map-" + Guid.NewGuid().ToString("N"));
        var scope = Path.Combine(root, "scope");
        var deep = Path.Combine(scope, "branch", "deep");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(scope, "empty.png"));
        Directory.CreateDirectory(Path.Combine(scope, "docs"));
        Directory.CreateDirectory(Path.Combine(root, "scope2"));
        await File.WriteAllTextAsync(Path.Combine(scope, "docs", "unmatched.txt"), "hidden");
        await File.WriteAllTextAsync(Path.Combine(root, "scope2", "outside.png"), "outside");
        for (var index = 0; index < 1703; index++) await File.WriteAllTextAsync(Path.Combine(deep, $"image-{index:D4}.png"), string.Empty);
        try
        {
            Array.Fill(classes, "UltraExplorer_NoEverything_" + Guid.NewGuid().ToString("N"));
            var builder = new SearchMapProjectionBuilder();
            var result = await builder.RunAsync("*.png", [scope, deep], CancellationToken.None);
            Check("the fallback completes without the global panel's 400/800/1500 caps", result.IsComplete && !result.Unsupported
                && result.Projection is { FileCount: 1703 } && result.Source == SearchSource.Walk);
            Check("the last match and every physical parent remain visible", result.Projection?.ContainsFile(deep, "image-1702.png") == true
                && result.Projection.ContainsFolder(Path.Combine(scope, "branch")) && result.Projection.ContainsFolder(scope));
            Check("a directory named .png without matching files, an empty branch and a neighboring scope stay hidden",
                result.Projection?.ContainsFolder(Path.Combine(scope, "empty.png")) == false
                && !result.Projection.ContainsFolder(Path.Combine(scope, "docs"))
                && !result.Projection.ContainsFile(Path.Combine(root, "scope2"), "outside.png"));
            var folders = await builder.RunAsync("folder:*.png", [scope], CancellationToken.None);
            Check("an explicit folder pattern can find an empty matching directory", folders.IsComplete
                && folders.Projection?.ContainsFolder(Path.Combine(scope, "empty.png")) == true);
            var unsupported = await builder.RunAsync("size:>10mb", [scope], CancellationToken.None);
            Check("an unavailable advanced search leaves the map unfiltered with an explicit reason", unsupported.Unsupported
                && unsupported.Projection is null && unsupported.Status.Length > 0);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var stopped = false;
            try { _ = await builder.RunAsync("*.png", [scope], canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check("a canceled scoped search stops instead of publishing an authoritative empty mask", stopped);
        }
        finally { kept.CopyTo(classes, 0); TryDelete(root); }
    }

    private static Task SearchMapViewModelChecksAsync()
    {
        Section("search map: each search context keeps its own mode and clearing restores immediately");
        using var icons = new ShellIconService();
        using var first = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        using var second = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        first.ShowOnMap = true;
        first.Text = "*.png";
        Check("map mode retains the query but closes the global result overlay", first.Text == "*.png"
            && first.IsOpen && first.ShowOnMap && !first.IsResultsPanelOpen);
        Check("another tab's search mode and text are independent", !second.ShowOnMap && second.Text.Length == 0 && !second.IsOpen);
        first.SearchNow();
        Check("Enter in map mode does not start a global capped search", first.Text == "*.png" && !first.IsBusy && first.Results.Count == 0);
        first.Text = string.Empty;
        Check("clearing the box closes search synchronously while keeping the selected mode", first.Text.Length == 0
            && !first.IsOpen && !first.IsResultsPanelOpen && first.ShowOnMap);
        first.Text = "report";
        first.Close();
        Check("Escape/close clears the map query and pending status", first.Text.Length == 0 && !first.IsOpen);
        return Task.CompletedTask;
    }

    private static void SearchMapQueryChecks()
    {
        Section("search map: local matching is explicit rather than silently ignoring advanced syntax");
        foreach (var query in new[] { "report", "\"annual report\"", "*.png", "ext:png;jpg", "file: *report* !*draft*", @"path:""Q:\Photos\"" *.png", "report|invoice", "wfn:\"!report.png\"" })
            Check($"common query can be evaluated locally: {query}", SearchQuery.Parse(query).SupportsLocalMatching);
        foreach (var query in new[] { "size:>10mb", "dm:today", "regex:.*", "<report invoice>|draft", "ext:png|ext:jpg", "\"unfinished" })
            Check($"advanced or unfinished query is never advertised as locally exact: {query}", !SearchQuery.Parse(query).SupportsLocalMatching);

        foreach (var query in new[] { "*.png", "IMG_??.PNG", "ext:jpg;png", "file:report", "*.jpg|*.png" })
            Check($"extension/file query retains folders only as ancestry: {query}", SearchQuery.Parse(query).MapFilesOnly);
        foreach (var query in new[] { "report", "folder:report", "folder:*.png", "!*.png", "*report*", "wfn:\"folder.png\"" })
            Check($"a generic or explicit folder query can keep its own folder hits: {query}", !SearchQuery.Parse(query).MapFilesOnly);
        Check("declaring map file-only does not change ordinary search matching semantics",
            SearchQuery.Parse("*.png").Matches("folder.png", @"Q:\", true));

        var reply = BuildList2(20, EverythingClient.Requested,
            [(0u, "picture.png", @"Q:\photos", 10L, 0L, string.Empty)]);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 15);
        var parsed = EverythingClient.Parse(reply);
        Check("a reply carries its own offset and declared-record completeness", parsed.Offset == 15 && parsed.IsComplete && parsed.Items.Count == 1);
        Check("a truncated page cannot be mistaken for a complete match set", !EverythingClient.Parse(reply.AsSpan(0, 24)).IsComplete);
        Check("an absent page header is incomplete rather than an authoritative empty result", !EverythingClient.Parse([]).IsComplete);
    }

    // A message-only-like, invisible window in this process implements just the
    // documented query/reply protocol. No installed Everything index is changed.
    private static async Task SearchMapPagingChecksAsync()
    {
        Section("search map: Everything offsets reach the actual IPC request and replies stay independent");
        var classes = (string[])typeof(EverythingClient).GetField("WindowClasses", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var kept = (string[])classes.Clone();
        var requests = new List<(uint Offset, uint Maximum, uint Sort, uint Flags, string Text)>();
        using var server = new HwndSource(new HwndSourceParameters("UltraExplorer isolated paged-search fixture") { WindowStyle = 0 });
        server.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0400)
            {
                handled = true;
                return wParam == 401 ? 1 : 0;
            }
            if (message != 0x004A) return 0;
            var copy = Marshal.PtrToStructure<SearchMapCopyData>(lParam);
            if (copy.Data != 18 || copy.Size < 30) return 0;
            var bytes = new byte[checked((int)copy.Size)];
            Marshal.Copy(copy.Pointer, bytes, 0, bytes.Length);
            var replyWindow = (nint)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var replyId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            var maximum = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
            var sort = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            var text = Encoding.Unicode.GetString(bytes, 28, bytes.Length - 30);
            requests.Add((offset, maximum, sort, flags, text));
            var count = offset >= 12 ? 0 : (int)Math.Min(maximum, 12 - offset);
            var items = Enumerable.Range((int)offset, count)
                .Select(index => (0u, $"picture-{index:D2}.png", @"Q:\photos", (long)index,
                    new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), $"*picture*-{index:D2}.png")).ToArray();
            var answer = BuildList2(12, flags, items);
            BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(8), offset);
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => SearchMapSendReply(replyWindow, window, replyId, answer)));
            handled = true;
            return 1;
        });
        var className = new StringBuilder(256);
        SearchMapClassName(server.Handle, className, className.Capacity);
        try
        {
            Array.Fill(classes, className.ToString());
            var pages = new List<EverythingPage>();
            foreach (var offset in new uint[] { 0, 5, 10 })
            {
                var page = await EverythingClient.Shared.QueryPageAsync("*.png", 5, offset, CancellationToken.None, EverythingClient.SortNameAscending);
                Check($"the page at offset {offset} answers through real window-message IPC", page is not null);
                if (page is not null) pages.Add(page);
            }
            Check("three stable pages contain every result exactly once", pages.Count == 3
                && pages.All(page => page.Total == 12 && page.IsComplete)
                && pages.Select(page => page.Offset).SequenceEqual(new uint[] { 0, 5, 10 })
                && pages.SelectMany(page => page.Items).Select(item => item.Name).SequenceEqual(
                    Enumerable.Range(0, 12).Select(index => $"picture-{index:D2}.png")));
            Check("offset, maximum, stable sort, requested columns and raw text are serialized correctly", requests.Count == 3
                && requests.Select(request => request.Offset).SequenceEqual(new uint[] { 0, 5, 10 })
                && requests.All(request => request.Maximum == 5 && request.Sort == EverythingClient.SortNameAscending
                    && request.Flags == EverythingClient.Requested && request.Text == "*.png"));
            var mapPage = await EverythingClient.Shared.QueryMapPageAsync("*.png", 2, 0, CancellationToken.None);
            Check("map queries request only name and path on the actual IPC wire and decode without row metadata",
                requests[^1].Flags == (EverythingClient.RequestName | EverythingClient.RequestPath)
                && mapPage is { IsComplete: true, Items.Count: 2 }
                && mapPage.Items[0] is { Name: "picture-00.png", Directory: @"Q:\photos", Size: -1, Modified: null, Highlighted.Length: 0 });
            var mapped = await new SearchMapProjectionBuilder().RunAsync("*.png", [@"Q:\photos"], CancellationToken.None);
            Check("the production map builder uses minimal columns while retaining every indexed member",
                mapped is { IsComplete: true, Projection.FileCount: 12 }
                && mapped.Projection.ContainsFile(@"Q:\photos", "picture-11.png")
                && requests[^1].Flags == EverythingClient.MapRequested && requests[^1].Offset == 0
                && requests[^1].Maximum == SearchMapProjectionBuilder.PageSize);
            var old = await EverythingClient.Shared.QueryAsync("*.png", 2, CancellationToken.None, EverythingClient.SortNewestFirst);
            Check("ordinary result-panel queries retain offset zero and their selected sort", old?.Items.Count == 2
                && requests[^1].Offset == 0 && requests[^1].Maximum == 2 && requests[^1].Sort == EverythingClient.SortNewestFirst);
            Check("ordinary result-panel replies keep size, modified date and highlighted names after a map request",
                requests[^1].Flags == EverythingClient.Requested && old is { IsComplete: true, Items.Count: 2 }
                && old.Items[1].Size == 1 && old.Items[1].Modified?.ToUniversalTime() == new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
                && old.Items[1].Highlighted == "*picture*-01.png");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var countBeforeCancel = requests.Count;
            var stopped = false;
            try { _ = await EverythingClient.Shared.QueryPageAsync("*.png", 5, 10, canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check("a canceled page sends no new request and leaves the client usable", stopped && requests.Count == countBeforeCancel);
            var after = await EverythingClient.Shared.QueryPageAsync("*.png", 1, 11, CancellationToken.None);
            Check("the next page succeeds after cancellation", after is { Items.Count: 1 } && after.Items[0].Name == "picture-11.png");
        }
        finally { kept.CopyTo(classes, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SearchMapCopyData { public nint Data; public uint Size; public nint Pointer; }

    private static void SearchMapSendReply(nint destination, nint sender, uint id, byte[] bytes)
    {
        var data = Marshal.AllocHGlobal(bytes.Length);
        var copyData = Marshal.AllocHGlobal(Marshal.SizeOf<SearchMapCopyData>());
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            Marshal.StructureToPtr(new SearchMapCopyData { Data = (nint)id, Size = (uint)bytes.Length, Pointer = data }, copyData, false);
            _ = SearchMapSendMessage(destination, 0x004A, sender, copyData, 0x0002, 1000, out _);
        }
        finally { Marshal.FreeHGlobal(copyData); Marshal.FreeHGlobal(data); }
    }

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int SearchMapClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SearchMapSendMessage(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
