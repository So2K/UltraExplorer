using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The nested canvas's side of the window.
///
/// The canvas draws folders; everything a folder can be asked to do - select
/// it, open its menu, copy it, rename it, show its files in the list - still
/// goes through the tree view model, exactly as a click on a tree node does.
/// Clicking a cell selects its path there, and whatever the rest of the window
/// selects (the address bar, the list, back and forward, a search result) is
/// mirrored back onto the canvas and flown to.  So the two pictures are two
/// views of one selection, and every command works in both.
/// </summary>
public partial class MainWindow
{
    private static readonly Color NoteBeaconColour = Color.FromRgb(0xC8, 0xD2, 0xDC);
    private static readonly Color PinBeaconColour = Color.FromRgb(0xFF, 0xD6, 0x6B);
    private static readonly Color ActiveBeaconColour = Color.FromRgb(0x60, 0xCD, 0xFF);
    private static readonly Color SearchBeaconColour = Color.FromRgb(0x4C, 0xC9, 0xD8);
    private const int SearchBeaconLimit = 100;

    private readonly NestedTree _nestedTree = new();
    private readonly List<NestedRoot> _nestedDrives = [];
    private DispatcherTimer? _nestedSaveTimer;
    private DispatcherTimer? _beaconTimer;
    private string[]? _nestedDragPaths;
    private bool _nestedReady;
    private bool _nestedCameraRestored;
    private readonly HashSet<string> _iconsAsked = new(StringComparer.OrdinalIgnoreCase);

    private bool IsNested => _viewModel.IsNestedLayout;

    /// <summary>The canvas that is showing, for keyboard focus.</summary>
    private void FocusCanvas()
    {
        if (IsNested)
        {
            Nested.Focus();
        }
        else
        {
            Editor.Focus();
        }
    }

    private void AttachNested()
    {
        Nested.Tree = _nestedTree;
        Nested.MarkLookup = _viewModel.Marks.Get;
        Nested.IconLookup = LookUpFileIcon;
        Nested.SelectRequested += OnNestedSelectRequested;
        Nested.OpenRequested += OnNestedOpenRequested;
        Nested.ContextMenuRequested += OnNestedContextMenuRequested;
        Nested.DragRequested += OnNestedDragRequested;
        Nested.CameraChanged += OnNestedCameraChanged;

        _nestedSaveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(400) };
        _nestedSaveTimer.Tick += (_, _) =>
        {
            _nestedSaveTimer.Stop();
            _viewModel.Tree.NestedCamera = Nested.CaptureCamera() ?? _viewModel.Tree.NestedCamera;
            _viewModel.Tree.ScheduleSave();
        };

        _beaconTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _beaconTimer.Tick += (_, _) =>
        {
            _beaconTimer.Stop();
            RebuildBeacons();
        };

        _viewModel.PropertyChanged += OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged += OnTreePropertyChangedForNested;
        _viewModel.Tree.SelectedNodes.CollectionChanged += OnTreeSelectionChangedForNested;
        _viewModel.Tree.PathRefreshed += OnTreePathRefreshed;
        _viewModel.QuickAccess.CollectionChanged += OnBeaconSourceChanged;
        _viewModel.SearchResults.CollectionChanged += OnBeaconSourceChanged;
        _viewModel.Marks.MarkChanged += OnMarkChangedForNested;
    }

    private void DetachNested()
    {
        _nestedSaveTimer?.Stop();
        _beaconTimer?.Stop();
        _viewModel.PropertyChanged -= OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged -= OnTreePropertyChangedForNested;
        _viewModel.Tree.SelectedNodes.CollectionChanged -= OnTreeSelectionChangedForNested;
        _viewModel.Tree.PathRefreshed -= OnTreePathRefreshed;
        _viewModel.QuickAccess.CollectionChanged -= OnBeaconSourceChanged;
        _viewModel.SearchResults.CollectionChanged -= OnBeaconSourceChanged;
        _viewModel.Marks.MarkChanged -= OnMarkChangedForNested;
        _nestedTree.Dispose();
    }

    /// <summary>
    /// Runs once the shell has loaded its state: the drives go in as the first
    /// row of cells, the hidden-folder rules are copied over, and the camera is
    /// put back where the last session left it.
    /// </summary>
    private Task InitializeNestedAsync()
    {
        // A file dialog always shows the tree; it never needs this canvas.
        if (IsPickerMode)
        {
            return Task.CompletedTask;
        }

        // The drives the tree already found, not a second scan of them: a
        // drive that is slow to answer would otherwise hold up startup twice.
        _nestedDrives.Clear();
        _nestedDrives.AddRange(_viewModel.Tree.Roots
            .Where(root => root.IsDrive)
            .Select(root => new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive, root.SecondaryText)));

        SyncNestedRoots();
        _nestedTree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
        _nestedTree.SetUserHidden(_viewModel.Tree.HiddenPaths);
        SyncNestedSelection();
        _nestedReady = true;
        _viewModel.NestedZoomLabel = Nested.ZoomText;
        if (IsNested)
        {
            EnterNested(fromStartup: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The nested canvas coming into view, at startup or from the tree.  Its
    /// marks are gathered only now - while it is hidden it reads nothing - and
    /// the camera goes back to where it was, or to the selection.  Deferred to
    /// after layout, so the view is framed for the size it really has.
    /// </summary>
    private void EnterNested(bool fromStartup)
    {
        if (!_nestedReady)
        {
            return;
        }

        SyncNestedSelection();
        RebuildBeacons();
        Dispatcher.InvokeAsync(() =>
        {
            Nested.UpdateLayout();
            if (!_nestedCameraRestored && _viewModel.Tree.RestoredNestedCamera is { } camera)
            {
                // Not awaited: the window is usable while the folders on the
                // way are read, and the view jumps there once they have been.
                _nestedCameraRestored = true;
                _ = Nested.RestoreCameraAsync(camera);
            }
            else if (!string.IsNullOrEmpty(_viewModel.Tree.ActivePath))
            {
                _ = FlyNestedToAsync(_viewModel.Tree.ActivePath, gentle: !fromStartup, animated: false);
            }

            FocusCanvas();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Drives, plus every share and WSL distribution the tree has as a root of its own.</summary>
    private void SyncNestedRoots()
    {
        var roots = new List<NestedRoot>(_nestedDrives);
        foreach (var root in _viewModel.Tree.Roots.Where(root => !root.IsDrive))
        {
            if (roots.All(existing => !ViewAllPath.Equals(existing.FullPath, root.FullPath)))
            {
                roots.Add(new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive));
            }
        }

        _nestedTree.SetRoots(roots);
    }

    // ---- selection, both ways ---------------------------------------------

    private async void OnNestedSelectRequested(string path, bool additive)
    {
        try
        {
            if (additive)
            {
                await _viewModel.Tree.ToggleSelectionAsync(path);
            }
            else
            {
                await _viewModel.Tree.SelectPathAsync(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.Toast.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// A double-click on a folder has already flown into it.  A file opens -
    /// through the tree, so a file dialog's own "this is the answer" still
    /// applies.  A link has nothing inside it to fly into, so it goes where it
    /// points instead.
    /// </summary>
    private async void OnNestedOpenRequested(NestedHit hit)
    {
        if (hit.IsFile)
        {
            try
            {
                if (await _viewModel.Tree.SelectPathAsync(hit.Path) is { } node)
                {
                    await _viewModel.Tree.ToggleAsync(node);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _viewModel.Toast.ShowError(ex.Message);
            }

            return;
        }

        var folder = hit.Folder;
        if (!folder.IsReparsePoint)
        {
            return;
        }

        try
        {
            var target = Directory.ResolveLinkTarget(folder.FullPath, returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                await _viewModel.Tree.RevealPathAsync(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.Toast.ShowError($"Could not follow {folder.Name}: {ex.Message}");
        }
    }

    private void OnTreeSelectionChangedForNested(object? sender, NotifyCollectionChangedEventArgs e) => SyncNestedSelection();

    private void OnTreePropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewAllViewModel.ActivePath):
                SyncNestedSelection();
                ScheduleBeacons();
                break;
            case nameof(ViewAllViewModel.HiddenPaths):
                _nestedTree.SetUserHidden(_viewModel.Tree.HiddenPaths);
                break;
            case nameof(ViewAllViewModel.ShowHiddenItems):
                _nestedTree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
                break;
        }
    }

    private void OnShellPropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsNestedLayout):
                // Switching pictures keeps the place: the other canvas opens on
                // whatever is selected.
                if (IsNested)
                {
                    EnterNested(fromStartup: false);
                }
                else
                {
                    if (_viewModel.Tree.ActiveNode is { } node)
                    {
                        FocusNode(node, animated: false);
                    }

                    Dispatcher.InvokeAsync(FocusCanvas, DispatcherPriority.Input);
                }

                break;
            case nameof(MainViewModel.IsSearchOpen):
                ScheduleBeacons();
                break;
        }
    }

    private void SyncNestedSelection() =>
        Nested.SetSelection(_viewModel.Tree.SelectedPaths, _viewModel.Tree.ActivePath);

    /// <summary>
    /// The nested version of "bring this node into view".  Navigation - the
    /// address bar, the sidebar, back and forward - flies to the folder.  A row
    /// picked in the list only needs to be visible, and usually already is.
    /// </summary>
    private async Task FlyNestedToAsync(string path, bool gentle, bool animated = true)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var folderPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var folder = await _nestedTree.RevealAsync(folderPath);
        if (folder is null)
        {
            // A share or a WSL distribution the tree has only just added.
            SyncNestedRoots();
            folder = await _nestedTree.RevealAsync(folderPath);
            if (folder is null)
            {
                return;
            }
        }

        var view = new Rect(0, 0, Nested.ActualWidth, Nested.ActualHeight);
        if (Nested.ScreenRectOf(folder) is { } rect)
        {
            if (gentle && rect.Width >= 24 && view.IntersectsWith(rect))
            {
                return;
            }

            if (!gentle && view.Contains(rect) && rect.Width >= view.Width * 0.45)
            {
                return;
            }
        }

        if (gentle && folder.Parent is { IsComputer: false } parent)
        {
            Nested.FlyTo(parent, 0.92, animated);
        }
        else
        {
            Nested.FlyTo(folder, 0.8, animated);
        }
    }

    private void OnNestedCameraChanged()
    {
        _viewModel.NestedZoomLabel = Nested.ZoomText;
        _nestedSaveTimer?.Stop();
        _nestedSaveTimer?.Start();
    }

    private void CaptureNestedCamera()
    {
        if (Nested.CaptureCamera() is { } camera)
        {
            _viewModel.Tree.NestedCamera = camera;
        }
    }

    private async void OnTreePathRefreshed(string path)
    {
        if (_nestedTree.Find(path) is { } folder)
        {
            try
            {
                await _nestedTree.RefreshAsync(folder);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException)
            {
                // The window is closing, or the folder went; nothing to redraw.
            }
        }
    }

    // ---- beacons -------------------------------------------------------------

    private void OnBeaconSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleBeacons();

    private void OnMarkChangedForNested(string path, FolderMark mark)
    {
        // Raised on whichever thread set the mark.
        Dispatcher.InvokeAsync(() =>
        {
            Nested.InvalidateMarks();
            ScheduleBeacons();
        });
    }

    private void ScheduleBeacons()
    {
        // Hidden, the canvas has no use for marks, and gathering them would
        // read every folder on the way to each one for nothing.
        if (!IsNested || !_nestedReady)
        {
            return;
        }

        _beaconTimer?.Stop();
        _beaconTimer?.Start();
    }

    /// <summary>
    /// Everything the user has put on a folder, gathered into one list of
    /// beacons: colours and notes from the mark store, folders pinned to Home,
    /// the folder that is selected, and while a search is open, what it found.
    /// A mark on a file shows on the folder it is in.
    /// </summary>
    private void RebuildBeacons()
    {
        var beacons = new Dictionary<string, (NestedBeaconKind Kind, Color Colour, string Label, string Note)>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, NestedBeaconKind kind, Color? colour, string label, string note = "")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (beacons.TryGetValue(path, out var existing))
            {
                beacons[path] = (
                    existing.Kind | kind,
                    existing.Kind.HasFlag(NestedBeaconKind.Colour) ? existing.Colour : colour ?? existing.Colour,
                    existing.Label,
                    existing.Note.Length > 0 ? existing.Note : note);
                return;
            }

            beacons[path] = (kind, colour ?? NoteBeaconColour, label, note);
        }

        foreach (var (path, mark) in _viewModel.Marks.Snapshot())
        {
            var kind = NestedBeaconKind.None;
            Color? colour = null;
            if (!string.IsNullOrEmpty(mark.AccentHex) && TryParse(mark.AccentHex, out var parsed))
            {
                kind |= NestedBeaconKind.Colour;
                colour = parsed;
            }

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                kind |= NestedBeaconKind.Note;
            }

            if (kind != NestedBeaconKind.None)
            {
                Add(path, kind, colour, LeafName(path), mark.Note);
            }
        }

        foreach (var pinned in _viewModel.QuickAccess.Where(item => item.IsCustom))
        {
            Add(pinned.Path, NestedBeaconKind.Pinned, PinBeaconColour, pinned.Name);
        }

        if (_viewModel.IsSearchOpen)
        {
            foreach (var result in _viewModel.SearchResults.Take(SearchBeaconLimit))
            {
                Add(result.FullPath, NestedBeaconKind.Search, SearchBeaconColour, result.Name);
            }
        }

        if (_viewModel.Tree.ActiveNode is { } active)
        {
            Add(active.FullPath, NestedBeaconKind.Active, ActiveBeaconColour, active.DisplayName);
        }

        Nested.SetBeacons([.. beacons.Select(pair => new NestedBeacon(pair.Key, pair.Value.Kind, pair.Value.Colour, pair.Value.Label, pair.Value.Note))]);
    }

    private static string LeafName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool TryParse(string hex, out Color colour)
    {
        colour = default;
        if (!NestedCanvas.IsHexColour(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- menus -----------------------------------------------------------------

    /// <summary>
    /// Right-click on a folder's title is the folder's own Windows menu.
    /// Right-click in the open space of a big folder is "in" that folder: new
    /// folder, new file, paste.  Outside every cell is the canvas's own menu.
    /// </summary>
    private async void OnNestedContextMenuRequested(NestedHit? hit, bool onBackground, Point point)
    {
        var tree = _viewModel.Tree;
        try
        {
            if (hit is not { } target)
            {
                ShowFolderAreaMenu(Nested, null);
                return;
            }

            if (onBackground)
            {
                var area = await tree.MaterializeAsync(target.Folder.FullPath);
                ShowFolderAreaMenu(Nested, area);
                return;
            }

            var path = target.Path;
            if (tree.SelectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                && tree.TryGetNode(path, out var selected))
            {
                tree.Activate(selected);
            }
            else if (await tree.SelectPathAsync(path) is null)
            {
                return;
            }

            _viewModel.ShowContextMenuFor(tree.SelectedPaths, Nested, point);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.Toast.ShowError(ex.Message);
        }
    }

    // ---- drag and drop ---------------------------------------------------------

    /// <summary>A folder or file picked up: the selection if it is part of one, otherwise just it.</summary>
    private void OnNestedDragRequested(string path)
    {
        if (IsPickerMode)
        {
            return;
        }

        var selected = _viewModel.Tree.SelectedPaths;
        string[] paths = selected.Count > 1 && selected.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? [.. selected]
            : [path];

        _nestedDragPaths = paths;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(Nested, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _nestedDragPaths = null;
            Nested.DropTarget = null;
        }

        // Moved somewhere else - into Explorer, onto the desktop - the items
        // are gone from where they were, and nothing else will say so.
        // Explorer often finishes a move after the drop has returned, so the
        // folders are looked at again now and once more a little later.
        _ = RefreshSourcesAsync(paths);
    }

    private async Task RefreshSourcesAsync(IReadOnlyList<string> paths)
    {
        var parents = paths
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrEmpty(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var delay in new[] { 300, 1500 })
        {
            await Task.Delay(delay);
            foreach (var parent in parents)
            {
                await _viewModel.Tree.RefreshPathAsync(parent!);
            }
        }
    }

    private void Nested_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetDropPaths(e.Data, out var paths) || ResolveNestedDropTarget(e, paths) is not { } target)
        {
            Nested.DropTarget = null;
            e.Effects = DragDropEffects.None;
            return;
        }

        Nested.DropTarget = target;
        e.Effects = IsCopyOperation(paths, target.FullPath) ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    private void Nested_DragLeave(object sender, DragEventArgs e)
    {
        if (!Nested.IsMouseOver)
        {
            Nested.DropTarget = null;
        }
    }

    private async void Nested_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Nested.DropTarget = null;
        if (!TryGetDropPaths(e.Data, out var paths) || ResolveNestedDropTarget(e, paths) is not { } target)
        {
            return;
        }

        await _viewModel.DropIntoPathAsync(paths, target.FullPath, Keyboard.Modifiers);
    }

    /// <summary>The innermost folder under the pointer, unless it is one of the things being dropped or inside one.</summary>
    private NestedFolder? ResolveNestedDropTarget(DragEventArgs e, IReadOnlyList<string> paths)
    {
        if (Nested.HitTest(e.GetPosition(Nested)) is not { } hit || hit.Folder.IsComputer)
        {
            return null;
        }

        var target = hit.Folder;
        return paths.Any(path =>
            ViewAllPath.Equals(path, target.FullPath)
            || NativeShellService.IsInvalidMoveTarget(path, target.FullPath)
            || ViewAllPath.Equals(Path.GetDirectoryName(path) ?? string.Empty, target.FullPath) && _nestedDragPaths is not null)
            ? null
            : target;
    }

    /// <summary>
    /// A file's icon for the canvas: whatever the icon service already has, and
    /// a request for the rest that repaints the canvas when it arrives.  Icons
    /// are per extension, so a folder of a thousand photos asks once.
    /// </summary>
    private ImageSource? LookUpFileIcon(string path)
    {
        var icons = _viewModel.Icons;
        var icon = icons.GetCached(path, isDirectory: false);
        if (icon is not null)
        {
            return icon;
        }

        var extension = Path.GetExtension(path);
        var key = extension is ".exe" or ".lnk" or ".ico" || string.IsNullOrEmpty(extension) ? path : extension;
        if (_iconsAsked.Count < 20_000 && _iconsAsked.Add(key))
        {
            icons.Request(path, isDirectory: false, _ => Nested.RefreshIcons());
        }

        return null;
    }

    // ---- keyboard --------------------------------------------------------------

    /// <summary>The keys the nested canvas answers itself: the arrows, Enter and Backspace.</summary>
    private bool TryHandleNestedKey(Key key, ModifierKeys modifiers)
    {
        if (!IsNested || !Nested.IsKeyboardFocusWithin)
        {
            return false;
        }

        return Nested.HandleKey(key, modifiers);
    }
}
