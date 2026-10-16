using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow
{
    // A tab retains the complete existing explorer view. Its window object is
    // never shown: the content lives below one shared caption and all native
    // owners use ShellWindow. No navigation state is replayed when switching.
    private MainWindow? _tabHostWindow;
    private bool _isTabHost;
    private bool _closingTabHost;
    private bool _contentLoadStarted;
    private bool _contextClosed;
    private Grid? _tabContentDeck;
    private readonly List<MainWindow> _tabContexts = [];
    private readonly Dictionary<MainWindow, TabHeader> _tabHeaders = [];
    private MainWindow? _activeTabContext;
    private MainWindow? _dragHoverTab;
    private readonly DispatcherTimer _tabHoverTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };
    private IInputElement? _tabKeyboardFocus;
    private bool _tabHostUpdatesStarted;
    private const string TabDragFormat = "UltraExplorer.TabContext";
    private readonly Stack<string> _closedTabPaths = [];

    internal MainWindow ShellWindow => _tabHostWindow ?? this;
    internal bool HasFolderTabs => ShellWindow._isTabHost;
    internal MainWindow ActiveTabContext => ShellWindow._activeTabContext ?? this;
    internal IReadOnlyList<MainWindow> TabContexts => _isTabHost ? _tabContexts : [this];
    internal bool IsTabPresented => _tabHostWindow is null || ReferenceEquals(_tabHostWindow._activeTabContext, this);
    internal FrameworkElement ContextRoot => ContextContent;
    internal int TabCount => ShellWindow._tabContexts.Count;

    internal MainWindow EnableFolderTabs(FolderInvocation? initial = null)
    {
        if (IsPickerMode) throw new InvalidOperationException("File dialogs do not contain explorer tabs.");
        if (_isTabHost) return ActiveTabContext;
        if (_contentLoadStarted) throw new InvalidOperationException("Tabs must be enabled before showing a standalone window.");
        _isTabHost = true;
        ContextContent.Children.Remove(ContextTitleBar);
        Content = null;
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.Children.Add(ContextTitleBar);
        _tabContentDeck = new Grid();
        Grid.SetRow(_tabContentDeck, 1);
        shell.Children.Add(_tabContentDeck);
        Content = shell;
        StandaloneTabHeader.Visibility = Visibility.Collapsed;
        TabHeaderScroll.Visibility = Visibility.Visible;
        _tabHoverTimer.Tick += (_, _) => CompleteTabHover();
        TabHeaderScroll.PreviewMouseWheel += (_, e) =>
        {
            TabHeaderScroll.ScrollToHorizontalOffset(TabHeaderScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        SizeChanged += (_, _) => UpdateTabStripWidth();
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) =>
        {
            _activeTabContext?.RememberTabKeyboardFocus();
            _activeTabContext?.MarkFolderRequestUsed();
        }), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) =>
            _activeTabContext?.MarkFolderRequestUsed()), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler((_, _) =>
        {
            ActiveTabContext.Shelf.NotifyDragOver();
        }), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, _) => CancelTabHover()), handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this)) CancelTabHover();
        }), handledEventsToo: true);
        var context = initial is null ? AddTabContext(null, null) : CreateFolderTab(initial);
        UpdateTabStripWidth();
        return context;
    }

    internal MainWindow? FindFolderContext(Guid destination)
    {
        if (_isTabHost) return _tabContexts.FirstOrDefault(context => !context._closeRequested
            && !context._contextClosed && context.FolderDestinationId == destination);
        return !_closeRequested && !_contextClosed && FolderDestinationId == destination ? this : null;
    }

    internal MainWindow CreateFolderTab(FolderInvocation invocation)
    {
        var host = ShellWindow;
        if (!host._isTabHost) throw new InvalidOperationException("This window has no tab host.");
        if (host._closeRequested) throw new InvalidOperationException("This window is closing.");
        if (invocation.DestinationId == Guid.Empty) invocation = invocation with { DestinationId = Guid.NewGuid() };
        if (host.FindFolderContext(invocation.DestinationId) is { } existing) return existing;
        return host.AddTabContext(ExplorerLaunchRouter.FolderWorkspacePath(invocation.DestinationId), invocation);
    }

    private MainWindow AddTabContext(string? workspace, FolderInvocation? invocation)
    {
        var context = new MainWindow(null, workspace) { _tabHostWindow = this, ShowActivated = false };
        context.FolderDestinationId = invocation?.DestinationId ?? Guid.NewGuid();
        if (invocation is not null) context.PrepareFolderInvocation(invocation);
        var root = context.ContextContent;
        context.Content = null;
        root.DataContext = context._viewModel;
        root.Resources.MergedDictionaries.Add(context.Resources);
        context.ContextTitleBar.Visibility = Visibility.Collapsed;
        root.RowDefinitions[0].Height = new GridLength(0);
        // Hidden still measures and arranges an inactive context. A request
        // arriving while another tab is loading can therefore frame its folder
        // and produce an exact receipt without needing to steal the active tab.
        root.Visibility = Visibility.Hidden;
        _tabContexts.Add(context);
        _tabContentDeck!.Children.Add(root);
        var header = MakeTabHeader(context);
        _tabHeaders.Add(context, header);
        TabHeaderPanel.Children.Add(header.Border);
        context._viewModel.PropertyChanged += header.TitleChanged;
        context.FolderLocationChanged += TabLocationChanged;
        context.Closed += (_, _) => RemoveTabContext(context);
        root.Loaded += (_, e) =>
        {
            if (context._contentLoadStarted || context._closeRequested) return;
            context.InitializeEmbeddedSource();
            context.Window_Loaded(root, e);
        };
        // Content can be added to an already loaded host while its initial
        // async folder read is pending. Initialization is guarded per context.
        SelectTabContext(context, focus: false);
        return context;
    }

    private void InitializeEmbeddedSource()
    {
        AttachHoverPreviewWindow();
        DependencyPropertyDescriptor.FromProperty(Nodify.NodifyEditor.ViewportLocationProperty,
            typeof(Nodify.NodifyEditor)).AddValueChanged(Editor, OnViewportLocationChanged);
        OnSourceInitializedForDevices(this, EventArgs.Empty);
    }

    private sealed record TabHeader(Border Border, Button Select, PropertyChangedEventHandler TitleChanged);

    private TabHeader MakeTabHeader(MainWindow context)
    {
        var border = new Border
        {
            Height = 34, Width = 220, Margin = new Thickness(0, 0, 4, 0),
            CornerRadius = new CornerRadius(7, 7, 0, 0), BorderThickness = new Thickness(0, 0, 0, 2),
            AllowDrop = true
        };
        WindowChrome.SetIsHitTestVisibleInChrome(border, true);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var text = new TextBlock { Text = context._viewModel.TabTitle, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0), Foreground = (Brush)FindResource("TextBrush") };
        var select = new Button { Content = text, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)FindResource("ChromeButton") };
        AutomationProperties.SetName(select, "Switch to " + text.Text);
        select.Click += (_, _) => { context.MarkFolderRequestUsed(); SelectTabContext(context); };
        var close = new Button { Width = 28, Height = 28, Padding = new Thickness(0),
            Content = new TextBlock { Text = "\uE711", FontSize = 9, Style = (Style)FindResource("IconText") },
            Style = (Style)FindResource("ChromeButton"), ToolTip = "Close tab (Ctrl+W)" };
        AutomationProperties.SetName(close, "Close " + text.Text + " tab");
        close.Click += async (_, _) => await CloseTabContextAsync(context);
        Grid.SetColumn(close, 1);
        body.Children.Add(select);
        body.Children.Add(close);
        border.Child = body;
        border.PreviewMouseDown += async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            await CloseTabContextAsync(context);
        };
        Point? dragStart = null;
        border.PreviewMouseLeftButtonDown += (_, e) =>
            dragStart = ReferenceEquals(FindAncestor<Button>(e.OriginalSource as DependencyObject), close) ? null : e.GetPosition(this);
        border.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        border.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
            var pointer = e.GetPosition(this);
            if (Math.Abs(pointer.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pointer.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragStart = null;
            select.ReleaseMouseCapture();
            var data = new DataObject();
            data.SetData(TabDragFormat, context, autoConvert: false);
            context.MarkFolderRequestUsed();
            DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
            e.Handled = true;
        };
        void HeaderDragOver(object sender, DragEventArgs e)
        {
            if (DraggedTab(e.Data) is not null)
            {
                CancelTabHover();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
            if (!HasFileDragData(e.Data)) { CancelTabHover(); e.Effects = DragDropEffects.None; e.Handled = true; return; }
            QueueTabHover(context);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        // WPF first delivers DragEnter when the OLE target changes. Starting
        // the timer there also switches tabs when the pointer is held still.
        border.DragEnter += HeaderDragOver;
        border.DragOver += HeaderDragOver;
        border.DragLeave += (_, _) => { if (ReferenceEquals(_dragHoverTab, context)) CancelTabHover(); };
        border.Drop += (_, e) =>
        {
            CancelTabHover();
            if (DraggedTab(e.Data) is { } dragged)
            {
                MoveTabTo(dragged, _tabContexts.IndexOf(context));
                e.Effects = DragDropEffects.Move;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        var menu = new ContextMenu();
        void Item(string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Item("New tab", () => NewExplorerTab());
        Item("Duplicate tab", () => NewExplorerTab(context.CurrentFolderPath));
        Item("Close tab", () => _ = CloseTabContextAsync(context));
        Item("Close other tabs", () => _ = CloseOtherTabsAsync(context));
        menu.Items.Add(new Separator());
        Item("Move tab left", () => MoveTab(context, -1));
        Item("Move tab right", () => MoveTab(context, 1));
        border.ContextMenu = menu;
        menu.Opened += (_, _) => context.MarkFolderRequestUsed();
        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.TabTitle)) return;
            text.Text = context._viewModel.TabTitle;
            AutomationProperties.SetName(select, "Switch to " + text.Text);
            AutomationProperties.SetName(close, "Close " + text.Text + " tab");
            border.ToolTip = context.CurrentFolderPath;
        };
        return new(border, select, changed);
    }

    internal void SelectTabContext(MainWindow context, bool focus = true)
    {
        var host = ShellWindow;
        if (!ReferenceEquals(host, this)) { host.SelectTabContext(context, focus); return; }
        if (!_tabContexts.Contains(context) || context._closeRequested) return;
        if (ReferenceEquals(context, _activeTabContext)) return;
        var previous = _activeTabContext;
        if (previous is not null)
        {
            previous.RememberTabKeyboardFocus();
            previous.SuspendTabContext();
            previous.ContextContent.Visibility = Visibility.Hidden;
        }
        _activeTabContext = context;
        DataContext = context._viewModel;
        context.ContextContent.Visibility = Visibility.Visible;
        context.ResumeTabContext();
        RefreshTabHeaders();
        FolderLocationChanged?.Invoke(this, EventArgs.Empty);
        Title = context._viewModel.TabTitle + " — UltraExplorer";
        _tabHeaders[context].Border.BringIntoView();
        if (!focus || !IsActive || IsTestWindow || IsDiagnosticsRun) return;
        if (context._tabKeyboardFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        else context.FocusCanvas();
    }

    private void TabLocationChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _activeTabContext))
        {
            Title = ActiveTabContext._viewModel.TabTitle + " — UltraExplorer";
            FolderLocationChanged?.Invoke(this, e);
        }
    }

    private void RefreshTabHeaders()
    {
        foreach (var (context, header) in _tabHeaders)
        {
            var active = ReferenceEquals(context, _activeTabContext);
            header.Border.Background = (Brush)FindResource(active ? "SurfaceRaisedBrush" : "WindowBrush");
            header.Border.BorderBrush = (Brush)FindResource(ReferenceEquals(context, _dragHoverTab)
                ? "AccentBrush" : active ? "BorderStrongBrush" : "WindowBrush");
            AutomationProperties.SetHelpText(header.Select, active ? "Active tab" : "Inactive tab");
        }
    }

    internal static bool HasFileDragData(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return false; }
    }

    internal void QueueTabHover(MainWindow context)
    {
        if (!_tabContexts.Contains(context) || context._closeRequested || ReferenceEquals(context, _activeTabContext))
        { CancelTabHover(); return; }
        if (ReferenceEquals(_dragHoverTab, context)) return;
        CancelTabHover();
        _dragHoverTab = context;
        RefreshTabHeaders();
        _tabHoverTimer.Start();
    }

    internal void CompleteTabHover()
    {
        _tabHoverTimer.Stop();
        var context = _dragHoverTab;
        if (context is not null && !context._closeRequested) SelectTabContext(context, focus: false);
        // The payload stays owned by DoDragDrop. Headers never consume files;
        // the normal canvas/list destination handles Copy/Move/invalid targets.
        CancelTabHover();
    }

    internal void CancelTabHover()
    {
        _tabHoverTimer.Stop();
        if (_dragHoverTab is null) return;
        _dragHoverTab = null;
        RefreshTabHeaders();
    }

    internal MainWindow NewExplorerTab(string? path = null)
    {
        var host = ShellWindow;
        if (!host._isTabHost || host._closeRequested) return host.ActiveTabContext;
        path ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!FolderCommandLine.TryOpenFolder(path, out var request, out var error))
        { ActiveTabContext._viewModel.Toast.ShowError(error); return ActiveTabContext; }
        request = request with { DestinationId = Guid.NewGuid() };
        var context = host.CreateFolderTab(request);
        _ = context.ApplyFolderInvocationAsync(request);
        return context;
    }

    internal async Task<bool> CloseTabContextAsync(MainWindow context)
    {
        if (context._contextClosed) return true;
        var path = context.CurrentFolderPath;
        context.Close();
        while (!context._contextClosed && context._closeRequested) await Task.Delay(20);
        if (context._contextClosed && !_closingTabHost && Directory.Exists(path))
            _closedTabPaths.Push(path);
        return context._contextClosed;
    }

    private async Task CloseOtherTabsAsync(MainWindow keep)
    {
        foreach (var context in _tabContexts.Where(context => !ReferenceEquals(context, keep)).ToArray())
            if (!await CloseTabContextAsync(context)) break;
    }

    private void RemoveTabContext(MainWindow context)
    {
        context._contextClosed = true;
        ExplorerLaunchRouter.ReleaseFolderContext(context.FolderDestinationId);
        if (!_tabContexts.Remove(context)) return;
        context.FolderLocationChanged -= TabLocationChanged;
        if (_tabHeaders.Remove(context, out var header))
        {
            context._viewModel.PropertyChanged -= header.TitleChanged;
            TabHeaderPanel.Children.Remove(header.Border);
        }
        _tabContentDeck!.Children.Remove(context.ContextContent);
        if (ReferenceEquals(_dragHoverTab, context)) CancelTabHover();
        if (ReferenceEquals(_activeTabContext, context))
        {
            _activeTabContext = null;
            if (_tabContexts.LastOrDefault() is { } next) SelectTabContext(next);
        }
        if (_tabContexts.Count == 0 && !_closingTabHost) Close();
    }

    private async Task CloseTabHostAsync()
    {
        if (_closingTabHost) return;
        _closingTabHost = true;
        _closeRequested = true;
        CancelTabHover();
        try
        {
            foreach (var context in _tabContexts.ToArray())
                if (!await CloseTabContextAsync(context)) { _closeRequested = false; return; }
            _allowClose = true;
            _ = Dispatcher.InvokeAsync(Close);
        }
        finally { _closingTabHost = false; }
    }

    private void MoveTab(MainWindow context, int direction)
    {
        var index = _tabContexts.IndexOf(context);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _tabContexts.Count) return;
        MoveTabTo(context, target);
    }

    private void MoveTabTo(MainWindow context, int target)
    {
        var index = _tabContexts.IndexOf(context);
        if (index < 0 || target < 0 || target >= _tabContexts.Count || index == target) return;
        _tabContexts.RemoveAt(index);
        _tabContexts.Insert(target, context);
        TabHeaderPanel.Children.Remove(_tabHeaders[context].Border);
        TabHeaderPanel.Children.Insert(target, _tabHeaders[context].Border);
    }

    private MainWindow? DraggedTab(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(TabDragFormat, autoConvert: false)
                && data.GetData(TabDragFormat, autoConvert: false) is MainWindow context && _tabContexts.Contains(context) ? context : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    private void UpdateTabStripWidth() => TabHeaderScroll.MaxWidth = Math.Max(180, ActualWidth - 210);

    private void RememberTabKeyboardFocus()
    {
        if (Keyboard.FocusedElement is DependencyObject focused && IsInsideFocusSurface(focused, ContextRoot))
            _tabKeyboardFocus = Keyboard.FocusedElement;
    }

    private void RestorePresentedFocusAfterDrag()
    {
        var host = ShellWindow;
        if (host._closeRequested || !host.IsActive || IsTestWindow || IsDiagnosticsRun) return;
        var context = host._activeTabContext;
        if (context is null || context._closeRequested || ReferenceEquals(context, this)) return;
        if (context._tabKeyboardFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        else context.FocusCanvas();
    }

    private void StartTabHostQuietUpdates()
    {
        var host = ShellWindow;
        if (!host._isTabHost || host._tabHostUpdatesStarted || host._closeRequested || IsTestWindow || IsDiagnosticsRun) return;
        if (Application.Current is App app)
        {
            host._tabHostUpdatesStarted = true;
            app.StartQuietUpdates(host);
        }
    }

    private bool TryTabKey(Key key, ModifierKeys modifiers)
    {
        if (!_isTabHost || _tabContexts.Count == 0) return false;
        if (_closeRequested) return true;
        if (modifiers == ModifierKeys.Control && key == Key.T) { NewExplorerTab(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.W) { _ = CloseTabContextAsync(ActiveTabContext); return true; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T)
        {
            if (_closedTabPaths.TryPop(out var path)) NewExplorerTab(path);
            return true;
        }
        if ((modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && key == Key.Tab)
        {
            var step = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            var index = _tabContexts.IndexOf(ActiveTabContext);
            if (_tabContexts.Count > 0)
            {
                var selected = _tabContexts[(index + step + _tabContexts.Count) % _tabContexts.Count];
                selected.MarkFolderRequestUsed();
                SelectTabContext(selected);
            }
            return true;
        }
        if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D9)
        {
            var index = key == Key.D9 ? _tabContexts.Count - 1 : key - Key.D1;
            if (index >= 0 && index < _tabContexts.Count)
            {
                _tabContexts[index].MarkFolderRequestUsed();
                SelectTabContext(_tabContexts[index]);
            }
            return true;
        }
        return false;
    }
}
