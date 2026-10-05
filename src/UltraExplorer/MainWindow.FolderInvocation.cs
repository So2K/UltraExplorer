using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Models;

namespace UltraExplorer;

public partial class MainWindow
{
    private readonly TaskCompletionSource _folderLaunchReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _folderLaunchTicket;
    private readonly Dictionary<NestedPane, string> _folderPaneRoots = new();
    private readonly Dictionary<NestedPane, int> _folderLoadOwners = new();
    private string? _folderInitialPath;

    internal void PrepareFolderInvocation(FolderInvocation invocation)
    {
        _folderInitialPath = invocation.FolderPath;
        _folderPaneRoots[ActivePane] = invocation.FolderPath;
        _handoffUntouched = invocation.OriginIsShell && invocation.DestinationId != Guid.Empty;
    }

    /// <summary>
    /// Opened for an Explorer window being handed over, and not used since:
    /// no click, key, wheel or touch in it, and no other folder sent to it.
    /// A handoff given up closes such a window, which would otherwise stay
    /// beside the Explorer window as a second one nobody asked for
    /// (<see cref="ExplorerLaunchRouter.DiscardIfUntouchedAsync"/>).
    /// </summary>
    internal bool IsUntouchedHandoff => _handoffUntouched && !_closeRequested;
    private bool _handoffUntouched;

    public string CurrentFolderPath => _viewModel.Tree.FolderList.FolderPath;
    public IReadOnlyList<string> CurrentFolderSelection => _viewModel.Tree.Selection.Paths.ToArray();
    public event EventHandler? FolderLocationChanged;
    internal bool IsFolderWindowReady => _folderLaunchReady.Task.IsCompletedSuccessfully && !_closeRequested && !IsPickerMode;
    internal Guid FolderDestinationId { get; set; }

    /// <summary>Saving on its way out: a folder sent now would be dropped here.</summary>
    internal bool IsFolderWindowClosing => _closeRequested;

    /// <summary>
    /// Comes back once every window of <paramref name="application"/> that
    /// has begun to close has closed - its close-time save on disk by then -
    /// or once <paramref name="limit"/> has passed.  A process that ends once
    /// it has answered - a picker its caller, a dialog worker its agent -
    /// waits for this before Application.Shutdown, which ends the dispatcher
    /// at once: the save was cut off half way, the last changes lost and its
    /// temporary file left behind.  UI thread.
    /// </summary>
    internal static async Task WhenClosingWindowsClosedAsync(Application application, TimeSpan limit)
    {
        var until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (application.Windows.OfType<MainWindow>().Any(window => window.IsFolderWindowClosing)
            && Environment.TickCount64 < until)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A folder request is waiting for this window or opening its folder in
    /// it.  The broker gives a request that arrives meanwhile a window of its
    /// own: several folders opened together from the Shell would otherwise
    /// each take this window from the one before, and only the last open.
    /// </summary>
    internal bool IsFolderInvocationPending => _folderInvocationsPending > 0;
    private int _folderInvocationsPending;

    private sealed class PendingFolderInvocation : IDisposable
    {
        private MainWindow? _window;
        internal PendingFolderInvocation(MainWindow window) { _window = window; window._folderInvocationsPending++; }
        public void Dispose() { if (_window is { } window) window._folderInvocationsPending--; _window = null; }
    }

    private void AttachFolderInvocationState()
    {
        _viewModel.Tree.FolderList.PropertyChanged += OnFolderInvocationStateChanged;
        _viewModel.Tree.PropertyChanged += OnFolderInvocationStateChanged;

        // Any use of the window makes it the user's (see IsUntouchedHandoff).
        PreviewMouseDown += (_, _) => _handoffUntouched = false;
        PreviewMouseWheel += (_, _) => _handoffUntouched = false;
        PreviewKeyDown += (_, _) => _handoffUntouched = false;
        PreviewTouchDown += (_, _) => _handoffUntouched = false;
        PreviewStylusDown += (_, _) => _handoffUntouched = false;
        Closed += (_, _) =>
        {
            _viewModel.Tree.FolderList.PropertyChanged -= OnFolderInvocationStateChanged;
            _viewModel.Tree.PropertyChanged -= OnFolderInvocationStateChanged;
        };
    }

    private void OnFolderInvocationStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ActivePane is not null && _folderPaneRoots.ContainsKey(ActivePane)
            && e.PropertyName is nameof(FolderListViewModel.FolderPath) or nameof(ViewAllViewModel.ActivePath))
        {
            _viewModel.Address.SetPath(CurrentFolderPath);
            _viewModel.DialogTitle = FolderName(CurrentFolderPath);
        }
        if (e.PropertyName == nameof(FolderListViewModel.FolderPath)) FolderLocationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFolderPaneLocation(NestedPane pane, string path)
    {
        _folderPaneRoots[pane] = path;
        pane.Canvas.LoadUnfocusedRoots = false;
        SyncNestedRoots();
    }

    internal bool MatchesFolderInvocation(FolderInvocation invocation)
        => IsFolderWindowReady && string.Equals(CurrentFolderPath, invocation.FolderPath, StringComparison.OrdinalIgnoreCase)
            && _viewModel.Tree.Selection.Paths.SequenceEqual(invocation.Kind == FolderInvocationKind.OpenFolder
                ? Array.Empty<string>() : invocation.SelectedPaths, StringComparer.OrdinalIgnoreCase)
            && ActivePane.Canvas.SelectionState.Items().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(_viewModel.Tree.Selection.Paths)
            && _viewModel.Address.CurrentPath == invocation.FolderPath
            && ActivePane.Canvas.CaptureCamera() is { Width: >= 0.4 and <= 1.5 } camera
            && string.Equals(camera.AnchorPath, invocation.FolderPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a filesystem launch in the normal window. The initialized tile
    /// canvas and selection are shared with ordinary navigation, without a
    /// file dialog session or footer.
    /// </summary>
    public Task<bool> ApplyFolderInvocationAsync(FolderInvocation invocation)
        => ApplyFolderInvocationAsync(invocation, CancellationToken.None);

    internal async Task<bool> ApplyFolderInvocationAsync(FolderInvocation invocation, CancellationToken cancellation)
    {
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => ApplyFolderInvocationAsync(invocation, cancellation)).Task.Unwrap();
        }

        bool Allowed() => !invocation.OriginIsShell || DialogIntegrationStore.Read().Enabled;
        if (IsPickerMode || _closeRequested || cancellation.IsCancellationRequested || !Allowed()) return false;

        // Another folder sent here - not the handoff this window was opened
        // for - makes it a window in use.
        if (invocation.DestinationId != FolderDestinationId) _handoffUntouched = false;
        using var pending = new PendingFolderInvocation(this);
        try { await _folderLaunchReady.Task.WaitAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        if (_closeRequested || cancellation.IsCancellationRequested || !Allowed())
        {
            // No request navigates the pane: its drives are read as ever.
            if (!_folderLoadOwners.ContainsKey(ActivePane) && _folderPaneRoots.ContainsKey(ActivePane)) ActivePane.Canvas.LoadUnfocusedRoots = true;
            return false;
        }

        // Taken once the window is ready, not while it starts: a request
        // that gave up during a slow start must not have superseded the one
        // that is still waiting, or neither folder opens.
        var ticket = ++_folderLaunchTicket;
        var pane = ActivePane;
        bool Current() => ticket == _folderLaunchTicket && !_closeRequested && !cancellation.IsCancellationRequested && Allowed()
            && ReferenceEquals(pane, ActivePane) && _panes.Contains(pane);
        void ReleaseLoadGate()
        {
            if (_folderLoadOwners.TryGetValue(pane, out var owner) && owner == ticket)
            {
                _folderLoadOwners.Remove(pane);
                pane.Canvas.LoadUnfocusedRoots = true;
                if (!_panes.Contains(pane)) _folderPaneRoots.Remove(pane);
            }
        }

        try
        {
            _viewModel.Layout = CanvasLayout.Nested;
            // Keep the normal drive hierarchy and focus the real folder;
            // describing its ancestors never enumerates their contents.
            _folderLoadOwners[pane] = ticket;
            SetFolderPaneLocation(pane, invocation.FolderPath);
            // The broker's deadline ends intent as well as its wait. Release
            // this gate promptly even when a network read has not returned.
            using var cancellationRegistration = cancellation.Register(() =>
            {
                if (Dispatcher.CheckAccess()) ReleaseLoadGate();
                else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ReleaseLoadGate, DispatcherPriority.Send);
            });
            if (!await FolderInvocationNavigation.ApplyAsync(_viewModel.Tree, invocation, Current)
                || !Current()) return false;

            pane.SyncSelection();
            await Dispatcher.InvokeAsync(() => { if (Current()) pane.Canvas.UpdateLayout(); }, DispatcherPriority.Loaded);
            if (!Current()) return false;
            await pane.FlyToAsync(invocation.FolderPath, gentle: false, animated: false, requestCurrent: Current, isDirectory: true);
            if (!Current()) return false;

            // Flying resolves the ancestor chain, while target contents may
            // still be waiting for a drawing frame. A readiness receipt must
            // wait for the actual selected tiles, not merely their path set.
            // A reveal shows its files in this pane only, for now: the layers
            // the user chose stay theirs, unsaved, until they next change them.
            if (invocation.Kind == FolderInvocationKind.Reveal && (pane.Canvas.ShownLayers & CanvasLayer.Files) == 0)
            {
                pane.Canvas.ShownLayers |= CanvasLayer.Files;
                pane.ScheduleBeacons();
            }
            pane.Tree.ForceVisible(invocation.SelectedPaths);
            var folders = invocation.SelectedPaths.Select(Path.GetDirectoryName)
                .OfType<string>().Append(invocation.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in folders)
            {
                var folder = await pane.Tree.MaterializePathAsync(path);
                if (!Current() || folder is null || !string.Equals(folder.FullPath, path, StringComparison.OrdinalIgnoreCase)) return false;
                await pane.Tree.LoadAsync(folder);
                if (!Current()) return false;
                pane.Tree.EnsureLayout(folder);
            }
            pane.SyncSelection();
            await Dispatcher.InvokeAsync(() => { if (Current()) pane.Canvas.UpdateLayout(); }, DispatcherPriority.ContextIdle);
            if (!Current()) return false;
            var target = pane.Tree.Find(invocation.FolderPath);
            if (target is null || !pane.Canvas.FlyTo(target, 0.92, animated: false)) return false;
            _viewModel.Address.SetPath(invocation.FolderPath);
            _viewModel.DialogTitle = FolderName(invocation.FolderPath);
            pane.Canvas.RenderNow();

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            if (!IsTestWindow && !IsDiagnosticsRun && (!invocation.OriginIsShell || invocation.DestinationId == Guid.Empty))
            {
                Activate();
                FocusCanvas();
            }
            FolderLocationChanged?.Invoke(this, EventArgs.Empty);
            return MatchesFolderInvocation(invocation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _viewModel.Toast.ShowError(exception.Message);
            return false;
        }
        finally
        {
            // Startup-only sparse loading must end on failure and supersession
            // too. An older request must not lift a newer request's gate, nor
            // leave its own inactive/detached pane permanently sparse.
            ReleaseLoadGate();
        }
    }
}
