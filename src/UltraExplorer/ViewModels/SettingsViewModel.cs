using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Updates;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.ViewModels;

/// <summary>
/// What the Settings window shows and changes.  It keeps no setting of its
/// own: each one reads and writes the very property the Canvas options menu,
/// the More menu and the column headers use, and their change notices are
/// passed on, so a choice made in any of them shows in all the others at
/// once.  Every change applies, and is remembered, the moment it is made, as
/// in Windows' own Settings - there is nothing to confirm or cancel.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] LayoutProperties =
        [nameof(IsNestedLayout), nameof(IsTreeLayout), nameof(CanShowMinimap), nameof(MinimapDescription)];

    private static readonly string[] SplitProperties =
        [nameof(IsSplit), nameof(IsSplitSideBySide), nameof(IsSplitStacked)];

    private static readonly string[] RendererProperties =
        [nameof(IsRendererAuto), nameof(IsRendererGpu), nameof(IsRendererCpu), nameof(CanChooseRenderer), nameof(RendererDescription)];

    private static readonly string[] OrderProperties =
    [
        nameof(DefaultColumnIndex), nameof(IsAscending), nameof(IsDescending),
        nameof(IsPerFolder), nameof(IsAllFolders), nameof(IsDownThenAcross), nameof(IsAcrossThenDown),
        nameof(OwnOrdersTitle), nameof(OwnOrdersDescription), nameof(CanResetOwnOrders)
    ];

    private static readonly string[] LeftDragProperties =
        [nameof(IsLeftDragSelect), nameof(IsLeftDragPan), nameof(LeftDragDescription)];

    private static readonly string[] LayerProperties =
        [nameof(ShowFiles), nameof(ShowIcons), nameof(ShowDetails), nameof(ShowFolderCounts), nameof(ShowMarks), nameof(CanShowAllLayers)];

    private readonly MainViewModel _main;
    private readonly Func<string>? _rendererNow;
    private readonly Func<string, string, string, bool> _confirm;
    private readonly Action<string> _openFolder;
    private readonly RelayCommand _resetOwnOrdersCommand;
    private readonly RelayCommand _showAllLayersCommand;
    private string _rendererStatus = string.Empty;
    private bool? _hiddenWanted;
    private bool _isApplyingHidden;
    private bool _isDisposed;
    private bool? _dialogWanted;
    private bool _applyingDialogs;
    private bool? _winEWanted;
    private bool _applyingWinE;
    private readonly DialogIntegrationController _dialogIntegration = DialogIntegrationController.Shared;
    private readonly QuietUpdateService _quietUpdates;
    private readonly System.Windows.Threading.Dispatcher _settingsDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <param name="main">The window's view model, whose settings these are.</param>
    /// <param name="canChooseLayout">False in a file dialog, which keeps the tree canvas whatever is chosen.</param>
    /// <param name="rendererNow">What draws the nested canvas at this moment, in words (see <see cref="DescribeRendererNow"/>).</param>
    /// <param name="confirm">Asks before something is undone for good: title, message, the confirming button's label.</param>
    /// <param name="openFolder">Opens a folder in Windows Explorer; Explorer itself when left out.</param>
    internal SettingsViewModel(
        MainViewModel main,
        bool canChooseLayout,
        Func<string>? rendererNow,
        Func<string, string, string, bool> confirm,
        Action<string>? openFolder = null,
        QuietUpdateService? quietUpdates = null)
    {
        _main = main;
        _quietUpdates = quietUpdates ?? QuietUpdateService.Shared;
        CanChooseLayout = canChooseLayout;
        _rendererNow = rendererNow;
        _confirm = confirm;
        _openFolder = openFolder ?? NativeShellService.ShowInExplorer;
        _resetOwnOrdersCommand = new RelayCommand(ResetOwnOrders, () => CanResetOwnOrders);
        _showAllLayersCommand = new RelayCommand(() => _main.Layers = CanvasLayer.All, () => CanShowAllLayers);
        OpenInstallFolderCommand = new RelayCommand(() => OpenFolder(InstallFolder));
        OpenStateFolderCommand = new RelayCommand(() => OpenFolder(StateFolder));
        RecoverDialogIntegrationCommand = new AsyncRelayCommand(() => DialogActionAsync(_dialogIntegration.RecoverAsync));
        ResetDialogExceptionsCommand = new AsyncRelayCommand(() => DialogActionAsync(_dialogIntegration.ResetExclusionsAsync));
        _dialogIntegration.Changed += OnDialogIntegrationChanged;
        _quietUpdates.SnapshotChanged += OnQuietUpdatesChanged;
        _rendererStatus = _rendererNow?.Invoke() ?? string.Empty;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Tree.PropertyChanged += OnTreePropertyChanged;
        Orders.Changed += OnOrdersChanged;
        GpuBootstrap.PreferenceChanged += OnRendererPreferenceChanged;
    }

    private FolderOrders Orders => _main.Orders;

    public bool ReceiveUpdates
    {
        get => _quietUpdates.Enabled;
        set
        {
            if (_quietUpdates.Enabled == value) return;
            _quietUpdates.Enabled = value;
            OnPropertyChanged();
        }
    }

    private void OnQuietUpdatesChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || _settingsDispatcher.HasShutdownStarted) return;
        if (_settingsDispatcher.CheckAccess()) OnPropertyChanged(nameof(ReceiveUpdates));
        else _ = _settingsDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!_isDisposed) OnPropertyChanged(nameof(ReceiveUpdates));
        });
    }

    public bool IsDialogReplacementEnabled
    {
        get => _dialogWanted ?? _dialogIntegration.Enabled;
        set
        {
            if (value == IsDialogReplacementEnabled) return;
            _dialogWanted = value;
            OnPropertyChanged();
            if (!_applyingDialogs) _ = ApplyDialogIntegrationAsync();
        }
    }
    public string DialogIntegrationStatus => _dialogIntegration.Status;
    public bool IsWinEShortcutEnabled
    {
        get => _winEWanted ?? _dialogIntegration.WinEEnabled;
        set
        {
            if (value == IsWinEShortcutEnabled) return;
            _winEWanted = value;
            OnPropertyChanged();
            if (!_applyingWinE) _ = ApplyWinEShortcutAsync();
        }
    }
    public string WinEShortcutStatus => _dialogIntegration.WinEStatus;
    public string DialogExceptionsDescription => _dialogIntegration.ExclusionCount == 0
        ? "Every supported application can use UltraExplorer."
        : $"{_dialogIntegration.ExclusionCount} application(s) keep their Windows dialogs. Reset to try them again.";
    public ICommand RecoverDialogIntegrationCommand { get; }
    public ICommand ResetDialogExceptionsCommand { get; }
    private async Task ApplyDialogIntegrationAsync()
    {
        _applyingDialogs = true;
        try
        {
            bool? applied = null;
            while (_dialogWanted is { } wanted && wanted != applied)
            { applied = wanted; await _dialogIntegration.SetEnabledAsync(wanted); }
        }
        catch (Exception ex) { _main.Toast.ShowError(ex.Message); }
        finally { _dialogWanted = null; _applyingDialogs = false; OnDialogIntegrationChanged(); }
    }
    private async Task ApplyWinEShortcutAsync()
    {
        _applyingWinE = true;
        try
        {
            bool? applied = null;
            while (_winEWanted is { } wanted && wanted != applied)
            { applied = wanted; await _dialogIntegration.SetWinEEnabledAsync(wanted); }
        }
        catch (Exception ex) { _main.Toast.ShowError(ex.Message); }
        finally { _winEWanted = null; _applyingWinE = false; OnDialogIntegrationChanged(); }
    }
    private async Task DialogActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _main.Toast.ShowError(ex.Message); }
    }
    private void OnDialogIntegrationChanged()
    {
        OnPropertyChanged(nameof(IsDialogReplacementEnabled));
        OnPropertyChanged(nameof(DialogIntegrationStatus));
        OnPropertyChanged(nameof(IsWinEShortcutEnabled));
        OnPropertyChanged(nameof(WinEShortcutStatus));
        OnPropertyChanged(nameof(DialogExceptionsDescription));
    }

    // ---- Canvas ----------------------------------------------------------------

    /// <summary>Whether the picture of the drives can be switched here; not in a file dialog.</summary>
    public bool CanChooseLayout { get; }

    /// <summary>
    /// The line over the page.  A file dialog writes back none of the sorting
    /// and order, the renderer or left drag (see <see cref="MainViewModel.SaveNowAsync"/>),
    /// so there it does not promise that they are remembered.
    /// </summary>
    public string Intro => CanChooseLayout
        ? "Every change applies at once and is remembered."
        : "Every change applies at once. A file dialog does not remember sorting and order, layers, the renderer or left drag.";

    public bool IsNestedLayout
    {
        get => _main.IsNestedLayout;
        set
        {
            if (value)
            {
                _main.Layout = CanvasLayout.Nested;
            }
        }
    }

    public bool ShowFavoriteLinks
    {
        get => _main.ShowFavoriteLinks;
        set => _main.ShowFavoriteLinks = value;
    }

    public bool ShowHoverPreviews
    {
        get => _main.ShowHoverPreviews;
        set => _main.ShowHoverPreviews = value;
    }

    public bool BrowseArchives { get => _main.BrowseArchives; set => _main.BrowseArchives = value; }
    public bool ShowDropShelf { get => _main.ShowDropShelf; set => _main.ShowDropShelf = value; }
    public bool ShowCopyPathButton { get => _main.ShowCopyPathButton; set => _main.ShowCopyPathButton = value; }

    public bool IsTreeLayout
    {
        get => _main.IsTreeLayout;
        set
        {
            if (value)
            {
                _main.Layout = CanvasLayout.Tree;
            }
        }
    }

    /// <summary>
    /// The split view: two panes of the nested canvas, each at its own place
    /// on the disk - the very switch Ctrl+\, the command bar's split button
    /// and Canvas options turn.  Not in a file dialog, which never splits.
    /// </summary>
    public bool IsSplit
    {
        get => _main.IsSplit;
        set => _main.IsSplit = value;
    }

    /// <summary>The panes side by side; choosing it splits the view if it is not.</summary>
    public bool IsSplitSideBySide
    {
        get => _main.SplitOrientation == SplitOrientation.SideBySide;
        set => ChooseSplit(value, SplitOrientation.SideBySide);
    }

    /// <summary>The panes one above the other; choosing it splits the view if it is not.</summary>
    public bool IsSplitStacked
    {
        get => _main.SplitOrientation == SplitOrientation.Stacked;
        set => ChooseSplit(value, SplitOrientation.Stacked);
    }

    private void ChooseSplit(bool chosen, SplitOrientation orientation)
    {
        if (chosen && CanChooseLayout)
        {
            _main.SplitOrientation = orientation;
            _main.IsSplit = true;
        }
    }

    /// <summary>The renderer in force: the command line or the environment variable when either chose, else the setting.</summary>
    private RendererPreference Renderer => GpuBootstrap.ExplicitPreference ?? _main.Renderer;

    public bool IsRendererAuto
    {
        get => Renderer == RendererPreference.Auto;
        set => ChooseRenderer(value, RendererPreference.Auto);
    }

    public bool IsRendererGpu
    {
        get => Renderer == RendererPreference.Gpu;
        set => ChooseRenderer(value, RendererPreference.Gpu);
    }

    public bool IsRendererCpu
    {
        get => Renderer == RendererPreference.Cpu;
        set => ChooseRenderer(value, RendererPreference.Cpu);
    }

    /// <summary>False while <c>--renderer</c> or ULTRAEXPLORER_RENDERER chose for this run, which wins over the setting.</summary>
    public bool CanChooseRenderer => GpuBootstrap.ExplicitPreference is null;

    public string RendererDescription => CanChooseRenderer
        ? "What draws the nested canvas. Automatic uses the graphics card wherever it can."
        : "Chosen for this run by --renderer or ULTRAEXPLORER_RENDERER, which win over this setting.";

    /// <summary>What draws the nested canvas now, and why when it is not the graphics card.</summary>
    public string RendererStatus => _rendererStatus;

    /// <summary>Asks the canvas again what draws it; the window does this every second while it is open.</summary>
    public void RefreshRendererStatus()
    {
        var now = _rendererNow?.Invoke() ?? string.Empty;
        if (now != _rendererStatus)
        {
            _rendererStatus = now;
            OnPropertyChanged(nameof(RendererStatus));
        }
    }

    /// <summary>
    /// Explorer's hidden items.  Reading the folders again takes a moment, and
    /// the switch shows the new choice from the click on rather than jumping
    /// back until the reading is done.
    /// </summary>
    public bool ShowHiddenItems
    {
        get => _hiddenWanted ?? _main.Tree.ShowHiddenItems;
        set
        {
            if (value == ShowHiddenItems)
            {
                return;
            }

            _hiddenWanted = value;
            OnPropertyChanged();
            if (!_isApplyingHidden)
            {
                _ = ApplyHiddenItemsAsync();
            }
        }
    }

    /// <summary>
    /// Reads the folders again for the switch, one reading at a time, as the
    /// menu's command does: a second click while the first reading is under
    /// way waits for it and is then applied, rather than starting a second
    /// reading of the same folders alongside the first.
    /// </summary>
    private async Task ApplyHiddenItemsAsync()
    {
        _isApplyingHidden = true;
        try
        {
            bool? applied = null;
            while (_hiddenWanted is { } wanted && wanted != applied)
            {
                applied = wanted;
                await _main.Tree.SetShowHiddenItemsAsync(wanted);
            }
        }
        finally
        {
            _isApplyingHidden = false;
            _hiddenWanted = null;
            OnPropertyChanged(nameof(ShowHiddenItems));
        }
    }

    /// <summary>
    /// The tree canvas's minimap.  The window itself writes it down only as it
    /// closes; this page promises that a change is remembered as it is made,
    /// so it has the workspace written at once, as the other settings do.
    /// </summary>
    public bool IsMinimapVisible
    {
        get => _main.IsMinimapVisible;
        set
        {
            if (value == _main.IsMinimapVisible)
            {
                return;
            }

            _main.IsMinimapVisible = value;
            _ = _main.SaveNowAsync();
        }
    }

    /// <summary>The minimap draws the tree's nodes; the nested canvas is its own overview.</summary>
    public bool CanShowMinimap => _main.IsTreeLayout;

    public string MinimapDescription => CanShowMinimap
        ? "A small map of the whole tree in the corner of the canvas."
        : "Only on the tree canvas: the nested canvas is its own overview.";

    // ---- Layers ----------------------------------------------------------------
    //
    // The switches of the layers menu, each reading and writing the window's
    // own setting (MainViewModel.Layers); hidden items, shown among them, are
    // ShowHiddenItems above.

    public bool ShowFiles
    {
        get => _main.IsLayerShown(CanvasLayer.Files);
        set => _main.SetLayer(CanvasLayer.Files, value);
    }

    public bool ShowIcons
    {
        get => _main.IsLayerShown(CanvasLayer.Icons);
        set => _main.SetLayer(CanvasLayer.Icons, value);
    }

    public bool ShowDetails
    {
        get => _main.IsLayerShown(CanvasLayer.Details);
        set => _main.SetLayer(CanvasLayer.Details, value);
    }

    public bool ShowFolderCounts
    {
        get => _main.IsLayerShown(CanvasLayer.FolderCounts);
        set => _main.SetLayer(CanvasLayer.FolderCounts, value);
    }

    public bool ShowMarks
    {
        get => _main.IsLayerShown(CanvasLayer.Marks);
        set => _main.SetLayer(CanvasLayer.Marks, value);
    }

    /// <summary>Whether any layer is switched off, for Show all.</summary>
    public bool CanShowAllLayers => _main.Layers != CanvasLayer.All;

    /// <summary>Every layer back on, as the layers menu's Show all layers does; hidden items stay as they are.</summary>
    public ICommand ShowAllLayersCommand => _showAllLayersCommand;

    // ---- Sorting and order -----------------------------------------------------------

    /// <summary>The columns an order can be by, in <see cref="SortColumn"/> order, as the headers name them.</summary>
    public IReadOnlyList<string> SortColumns { get; } = [.. Enum.GetValues<SortColumn>().Select(ItemSort.Describe)];

    /// <summary>
    /// The default order's column, as an index into <see cref="SortColumns"/>.
    /// Another column starts its own way round - names and types from A,
    /// dates and sizes from the newest and largest - as a header click would.
    /// </summary>
    public int DefaultColumnIndex
    {
        get => (int)Orders.Default.Column;
        set
        {
            if (value < 0 || value >= SortColumns.Count || value == DefaultColumnIndex)
            {
                return;
            }

            Orders.SetDefault(Orders.Default.Click((SortColumn)value));
        }
    }

    public bool IsAscending
    {
        get => !Orders.Default.Descending;
        set
        {
            if (value)
            {
                Orders.SetDefault(Orders.Default with { Descending = false });
            }
        }
    }

    public bool IsDescending
    {
        get => Orders.Default.Descending;
        set
        {
            if (value)
            {
                Orders.SetDefault(Orders.Default with { Descending = true });
            }
        }
    }

    public bool IsPerFolder
    {
        get => Orders.Scope == SortScope.PerFolder;
        set
        {
            if (value)
            {
                Orders.Scope = SortScope.PerFolder;
            }
        }
    }

    public bool IsAllFolders
    {
        get => Orders.Scope == SortScope.AllFolders;
        set
        {
            if (value)
            {
                Orders.Scope = SortScope.AllFolders;
            }
        }
    }

    public bool IsDownThenAcross
    {
        get => Orders.Flow == LayoutOrder.DownThenAcross;
        set
        {
            if (value)
            {
                Orders.Flow = LayoutOrder.DownThenAcross;
            }
        }
    }

    public bool IsAcrossThenDown
    {
        get => Orders.Flow == LayoutOrder.AcrossThenDown;
        set
        {
            if (value)
            {
                Orders.Flow = LayoutOrder.AcrossThenDown;
            }
        }
    }

    public string OwnOrdersTitle => $"Folders with their own order: {Orders.Count.ToString("N0", CultureInfo.CurrentCulture)}";

    public string OwnOrdersDescription => Orders.Count > 0 && Orders.Scope == SortScope.AllFolders
        ? "Kept, but not used while all folders are sorted the same."
        : "Folders sorted on their own with a header or Sort by keep that order.";

    public bool CanResetOwnOrders => Orders.Count > 0;

    /// <summary>Lets go of every folder's own order, after asking: they all follow the default order again.</summary>
    public ICommand ResetOwnOrdersCommand => _resetOwnOrdersCommand;

    private void ResetOwnOrders()
    {
        var count = Orders.Count;
        if (count == 0)
        {
            return;
        }

        var folders = count == 1
            ? "The one folder sorted on its own"
            : $"All {count.ToString("N0", CultureInfo.CurrentCulture)} folders sorted on their own";
        var message = $"{folders} will be shown in the default order, "
            + $"{ItemSort.Describe(Orders.Default.Column)}, {ItemSort.DescribeDirection(Orders.Default.Column, Orders.Default.Descending)}. "
            + "This cannot be undone.";
        if (_confirm("Reset every folder's order?", message, "Reset all"))
        {
            Orders.UseEverywhere(Orders.Default);
        }
    }

    // ---- Mouse -----------------------------------------------------------------

    public bool IsLeftDragSelect
    {
        get => _main.LeftDrag == NestedLeftDrag.SelectArea;
        set
        {
            if (value)
            {
                _main.LeftDrag = NestedLeftDrag.SelectArea;
            }
        }
    }

    public bool IsLeftDragPan
    {
        get => _main.LeftDrag == NestedLeftDrag.Pan;
        set
        {
            if (value)
            {
                _main.LeftDrag = NestedLeftDrag.Pan;
            }
        }
    }

    public string LeftDragDescription => IsLeftDragPan
        ? "On the nested canvas. Shift+drag selects an area."
        : "On the nested canvas. The right or middle button, Space+drag and the wheel move it.";

    // ---- About -----------------------------------------------------------------

    /// <summary>The version this build calls itself, the commit it was built from and the day it was built.</summary>
    public string Version { get; } = DescribeVersion();

    /// <summary>The folder the running program is in.</summary>
    public string InstallFolder { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

    /// <summary>Where the workspace, the orders, colours and notes, and the caches are kept.</summary>
    public string StateFolder { get; } = AppPaths.StateDirectory;

    public ICommand OpenInstallFolderCommand { get; }

    public ICommand OpenStateFolderCommand { get; }

    private void OpenFolder(string folder)
    {
        try
        {
            // The state folder is made at the first save; one opened before
            // that would send Explorer to Documents instead.
            Directory.CreateDirectory(folder);
            _openFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _main.Toast.ShowError(ex.Message);
        }
    }

    private static string DescribeVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // The SDK appends the commit as "+<sha>"; seven characters name it.
        var text = informational;
        if (informational.IndexOf('+') is var plus and >= 0)
        {
            var commit = informational[(plus + 1)..];
            text = $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
        }

        // The running exe, not the assembly: a single-file build has no
        // assembly file of its own to date.
        var file = Environment.ProcessPath;
        if (file is not null && File.Exists(file))
        {
            text += ", built " + File.GetLastWriteTime(file).ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        return "Version " + text;
    }

    /// <summary>
    /// What draws the nested canvas at this moment, as the Renderer setting
    /// shows it: the graphics card by name, or the processor and why.
    /// </summary>
    internal static string DescribeRendererNow(bool isNestedShowing, bool isOnGpu, string adapter, string reason)
    {
        if (!isNestedShowing)
        {
            return "Now: not in use, the tree canvas is showing";
        }

        if (isOnGpu)
        {
            return string.IsNullOrWhiteSpace(adapter) ? "Now: graphics card" : $"Now: graphics card ({adapter.Trim()})";
        }

        var why = reason switch
        {
            GpuBootstrap.ReasonCpuChosen => "as chosen",
            GpuBootstrap.ReasonRemoteSession => "remote session",
            GpuBootstrap.ReasonSoftwareRendering => "Windows draws this window in software",
            GpuBootstrap.ReasonRenderTier => "the display driver offers no acceleration",
            GpuBootstrap.ReasonLostTooOften => "the graphics card failed three times; restart to try it again",
            GpuBootstrap.ReasonWarmingUp => "the graphics card is still being prepared",
            GpuBootstrap.ReasonNotOnScreen => "the canvas is not on screen yet",
            GpuBootstrap.ReasonUnavailable => "the graphics card for this monitor cannot be used",
            GpuBootstrap.ReasonReady => "moving to the graphics card",
            _ => reason
        };

        return $"Now: processor — {why}";
    }

    // ---- Keeping up with the menus and the headers -----------------------------------

    private void ChooseRenderer(bool chosen, RendererPreference preference)
    {
        if (chosen && CanChooseRenderer)
        {
            _main.Renderer = preference;
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Layout):
            case nameof(MainViewModel.IsNestedLayout):
            case nameof(MainViewModel.IsTreeLayout):
                Raise(LayoutProperties);
                RefreshRendererStatus();
                break;
            case nameof(MainViewModel.IsSplit):
            case nameof(MainViewModel.SplitOrientation):
                Raise(SplitProperties);
                break;
            case nameof(MainViewModel.Renderer):
                Raise(RendererProperties);
                RefreshRendererStatus();
                break;
            case nameof(MainViewModel.IsMinimapVisible):
                OnPropertyChanged(nameof(IsMinimapVisible));
                break;
            case nameof(MainViewModel.LeftDrag):
                Raise(LeftDragProperties);
                break;
            case nameof(MainViewModel.ShowFavoriteLinks):
                OnPropertyChanged(nameof(ShowFavoriteLinks));
                break;
            case nameof(MainViewModel.ShowHoverPreviews):
                OnPropertyChanged(nameof(ShowHoverPreviews));
                break;
            case nameof(MainViewModel.BrowseArchives):
            case nameof(MainViewModel.ShowDropShelf):
            case nameof(MainViewModel.ShowCopyPathButton):
                OnPropertyChanged(e.PropertyName);
                break;
            case nameof(MainViewModel.Layers):
                Raise(LayerProperties);
                _showAllLayersCommand.RaiseCanExecuteChanged();
                break;
        }
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewAllViewModel.ShowHiddenItems) && _hiddenWanted is null)
        {
            OnPropertyChanged(nameof(ShowHiddenItems));
        }
    }

    private void OnOrdersChanged(string? folder)
    {
        Raise(OrderProperties);
        _resetOwnOrdersCommand.RaiseCanExecuteChanged();
    }

    private void OnRendererPreferenceChanged(object? sender, EventArgs e) => Raise(RendererProperties);

    private void Raise(string[] names)
    {
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _dialogIntegration.Changed -= OnDialogIntegrationChanged;
        _quietUpdates.SnapshotChanged -= OnQuietUpdatesChanged;
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.Tree.PropertyChanged -= OnTreePropertyChanged;
        Orders.Changed -= OnOrdersChanged;
        GpuBootstrap.PreferenceChanged -= OnRendererPreferenceChanged;
    }
}
