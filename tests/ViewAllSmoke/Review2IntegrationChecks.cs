using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task Review2IntegrationChecks()
    {
        if (_only is not [var alone] || alone != nameof(Review2IntegrationChecks))
        {
            RunGroupInOwnProcess(nameof(Review2IntegrationChecks));
            return Task.CompletedTask;
        }
        RunOnSta("review 2 integration", Review2IntegrationOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task Review2IntegrationOnStaAsync()
    {
        Section("review 2 cross-file integration");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerReview2Integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            BreadcrumbPublicationCheck();
            IndexedEdgeCheck();
            OwnRenamePairCheck(root);
            await LiteralPruneCheck(root);
            await IncomingGraphIndexCheck(root);
            await FocusOnlyLiveNodeCheck(root);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
            using (ActivationGuard.GuardWindowsCreated())
            {
                await PickerRenameBridgeCheck(root);
                await StaleListSelectionCheck(root);
                await ExplicitHideBridgeCheck(root);
                OwnerOnlyPromptCheck();
            }
        }
        finally { TryDelete(root); }
    }

    private static void BreadcrumbPublicationCheck()
    {
        var segment = new BreadcrumbSegment("root", @"Q:\root", true);
        var old = segment.Children;
        var notifications = 0;
        var oldAdds = 0;
        old.CollectionChanged += (_, _) => oldAdds++;
        segment.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BreadcrumbSegment.Children)) notifications++; };
        var children = new ObservableCollection<AddressSuggestion>(Enumerable.Range(0, 16_000)
            .Select(i => new AddressSuggestion($"child-{i}", $@"Q:\root\child-{i}", AddressSuggestionKind.Folder)));
        segment.ReplaceChildren(children);
        Check("a 16k-child crumb publishes one collection property change, not 16k bound Adds",
            notifications == 1 && oldAdds == 0 && segment.Children.Count == 16_000 && ReferenceEquals(segment.Children, children));
        segment.ReplaceChildren(children);
        Check("republishing the identical crumb collection changes nothing", notifications == 1);
        segment.ReplaceChildren([]);
        Check("an empty crumb replaces the old contents and retains its empty-state message", segment.Children.Count == 0 && segment.EmptyText.Length > 0);
    }

    private static void IndexedEdgeCheck()
    {
        var parent = new ViewAllNodeViewModel(new(@"Q:\view", "view", ViewAllEntryKind.Folder, false, false, null, DateTime.UnixEpoch), 0)
            { Location = new Point(0, 0) };
        var nodes = new List<ViewAllNodeViewModel> { parent };
        var edges = new List<ViewAllEdgeViewModel>();
        var incoming = new Dictionary<Guid, ViewAllEdgeViewModel>();
        var index = new ViewAllSpatialIndex();
        index.AddOrUpdate(parent);
        try
        {
            for (var i = 0; i < 20_000; i++)
            {
                var child = new ViewAllNodeViewModel(new($@"Q:\view\item-{i}", $"item-{i}", ViewAllEntryKind.File, false, false, 1, DateTime.UnixEpoch), 1, parent)
                    { Location = new Point(280 + i * 3000, 0) };
                nodes.Add(child);
                var edge = new ViewAllEdgeViewModel(parent, child);
                edges.Add(edge);
                incoming[child.Id] = edge;
                index.AddOrUpdate(child);
            }
            var viewport = new ViewAllViewportService(new(OverscanPixels: 0));
            var counted = new IntegrationCountingEdges(edges);
            var area = new Rect(-10, -10, 1200, 500);
            var set = viewport.BuildRenderSet(index, counted, [nodes[^1]], area, 1, nodes.Count, incoming);
            var visible = set.Nodes.ToHashSet();
            var expected = edges.Where(e => e.IsTreeVisible && visible.Contains(e.Source) && visible.Contains(e.Target)).ToArray();
            Check("an indexed sparse viewport never walks the 20k-edge list", counted.Enumerations == 0 && counted.IndexReads == 0);
            Check("indexed culling preserves the original edge references and paint order", set.Edges.SequenceEqual(expected) && set.Edges.Count == 2);
            edges[0].IsTreeVisible = false;
            set = viewport.BuildRenderSet(index, counted, [nodes[^1]], area, 1, nodes.Count, incoming);
            Check("a hidden edge is not revived by the incoming index", set.Edges.Count == 1 && ReferenceEquals(set.Edges[0], edges[^1]));
            incoming.Remove(nodes[^1].Id);
            set = viewport.BuildRenderSet(index, counted, [nodes[^1]], area, 1, nodes.Count, incoming);
            Check("an edge removed from the incoming index is no longer drawn", set.Edges.Count == 0);
        }
        finally { foreach (var edge in edges) edge.Dispose(); }
    }

    private sealed class IntegrationCountingEdges(IReadOnlyList<ViewAllEdgeViewModel> source) : IReadOnlyList<ViewAllEdgeViewModel>
    {
        public int Enumerations;
        public int IndexReads;
        public int Count => source.Count;
        public ViewAllEdgeViewModel this[int index] { get { IndexReads++; return source[index]; } }
        public IEnumerator<ViewAllEdgeViewModel> GetEnumerator() { Enumerations++; return source.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void OwnRenamePairCheck(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "rename-pairs"));
        using var lease = hub;
        var directory = Path.Combine(watch.Key, "A");
        var owner = new object();
        hub.Register(ChangeConsumer.Nested, directory, owner);
        hub.TouchRename(Path.Combine(directory, "old"), Path.Combine(directory, "new"));
        time.Advance(1);
        DrainHub(hub, sink);
        Check("an immediate app touch already carries its rename pair, before any watcher record",
            sink.Changes.Count == 1 && sink.Changes[0].Change.Renames.Span.SequenceEqual(new RenamePair[] { new("old", "new") }));
        sink.Clear();
        hub.TouchRename(Path.Combine(directory, "A"), Path.Combine(directory, "B"));
        hub.TouchRename(Path.Combine(directory, "B"), Path.Combine(directory, "A"));
        hub.TouchRename(Path.Combine(directory, "A"), Path.Combine(directory, "B"));
        time.Advance(1);
        DrainHub(hub, sink);
        Check("an A-B-A-B rename cycle retains its third operation", sink.Changes.Count == 1 && sink.Changes[0].Change.Renames.Length == 3);
        sink.Clear();
        hub.TouchRename(Path.Combine(directory, "Case"), Path.Combine(directory, "case"));
        Feed(hub, watch, (4, @"A\Case"), (5, @"A\case"));
        time.Advance(1);
        DrainHub(hub, sink);
        Check("a pending case-only app rename and its identical watcher echo are one pair",
            sink.Changes.Count == 1 && sink.Changes[0].Change.Renames.Length == 1);
    }

    private static async Task IncomingGraphIndexCheck(string root)
    {
        var folder = Path.Combine(root, "edge-index");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.txt"), "one");
        using var graph = new ViewAllGraphService();
        var node = (await graph.AddRootAsync(folder))!;
        await graph.ExpandAsync(node);
        Check("the graph's incoming index references every currently owned edge",
            graph.Edges.Count > 0 && graph.Edges.All(e => graph.IncomingEdges.TryGetValue(e.Target.Id, out var found) && ReferenceEquals(e, found)));
        File.WriteAllText(Path.Combine(folder, "two.txt"), "two");
        await graph.RefreshBranchAsync(node);
        Check("refresh keeps no removed edges in the incoming index",
            graph.IncomingEdges.Count == graph.Edges.Count && graph.IncomingEdges.Values.All(graph.Edges.Contains));
    }

    private static async Task FocusOnlyLiveNodeCheck(string root)
    {
        var folder = Path.Combine(root, "focus-only");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(0, 4).Select(i => Path.Combine(folder, $"file-{i}.txt")).ToArray();
        foreach (var file in files) File.WriteAllText(file, "owned");
        using var icons = new ShellIconService();
        using var tree = NewTree(root, icons);
        await tree.AddRootAsync(folder);
        var live = (await tree.RevealAsync(files[0], focus: false, select: false)).Node!;
        tree.Selection.Apply(new SelectionEdit
        {
            Clear = true, Container = folder, Added = [.. files.Select(path => new SelectionItem(path, false, 5))],
            Focus = files[0], Anchor = files[0], Source = SelectionSource.Canvas
        });
        await Until(() => ViewAllPath.Equals(tree.ActivePath, files[0]), 5000);
        // Deterministically model a by-name object replaced during expansion:
        // the selected path is unchanged, so no selection event will repair it.
        var detachedParent = new ViewAllNodeViewModel(live.Parent!.Entry, live.Parent.Depth);
        var detached = new ViewAllNodeViewModel(live.Entry, live.Depth, detachedParent);
        typeof(ViewAllViewModel).GetProperty(nameof(ViewAllViewModel.ActiveNode))!.SetValue(tree, detached);
        var navigation = tree.BeginNavigation();
        var outcome = await tree.RevealAsync(files[0], focus: true, select: false);
        Check("a focus-only reveal replaces a stale by-name active reference with the live expanded graph node",
            ReferenceEquals(tree.ActiveNode, outcome.Node) && tree.ActiveNode?.Parent?.IsExpanded == true);
        Check("repairing that active reference preserves four selected files and the reserved navigation ticket",
            tree.Selection.Count == 4 && files.All(tree.Selection.Contains) && tree.IsLatestNavigation(navigation));
    }

    private static async Task LiteralPruneCheck(string root)
    {
        var ordinary = Path.Combine(root, "twin");
        var literal = ordinary + ".";
        Directory.CreateDirectory(ordinary);
        Directory.CreateDirectory(ViewAllFileSystemService.ForWindows(literal));
        var file = Path.Combine(literal, "literal.txt");
        File.WriteAllText(ViewAllFileSystemService.ForWindows(file), "literal");
        using var icons = new ShellIconService();
        using var tree = NewTree(root, icons);
        tree.IsCanvasShown = false;
        tree.Selection.Apply(new SelectionEdit { Added = [new(file, false, 7)], Container = literal, Source = SelectionSource.Command });
        await (Task)typeof(ViewAllViewModel).GetMethod("PruneSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tree, [literal])!;
        Check("pruning a trailing-dot folder retains its actual selected file rather than listing its twin", tree.Selection.Contains(file));
        var gone = (Task<bool>)typeof(ViewAllViewModel).GetMethod("IsGoneAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [file])!;
        Check("the literal trailing-dot file is not declared gone", !await gone);
    }

    private static async Task PickerRenameBridgeCheck(string root)
    {
        var oldPath = Path.Combine(root, "Proj");
        var newPath = Path.Combine(root, "Proj2");
        Directory.CreateDirectory(oldPath);
        var session = new FileDialogSession(new FileDialogRequest { Mode = FileDialogMode.Save, InitialFolder = oldPath });
        var window = new MainWindow(session) { ShowActivated = false };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var model = (MainViewModel)window.DataContext;
        try
        {
            session.CurrentFolder = oldPath;
            session.FileNameText = "report.exr";
            model.Tree.NoteOwnRename(oldPath, newPath);
            Check("an explicit rename updates a picker with no selection and preserves its typed Save name",
                session.CurrentFolder == newPath && session.FileNameText == "report.exr");
            session.CurrentFolder = Path.Combine(oldPath, "inside");
            model.Tree.NoteOwnRename(oldPath, newPath);
            Check("the picker follows an ancestor rename on a separator boundary", session.CurrentFolder == Path.Combine(newPath, "inside"));
            session.CurrentFolder = oldPath + "-other";
            model.Tree.NoteOwnRename(oldPath, newPath);
            Check("a similarly prefixed sibling folder does not follow the rename", session.CurrentFolder == oldPath + "-other");
            session.CurrentFolder = oldPath;
            var changedCase = Path.Combine(root, "proj");
            model.Tree.NoteOwnRename(oldPath, changedCase);
            Check("a picker also keeps the new spelling of a case-only rename", session.CurrentFolder == changedCase);
        }
        finally
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        var handlers = (Delegate?)typeof(ViewAllViewModel).GetField("RenameFollowed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model.Tree);
        Check("closing the picker removes only its rename subscription", handlers?.GetInvocationList().All(h => !ReferenceEquals(h.Target, window)) ?? true);
    }

    private static async Task StaleListSelectionCheck(string root)
    {
        var before = Path.Combine(root, "list-before");
        var next = Path.Combine(root, "list-next");
        Directory.CreateDirectory(before);
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(before, "old.txt"), "owned");
        var window = ProxyWindow(out var model);
        var list = model.Tree.FolderList;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var gate = new TaskCompletionSource<ViewAllDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            list.IsVisible = true;
            await list.NavigateAsync(before, moveCanvas: false);
            var old = list.Items.Single();
            model.Tree.Selection.ReplaceSingle(old.FullPath, false, 5, SelectionSource.Command);
            var field = typeof(FolderListViewModel).GetField("_read", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = (Func<string, ItemSort, CancellationToken, Task<ViewAllDirectorySnapshot>>)field.GetValue(list)!;
            field.SetValue(list, (Func<string, ItemSort, CancellationToken, Task<ViewAllDirectorySnapshot>>)((path, sort, token) =>
                ViewAllPath.Equals(path, next) ? gate.Task : original(path, sort, token)));
            var going = list.NavigateAsync(next, moveCanvas: false);
            Check("old rows kept for the brief loading grace period cannot claim the new folder", !list.HasCurrentRows && !list.IsCurrentRow(old));
            typeof(MainWindow).GetField("_listUserInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var changed = new System.Windows.Controls.SelectionChangedEventArgs(
                System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new ArrayList(), new ArrayList { old });
            typeof(MainWindow).GetMethod("FolderListItems_SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, changed]);
            Check("a real list selection callback ignores stale rows rather than replacing the shared selection",
                model.Tree.Selection.Count == 1 && model.Tree.Selection.Contains(old.FullPath) && list.SelectedRows().Count == 0);
            gate.TrySetResult(new ViewAllDirectorySnapshot([], false, 0));
            await going;
            Check("publication of the new folder restores the row interaction fence", list.HasCurrentRows);
        }
        finally
        {
            gate.TrySetResult(new ViewAllDirectorySnapshot([], false, 0));
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task ExplicitHideBridgeCheck(string root)
    {
        var folder = Path.Combine(root, "explicit-hide");
        var child = Path.Combine(folder, "inside");
        Directory.CreateDirectory(child);
        var window = ProxyWindow(out var model);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await window.StartNestedForChecksAsync();
            await model.Tree.AddRootAsync(folder);
            model.IsSplit = true;
            var panes = window.Panes.ToArray();
            foreach (var pane in panes)
            {
                // Drives themselves are intentionally not hidden. Exercise a
                // normal folder below the owned root, as the real command does.
                pane.Tree.SetRoots([new NestedRoot(root, "owned fixture", NestedFolderKind.Drive)]);
                pane.Tree.SetUserHidden([folder]);
                await pane.Tree.MaterializePathAsync(child);
            }
            Check("both panes have a named path reopened below the already-hidden folder",
                panes.Length == 2 && panes.All(pane => pane.Tree.Find(child) is { } node && NestedTree.IsOnCanvas(node)));
            model.Tree.Selection.ReplaceSingle(folder, true, 0, SelectionSource.Canvas);
            await model.Tree.HideSelectedAsync();
            Check($"the actual Hide command carries its resolved snapshot after clearing selection to both panes (selected={model.Tree.Selection.Count}; "
                + string.Join("; ", panes.Select(pane => $"pane{pane.Index}: child={pane.Tree.Find(child) is not null}, hidden={pane.Tree.IsUserHidden(folder)}, visible={pane.Tree.Find(child) is { } found && NestedTree.IsOnCanvas(found)}")) + ")",
                model.Tree.Selection.Count == 0 && panes.All(pane => pane.Tree.Find(child) is { } node && !NestedTree.IsOnCanvas(node)));
        }
        finally
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        var handlers = (Delegate?)typeof(ViewAllViewModel).GetField("FoldersHiddenByUser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model.Tree);
        Check("closing a window removes its explicit-hide pane bridge", handlers?.GetInvocationList().All(h => !ReferenceEquals(h.Target, window)) ?? true);
    }

    private static void OwnerOnlyPromptCheck()
    {
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  note: owner-only prompt window checks skipped: no secondary test monitor");
            return;
        }
        var owner = new Window { ShowActivated = false, Width = 100, Height = 100 };
        var peer = new Window { ShowActivated = false, Width = 100, Height = 100 };
        void Prepare(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                DialogNative.CloakOwn(handle, true);
                DialogNative.Place(handle, new NativeRect { Left = target.Work.Left + 40, Top = target.Work.Top + 40, Right = target.Work.Left + 240, Bottom = target.Work.Top + 200 });
            };
        }
        Prepare(owner);
        Prepare(peer);
        owner.Show();
        peer.Show();
        try
        {
            var dialog = new InputDialog("test", "owned fixture", "value") { Owner = owner, ShowActivated = false };
            Prepare(dialog);
            var onlyOwner = false;
            dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                onlyOwner = !owner.IsEnabled && peer.IsEnabled;
                typeof(InputDialog).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [true]);
            });
            var accepted = dialog.ShowOwnerModal();
            Check("an input prompt disables only its owner, keeps its accepted result and restores that owner", onlyOwner && accepted && owner.IsEnabled && peer.IsEnabled);
            var confirm = new ConfirmDialog("test", "owned fixture") { Owner = owner, ShowActivated = false };
            Prepare(confirm);
            confirm.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                typeof(ConfirmDialog).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(confirm, [false]));
            Check("cancel remains the non-destructive confirmation default", !confirm.ShowOwnerModal() && owner.IsEnabled);
        }
        finally { peer.Close(); owner.Close(); }
    }
}
