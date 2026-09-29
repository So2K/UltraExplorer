using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The split view: two panes of the nested canvas side by side or one above
/// the other, each at its own place on the disk, like the two panes of
/// Houdini's network editor.  Ctrl+\, the command bar's split button, Canvas
/// options or Settings turn it on and off; Ctrl+Shift+\ or the button's
/// arrow chooses how the panes are laid out; the divider between them sets
/// their shares.
///
/// <para>The second pane is made only when the view is split, and let go of
/// whole when it no longer is: a window that is not split has one pane,
/// exactly the canvas it always had.  The first pane stays: closing the
/// split closes the second, and its place is kept for the next split.</para>
///
/// <para>One pane is being worked with at a time (<see cref="ActivePane"/>):
/// the one last clicked or given the keyboard, or F6's.  It has the accent
/// along the top of its header, and the window's selection, Back and
/// Forward, the address bar, the status bar, the list, the zoom buttons and
/// every command are its (see <see cref="ActivatePane"/>).</para>
///
/// <para>Things go from one pane to the other by a drag onto a folder of
/// the other, by Copy and Move to other pane (Shift+F5, Shift+F6) into the
/// other pane's folder, and a folder goes to the other pane by Open in other
/// pane.</para>
///
/// <para>The tree canvas and a file dialog are never split: the tree has no
/// panes, and a file dialog never makes a second.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>How thick the divider between the panes is: enough to take hold of.</summary>
    private const double PaneSplitterSize = 5;

    private static readonly Geometry SideBySideGlyph = FrozenGeometry("M0.5,0.5 H15.5 V13.5 H0.5 Z M8.5,0.5 V13.5");
    private static readonly Geometry StackedGlyph = FrozenGeometry("M0.5,0.5 H15.5 V13.5 H0.5 Z M0.5,7.5 H15.5");

    private GridSplitter? _paneSplitter;

    /// <summary>What the second pane had selected when the split was last closed, for the next split of this session.</summary>
    private (SelectionItem[] Items, string? Focus)? _closedSecondSelection;

    /// <summary>The second pane of a split view, or null while the view has one pane.</summary>
    internal NestedPane? SecondPane => _panes.Count > 1 ? _panes[1] : null;

    /// <summary>The pane of a split view that is not being worked with, or null while there is one pane.</summary>
    internal NestedPane? InactivePane => _panes.Count > 1 ? _panes.FirstOrDefault(pane => !ReferenceEquals(pane, ActivePane)) : null;

    /// <summary>For the checks: the divider between the panes while the view is split.</summary>
    internal GridSplitter? PaneSplitter => SecondPane is null ? null : _paneSplitter;

    /// <summary>
    /// For the checks: what the window does once its state has been read -
    /// every pane given its drives, the split the workspace left put back,
    /// the pane being worked with entered - with no window on screen.
    /// </summary>
    internal Task StartNestedForChecksAsync() => InitializeNestedAsync();

    private void AttachSplit()
    {
        _viewModel.PropertyChanged += OnShellPropertyChangedForSplit;
        NestedHost.SizeChanged += OnNestedHostSizeChanged;
    }

    private void DetachSplit()
    {
        _viewModel.PropertyChanged -= OnShellPropertyChangedForSplit;
        NestedHost.SizeChanged -= OnNestedHostSizeChanged;
    }

    /// <summary>
    /// The split the last session left, once every pane has its drives: the
    /// second pane made where it was, and made the one being worked with if
    /// it was.  Before either pane is entered, so each goes back to its own
    /// camera.
    /// </summary>
    private void RestoreSplit()
    {
        if (_viewModel.IsSplit && !IsPickerMode && SecondPane is null)
        {
            var second = OpenSecondPane(restoring: true);
            if (_viewModel.ActivePaneIndex == 1)
            {
                ActivatePane(second);
            }
        }

        UpdateSplitControls();
    }

    private void OnShellPropertyChangedForSplit(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSplit):
                // Before the panes have their drives the split waits for
                // RestoreSplit, which opens it once they have.
                if (_nestedReady && !IsPickerMode)
                {
                    if (_viewModel.IsSplit && SecondPane is null)
                    {
                        OpenSecondPane(restoring: false);
                    }
                    else if (!_viewModel.IsSplit && SecondPane is { } second)
                    {
                        CloseSecondPane(second);
                    }
                }

                UpdateSplitControls();
                break;
            case nameof(MainViewModel.SplitOrientation):
                LayOutPanes();
                UpdateSplitControls();
                break;
            case nameof(MainViewModel.SplitRatio):
                LayOutPanes();
                break;
        }
    }

    /// <summary>
    /// Makes the second pane: a canvas and a tree of its own, its own inbox
    /// for icons, the drives, orders, layers and hidden rules every pane
    /// shares, and a place to start - where it was when the view was last
    /// split, this session or the last, or else where the first pane is.  It
    /// opens beside the first without taking the keyboard from it: the pane
    /// being worked with stays the one it was.
    /// </summary>
    /// <param name="restoring">
    /// At startup, from the workspace: the item the last session had selected
    /// in it, and no entering yet - the window enters every pane once it is
    /// ready.
    /// </param>
    private NestedPane OpenSecondPane(bool restoring)
    {
        var view = new NestedPaneView();
        AutomationProperties.SetAutomationId(view.Canvas, "NestedSecond");
        AutomationProperties.SetName(view.Canvas, "Folders, second pane");
        var pane = new NestedPane(this, _viewModel, view, _viewModel.Icons.SubscribeCanvas(), index: 1);
        _panes.Add(pane);
        pane.Attach();
        pane.Canvas.LeftDrag = _viewModel.LeftDrag;
        pane.Canvas.ShownLayers = _viewModel.Layers;
        pane.Canvas.IsSpacePanArmed = _isSpaceHeld;
        pane.Initialize(NestedRoots());

        var tree = _viewModel.Tree;
        var saved = tree.SecondPane;
        SelectionItem[] items;
        string? focus;
        if (restoring)
        {
            items = tree.RestoredSecondPaneItem is { } item ? [item] : [];
            focus = tree.RestoredSecondPaneItem?.Path ?? saved?.ActivePath;
        }
        else if (_closedSecondSelection is { } closed)
        {
            items = closed.Items;
            focus = closed.Focus ?? saved?.ActivePath;
        }
        else
        {
            items = [];
            focus = saved?.ActivePath;
        }

        if (string.IsNullOrEmpty(focus))
        {
            focus = FirstPane.FocusPath;
        }

        // Split for the first time, it starts where the first pane is; at
        // startup the first pane is not anywhere yet, and the second goes to
        // its selection instead.
        var camera = saved?.NestedCamera ?? (restoring ? null : FirstPane.Canvas.CaptureCamera());
        pane.StartAt(camera, items, focus);
        tree.SecondPane = new NestedPaneState(focus, camera);
        tree.OtherPanePath = () => InactivePane?.FocusPath;
        tree.IsSecondPaneActive = false;

        LayOutPanes();
        UpdatePaneChrome();
        if (!restoring && IsNested)
        {
            pane.Enter(fromStartup: false, focus: false, fly: false);
        }

        return pane;
    }

    /// <summary>
    /// Closes the second pane: the first becomes the one being worked with
    /// if it was not, the second's place and selection are kept for the next
    /// split, and its canvas, tree, inbox and watches are let go of whole, so
    /// the window is again exactly the one pane it was.
    /// </summary>
    private void CloseSecondPane(NestedPane second)
    {
        if (ReferenceEquals(ActivePane, second))
        {
            ActivatePane(FirstPane);
        }

        var tree = _viewModel.Tree;
        second.CaptureCamera();
        tree.SecondPane = (tree.SecondPane ?? new NestedPaneState(null, null)) with { ActivePath = second.FocusPath };
        _closedSecondSelection = ([.. second.KeptSelection.Items], second.KeptSelection.Focus);
        tree.OtherPanePath = null;
        tree.IsSecondPaneActive = false;

        var hadKeyboard = second.View.IsKeyboardFocusWithin;
        second.Canvas.Tree = null;
        second.Detach();
        _panes.Remove(second);
        LayOutPanes();
        UpdatePaneChrome();
        if (hadKeyboard)
        {
            FocusCanvas();
        }

        tree.ScheduleSave();
    }

    /// <summary>
    /// Lays the canvas area out for the panes there are: one pane fills it,
    /// as the window's one canvas always did; two share it at the split's
    /// ratio, side by side or stacked, with the divider between them.
    /// </summary>
    private void LayOutPanes()
    {
        var host = NestedHost;
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        if (SecondPane is not { } second)
        {
            for (var index = host.Children.Count - 1; index >= 0; index--)
            {
                if (!ReferenceEquals(host.Children[index], FirstPaneView))
                {
                    host.Children.RemoveAt(index);
                }
            }

            FirstPaneView.ClearValue(Grid.RowProperty);
            FirstPaneView.ClearValue(Grid.ColumnProperty);
            return;
        }

        var splitter = _paneSplitter ??= CreatePaneSplitter();
        if (!host.Children.Contains(splitter))
        {
            host.Children.Add(splitter);
        }

        if (!host.Children.Contains(second.View))
        {
            host.Children.Add(second.View);
        }

        var ratio = _viewModel.SplitRatio;
        var first = new GridLength(ratio, GridUnitType.Star);
        var rest = new GridLength(1 - ratio, GridUnitType.Star);
        var gap = new GridLength(PaneSplitterSize);
        UIElement[] parts = [FirstPaneView, splitter, second.View];
        if (_viewModel.SplitOrientation == SplitOrientation.Stacked)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = first });
            host.RowDefinitions.Add(new RowDefinition { Height = gap });
            host.RowDefinitions.Add(new RowDefinition { Height = rest });
            splitter.ResizeDirection = GridResizeDirection.Rows;
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index].ClearValue(Grid.ColumnProperty);
                Grid.SetRow(parts[index], index);
            }
        }
        else
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = first });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = gap });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = rest });
            splitter.ResizeDirection = GridResizeDirection.Columns;
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index].ClearValue(Grid.RowProperty);
                Grid.SetColumn(parts[index], index);
            }
        }

        UpdatePaneMinimums();
    }

    private GridSplitter CreatePaneSplitter()
    {
        var splitter = new GridSplitter { Style = (Style)FindResource("PaneSplitter") };
        AutomationProperties.SetName(splitter, "Divider between the panes");
        splitter.DragCompleted += OnPaneSplitterDragCompleted;
        return splitter;
    }

    /// <summary>
    /// Neither pane may be dragged below its least share of the room: the
    /// divider stops there as it is dragged, rather than jumping back after.
    /// Worked out again whenever the room changes.
    /// </summary>
    private void UpdatePaneMinimums()
    {
        if (SecondPane is null)
        {
            return;
        }

        var host = NestedHost;
        if (_viewModel.SplitOrientation == SplitOrientation.Stacked)
        {
            var least = Math.Max(0, (host.ActualHeight - PaneSplitterSize) * SplitLayout.MinimumRatio);
            if (host.RowDefinitions.Count == 3)
            {
                host.RowDefinitions[0].MinHeight = least;
                host.RowDefinitions[2].MinHeight = least;
            }
        }
        else
        {
            var least = Math.Max(0, (host.ActualWidth - PaneSplitterSize) * SplitLayout.MinimumRatio);
            if (host.ColumnDefinitions.Count == 3)
            {
                host.ColumnDefinitions[0].MinWidth = least;
                host.ColumnDefinitions[2].MinWidth = least;
            }
        }
    }

    private void OnNestedHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePaneMinimums();

    /// <summary>The divider was let go: the panes' shares, as it left them, are the split's ratio from now on.</summary>
    private void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SecondPane is not { } second)
        {
            return;
        }

        NestedHost.UpdateLayout();
        var stacked = _viewModel.SplitOrientation == SplitOrientation.Stacked;
        var first = stacked ? FirstPaneView.ActualHeight : FirstPaneView.ActualWidth;
        var other = stacked ? second.View.ActualHeight : second.View.ActualWidth;
        if (first + other > 0)
        {
            _viewModel.SplitRatio = first / (first + other);
        }

        // The stars back to the ratio itself, whether or not it changed.
        LayOutPanes();
    }

    // ---- the pane being worked with ------------------------------------------------

    /// <summary>
    /// Makes <paramref name="pane"/> the one being worked with.  The pane
    /// that was keeps what it had selected, and this one's selection becomes
    /// the window's - not a step for Back and Forward, and with nothing
    /// selected, its folder in view is where the address bar and the list
    /// go.  Back and Forward step through this pane's history from now on,
    /// the zoom buttons and the keys act on its canvas, and its header takes
    /// the accent.  Nothing flies anywhere: each pane stays where it is.
    /// </summary>
    internal void ActivatePane(NestedPane pane)
    {
        if (ReferenceEquals(pane, ActivePane) || !_panes.Contains(pane))
        {
            return;
        }

        var outgoing = ActivePane;
        outgoing.Deactivate();
        ActivePane = pane;
        _viewModel.History = pane.History;
        _viewModel.ActivePaneIndex = pane.Index;
        _viewModel.Tree.IsSecondPaneActive = pane.Index == 1;
        pane.Activate();
        _viewModel.NestedZoomLabel = pane.Canvas.ZoomText;
        UpdatePaneChrome();
        foreach (var each in _panes)
        {
            each.UpdateSortHeaders();
            each.ScheduleBeacons();
        }
    }

    void INestedPaneHost.ActivatePane(NestedPane pane) => ActivatePane(pane);

    /// <summary>F6: the other pane is the one being worked with, and has the keyboard.</summary>
    private void ActivateOtherPane()
    {
        if (InactivePane is { } other)
        {
            ActivatePane(other);
            FocusCanvas(other);
        }
    }

    /// <summary>Each pane's header shown for a split and hidden for one pane, the accent on the one being worked with.</summary>
    private void UpdatePaneChrome()
    {
        var split = _panes.Count > 1;
        foreach (var pane in _panes)
        {
            pane.View.ShowAsPane(split, ReferenceEquals(pane, ActivePane));
            pane.UpdateHeader();
            pane.ScheduleHeader();
        }
    }

    // ---- between the panes ---------------------------------------------------------
    //
    // Dragging from one pane onto a folder of the other is a drop like any
    // other (NestedPane): Explorer's rules, the folder lit while the drag is
    // over it.  Without the mouse, what is selected in the pane being worked
    // with goes to the other with Shift+F5 and Shift+F6 or its menu, and a
    // folder's menu opens it in the other pane.

    /// <summary>
    /// The folder of the pane not being worked with that Copy and Move to
    /// other pane put things into: the folder selected there, or the one the
    /// file selected there is in, or else the folder it has in view - the
    /// rule its sort headers go by (see <see cref="NestedPane.SortFolder"/>).
    /// Null with one pane, on the tree canvas, and with the other pane at
    /// This PC with nothing selected there.
    /// </summary>
    internal string? OtherPaneFolder() => IsNested && !IsPickerMode && InactivePane is { } other ? other.SortFolder() : null;

    /// <summary>
    /// What of <paramref name="paths"/> can go into <paramref name="folder"/>:
    /// not the folder itself, not a folder it is inside, and not what is in
    /// it already - a copy or a move there would do nothing, or ask the Shell
    /// something that makes no sense.  Only the names are compared.
    /// </summary>
    internal static string[] PathsGoingTo(IReadOnlyList<string> paths, string folder) =>
    [
        .. paths.Where(path =>
            !ViewAllPath.Equals(path, folder)
            && !(Path.GetDirectoryName(path) is { Length: > 0 } parent && ViewAllPath.Equals(parent, folder))
            && !NativeShellService.IsInvalidMoveTarget(path, folder))
    ];

    /// <summary>
    /// Copy or Move to other pane - Shift+F5 and Shift+F6, or the item menu -
    /// for <paramref name="paths"/>, what the pane being worked with has
    /// selected: into the other pane's folder (<see cref="OtherPaneFolder"/>)
    /// through the Shell, as a paste or a drop goes, with its progress, its
    /// questions and its Undo.  A move lets go of what moved in both panes'
    /// selections.  Where nothing can go, the toast says why.
    /// </summary>
    internal async Task SendToOtherPaneAsync(IReadOnlyList<string> paths, bool move)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (OtherPaneFolder() is not { } folder)
        {
            _viewModel.Toast.ShowError(SecondPane is null
                ? "There is no other pane: split the view first (Ctrl+\\)."
                : IsNested
                    ? "The other pane has no folder in view: go into one there first."
                    : "The other pane is on the nested canvas: go back to it first.");
            return;
        }

        var name = FolderName(folder);
        string[] going;
        try
        {
            if (!Directory.Exists(folder))
            {
                _viewModel.Toast.ShowError($"{name} is no longer there.");
                return;
            }

            going = PathsGoingTo(paths, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _viewModel.Toast.ShowError(ex.Message);
            return;
        }

        if (going.Length == 0)
        {
            _viewModel.Toast.ShowError($"Nothing to {(move ? "move" : "copy")}: what is selected is {name} or already in it.");
            return;
        }

        await _viewModel.DropIntoPathAsync(going, folder, move);
    }

    /// <summary>
    /// Open in other pane: the other pane goes to <paramref name="folder"/>
    /// and has it selected (see <see cref="NestedPane.OpenAsync"/>); the pane
    /// being worked with stays the one it was.  A window that is not split is
    /// split for it - from the tree canvas, onto the nested one.
    /// </summary>
    /// <param name="animated">Whether a pane that was already on screen flies there; a pane the split has only just made is simply there.</param>
    internal async Task OpenInOtherPaneAsync(string folder, bool animated = true)
    {
        if (IsPickerMode || string.IsNullOrEmpty(folder))
        {
            return;
        }

        var shown = SecondPane is not null && IsNested;
        if (!_viewModel.IsSplit)
        {
            _viewModel.IsSplit = true;
        }
        else if (!IsNested)
        {
            _viewModel.Layout = CanvasLayout.Nested;
        }

        if (InactivePane is { } other)
        {
            await other.OpenAsync(folder, animated && shown);
        }
    }

    /// <summary>
    /// The split view's entries on the menu of items on the nested canvas:
    /// Open in other pane for a <paramref name="folder"/> - which splits the
    /// view if it is not split - and, while it is, Copy and Move to other
    /// pane for <paramref name="paths"/>, named after the folder they would
    /// go to, and not to be chosen where nothing can go there.  Nothing on
    /// the tree canvas or in a file dialog, which are never split.
    /// </summary>
    internal List<ShellMenuEntry> OtherPaneEntries(IReadOnlyList<string> paths, string? folder)
    {
        var entries = new List<ShellMenuEntry>();
        if (IsPickerMode || !IsNested)
        {
            return entries;
        }

        if (folder is not null)
        {
            entries.Add(OpenInOtherPaneEntry(folder));
        }

        if (SecondPane is null || paths.Count == 0)
        {
            return entries;
        }

        var target = OtherPaneFolder();
        string[] going;
        try
        {
            going = target is null ? [] : PathsGoingTo(paths, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            going = [];
        }

        var name = target is null ? null : FolderName(target);
        var why = name is null
            ? "The other pane has no folder in view: go into one there first"
            : going.Length == 0 ? $"What is selected is {name} or already in it" : null;
        string[] sent = [.. paths];
        entries.Add(new ShellMenuEntry(name is null ? "Copy to other pane" : $"Copy to other pane ({name})", () => _ = SendToOtherPaneAsync(sent, move: false))
        {
            Glyph = "",
            Shortcut = "Shift+F5",
            IsEnabled = going.Length > 0,
            ToolTip = why ?? $"Copies what is selected into {name}, the folder of the other pane"
        });
        entries.Add(new ShellMenuEntry(name is null ? "Move to other pane" : $"Move to other pane ({name})", () => _ = SendToOtherPaneAsync(sent, move: true))
        {
            Glyph = "",
            Shortcut = "Shift+F6",
            IsEnabled = going.Length > 0,
            ToolTip = why ?? $"Moves what is selected into {name}, the folder of the other pane"
        });
        return entries;
    }

    /// <summary>A folder's Open in other pane, on the nested canvas only; null elsewhere.</summary>
    private ShellMenuEntry? OpenInOtherPaneEntryFor(string folder) =>
        IsPickerMode || !IsNested ? null : OpenInOtherPaneEntry(folder);

    private ShellMenuEntry OpenInOtherPaneEntry(string folder) =>
        new("Open in other pane", () => _ = OpenInOtherPaneAsync(folder))
        {
            Glyph = "",
            ToolTip = SecondPane is null
                ? $"Splits the view, with {FolderName(folder)} in the second pane (F6 goes to it)"
                : $"Shows {FolderName(folder)} in the other pane (F6 goes to it)"
        };

    /// <summary>
    /// Shift+F5 copies what is selected into the other pane's folder and
    /// Shift+F6 moves it there - Total Commander's F5 and F6, with Shift, as
    /// F5 alone reads the folder again and F6 goes to the other pane.  Only
    /// from where the selection is shown, as the other keys that act on it.
    /// </summary>
    internal bool TryHandlePaneTransferKey(Key key, ModifierKeys modifiers)
    {
        if (IsPickerMode || modifiers != ModifierKeys.Shift || key is not (Key.F5 or Key.F6))
        {
            return false;
        }

        _ = SendToOtherPaneAsync([.. _viewModel.Tree.SelectedPaths], move: key == Key.F6);
        return true;
    }

    // ---- the ways to it ------------------------------------------------------------

    /// <summary>
    /// The split's keys, wherever the keyboard is: Ctrl+\ splits the view or
    /// closes the split, Ctrl+Shift+\ turns it from side by side to stacked
    /// and back - splitting it if it was not - and F6 goes to the other pane,
    /// as it moves between Explorer's panes.  None of them in a file dialog.
    /// </summary>
    internal bool TryHandleSplitKey(Key key, ModifierKeys modifiers)
    {
        if (IsPickerMode)
        {
            return false;
        }

        if (key is Key.Oem5 or Key.OemBackslash)
        {
            switch (modifiers)
            {
                case ModifierKeys.Control:
                    _viewModel.IsSplit = !_viewModel.IsSplit;
                    return true;
                case ModifierKeys.Control | ModifierKeys.Shift:
                    SwitchSplitOrientation();
                    return true;
            }
        }

        if (key == Key.F6 && modifiers == ModifierKeys.None && IsNested && SecondPane is not null)
        {
            ActivateOtherPane();
            return true;
        }

        return false;
    }

    /// <summary>Side by side becomes stacked and stacked side by side, and the view is split if it was not.</summary>
    private void SwitchSplitOrientation()
    {
        _viewModel.SplitOrientation = _viewModel.SplitOrientation == SplitOrientation.Stacked
            ? SplitOrientation.SideBySide
            : SplitOrientation.Stacked;
        _viewModel.IsSplit = true;
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e) => _viewModel.IsSplit = !_viewModel.IsSplit;

    private void SplitMenuButton_Click(object sender, RoutedEventArgs e) => BuildSplitMenu(SplitButton).IsOpen = true;

    /// <summary>The split button's drop-down: the split itself, and how its panes are laid out.</summary>
    internal ContextMenu BuildSplitMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        AddSplitItems(menu);
        return menu;
    }

    /// <summary>
    /// Split view and its two layouts, as the split button's drop-down and
    /// Canvas options have them.  Choosing a layout splits the view if it is
    /// not split yet.
    /// </summary>
    private void AddSplitItems(ItemsControl menu)
    {
        var split = new MenuItem
        {
            Header = "Split view",
            IsCheckable = true,
            IsChecked = _viewModel.IsSplit,
            InputGestureText = "Ctrl+\\",
            ToolTip = "Two panes, each at its own place on the disk; F6 goes from one to the other"
        };
        split.Click += (_, _) => _viewModel.IsSplit = !_viewModel.IsSplit;
        menu.Items.Add(split);
        menu.Items.Add(new Separator());
        foreach (var (choice, name, tip) in new[]
        {
            (SplitOrientation.SideBySide, "Side by side", "The second pane to the right of the first (Ctrl+Shift+\\ switches)"),
            (SplitOrientation.Stacked, "Stacked", "The second pane below the first (Ctrl+Shift+\\ switches)")
        })
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = _viewModel.IsSplit && _viewModel.SplitOrientation == choice,
                ToolTip = tip
            };
            var chosen = choice;
            item.Click += (_, _) =>
            {
                _viewModel.SplitOrientation = chosen;
                _viewModel.IsSplit = true;
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// The split button as the split is: its glyph the layout it makes, lit
    /// while the view is split; not there at all in a file dialog, which
    /// never splits.
    /// </summary>
    private void UpdateSplitControls()
    {
        SplitControls.Visibility = IsPickerMode ? Visibility.Collapsed : Visibility.Visible;
        SplitGlyph.Data = _viewModel.SplitOrientation == SplitOrientation.Stacked ? StackedGlyph : SideBySideGlyph;
        if (_viewModel.IsSplit)
        {
            SplitButton.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            SplitButton.ClearValue(ForegroundProperty);
        }
    }

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
