using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow
{
    private readonly SearchMapProjectionBuilder _mapSearch = new();
    private DispatcherTimer? _mapTyping;
    private CancellationTokenSource? _mapRun;
    private SearchMapProjection? _mapProjection;
    private string _mapQuery = string.Empty;
    private string[] _mapScope = [];
    private int _mapTicket;
    private bool _mapRefreshAgain;

    internal Task SearchMapTaskForChecks { get; private set; } = Task.CompletedTask;

    private void AttachSearchMap()
    {
        _mapTyping = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _mapTyping.Tick += (_, _) =>
        {
            _mapTyping.Stop();
            SearchMapTaskForChecks = RunSearchMapAsync(_mapTicket, _mapQuery, _mapScope);
        };
        _viewModel.Tree.FilesystemChanged += OnMapFilesystemChanged;
        _viewModel.Search.MapRefreshRequested += OnMapRefreshRequested;
        UpdateSearchMapAvailability();
    }

    private void DetachSearchMap()
    {
        _mapTyping?.Stop();
        _mapTicket++;
        _mapRun?.Cancel();
        _mapRun = null;
        _mapProjection = null;
        _mapScope = [];
        _mapQuery = string.Empty;
        _mapRefreshAgain = false;
        _viewModel.Search.MapRefreshRequested -= OnMapRefreshRequested;
        _viewModel.Tree.FilesystemChanged -= OnMapFilesystemChanged;
    }

    private void UpdateSearchMapAvailability()
    {
        var search = _viewModel.Search;
        search.CanFilterMap = !IsPickerMode && IsNested;
        if (!search.CanFilterMap && search.ShowOnMap) search.ShowOnMap = false;
    }

    private void OnMapRefreshRequested(object? sender, EventArgs e) => ScheduleSearchMap(refresh: true);

    private void OnMapFilesystemChanged() => ScheduleSearchMap(refresh: true);

    private void OnSearchMapPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.Text) or nameof(SearchViewModel.ShowOnMap))
            ScheduleSearchMap();
    }

    private string[] SearchMapScope() => _panes.SelectMany(pane =>
            _folderPaneRoots.TryGetValue(pane, out var folder)
                ? new[] { folder }
                : pane.Tree.Root.Children.Select(root => root.FullPath))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private void ScheduleSearchMap(bool refresh = false)
    {
        var search = _viewModel.Search;
        var query = search.Text.Trim();
        if (_closeRequested || !search.ShowOnMap || !search.CanFilterMap || query.Length == 0)
        {
            _mapTyping?.Stop();
            _mapRun?.Cancel();
            _mapRun = null;
            _mapTicket++;
            _mapRefreshAgain = false;
            _mapQuery = string.Empty;
            _mapProjection = null;
            foreach (var pane in _panes) pane.Canvas.SetSearchMapProjection(null);
            search.IsMapBusy = false;
            search.MapStatus = "Enter a query to filter the map. Positions stay unchanged.";
            return;
        }
        if (!_nestedReady || _mapTyping is null) return;

        var scope = SearchMapScope();
        var same = string.Equals(query, _mapQuery, StringComparison.Ordinal)
            && scope.SequenceEqual(_mapScope, StringComparer.OrdinalIgnoreCase);
        if (same && !refresh) return;
        // Disk changes arriving during a scan ask for one more pass afterwards,
        // instead of cancelling a large query before it can ever finish.
        if (same && refresh && _mapRun is not null)
        {
            _mapRefreshAgain = true;
            return;
        }

        _mapTyping.Stop();
        _mapRun?.Cancel();
        _mapRun = null;
        _mapTicket++;
        _mapQuery = query;
        _mapScope = scope;
        if (!same)
        {
            _mapProjection = null;
            foreach (var pane in _panes) pane.Canvas.SetSearchMapProjection(null);
        }
        search.IsMapBusy = true;
        search.MapStatus = same ? "Updating matches… Positions stay unchanged." : "Searching this map… Positions stay unchanged.";
        _mapTyping.Start();
    }

    private async Task RunSearchMapAsync(int ticket, string query, string[] scope)
    {
        if (_closeRequested || ticket != _mapTicket || !_viewModel.Search.ShowOnMap) return;
        var run = new CancellationTokenSource();
        _mapRun = run;
        _mapRefreshAgain = false;
        bool Current() => !_closeRequested && !run.IsCancellationRequested
            && ticket == _mapTicket && ReferenceEquals(_mapRun, run)
            && _viewModel.Search.ShowOnMap && string.Equals(_viewModel.Search.Text.Trim(), query, StringComparison.Ordinal);
        try
        {
            var result = await Task.Run(() => _mapSearch.RunAsync(query, scope, run.Token), run.Token);
            if (!Current()) return;
            _mapProjection = result.Projection;
            foreach (var pane in _panes) pane.Canvas.SetSearchMapProjection(_mapProjection);
            _viewModel.Search.MapStatus = result.Status;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!Current()) return;
            _mapProjection = null;
            foreach (var pane in _panes) pane.Canvas.SetSearchMapProjection(null);
            _viewModel.Search.MapStatus = $"Could not filter the map: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_mapRun, run))
            {
                _mapRun = null;
                _viewModel.Search.IsMapBusy = false;
                if (_mapRefreshAgain) ScheduleSearchMap(refresh: true);
            }
            run.Dispose();
        }
    }

    private void SearchBuilderButton_Click(object sender, RoutedEventArgs e)
    {
        var builder = new SearchBuilderWindow(_viewModel.Search.Text, SortFolder()) { Owner = ShellWindow };
        if (builder.ShowOwnerModal())
        {
            _viewModel.Search.Text = builder.QueryText;
            _viewModel.Search.SearchNow();
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

}
