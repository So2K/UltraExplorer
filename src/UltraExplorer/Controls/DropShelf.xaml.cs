using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer.Controls;

/// <summary>
/// The shelf at the window's right edge.  It slides out the moment anything
/// is dragged over the window - from the canvas, out of an archive, from
/// Explorer - and slides back once the drag is over, unless it was opened
/// by hand from its tab.  What is on it lies on a board of its own as cards,
/// each where it was dropped or moved to (see <see cref="ShelfBoard"/>); its
/// left edge pulls it wider.
/// </summary>
public partial class DropShelf : UserControl, IDisposable
{
    private readonly DispatcherTimer _dragWatch;
    private ShelfStore? _store;
    private MainViewModel? _viewModel;
    private long _lastDragTicks;
    private bool _isShown;
    private bool _isPinned;
    private bool _fitted;
    private bool _enabled;
    private bool _disposed;
    private int _generation;
    private ContextMenu? _menu;

    public DropShelf()
    {
        InitializeComponent();
        _dragWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnDragWatch, Dispatcher);
        _dragWatch.Stop();
        Board.OpenRequested += OnOpenRequested;
        Board.CopyPathRequested += OnCopyPathRequested;
        Board.RemoveRequested += paths => _store?.Remove(paths);
        Board.CardsMoved += cards => _store?.SetPositions(cards.Select(card => (card.Entry.Path, card.X, card.Y)));
        Board.MenuRequested += OnMenuRequested;
    }

    private double HiddenOffset => Panel.Width + 40;

    /// <summary>Opt-in shelf. Hiding it preserves its entries and stops all card previews.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            Dispatcher.VerifyAccess();
            if (_enabled == value || _disposed) return;
            _enabled = value;
            _generation++;
            if (value)
            {
                Visibility = Visibility.Visible;
                Ready = ActivateAsync();
            }
            else
            {
                _dragWatch.Stop();
                if (_menu is not null) { _menu.IsOpen = false; _menu = null; }
                _isShown = _isPinned = _fitted = false;
                Slide.BeginAnimation(TranslateTransform.XProperty, null);
                Panel.Visibility = Tab.Visibility = DropHighlight.Visibility = Visibility.Collapsed;
                Board.ClearCards();
                Visibility = Visibility.Collapsed;
            }
        }
    }

    internal Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>The window's OLE lifetime and close tracking, shared with its other drop targets.</summary>
    public Func<IDataObject, IReadOnlyList<string>, DragDropEffects, Func<Task<bool>>, bool>? CompleteDrop { get; set; }

    public void Initialize(ShelfStore store, MainViewModel viewModel)
    {
        if (_store is not null) _store.Changed -= OnStoreChanged;
        _store = store;
        _viewModel = viewModel;
        Panel.Width = Math.Max(Panel.MinWidth, store.Width);
        Slide.X = HiddenOffset;
        store.Changed += OnStoreChanged;
        if (Enabled) Ready = ActivateAsync();
    }

    private void OnStoreChanged()
    {
        if (_disposed || !Enabled || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(SyncCards);
    }

    private async Task ActivateAsync()
    {
        var generation = _generation;
        // Cards need no disk IO to be built; an unavailable share must not
        // delay every other saved card while background pruning checks it.
        if (Enabled && !_disposed) SyncCards();
        if (_store is { } store) await Task.Run(store.Prune);
        if (Enabled && !_disposed && generation == _generation) SyncCards();
    }

    public void RefreshCards()
    {
        if (!Enabled || _disposed) return;
        Board.ClearCards();
        SyncCards();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Enabled = false;
        _disposed = true;
        if (_store is not null) _store.Changed -= OnStoreChanged;
        _dragWatch.Stop();
        _dragWatch.Tick -= OnDragWatch;
        CompleteDrop = null;
    }

    /// <summary>A drag is over the window: out it comes, and stays while the drag goes on.</summary>
    public void NotifyDragOver()
    {
        if (!Enabled || _disposed) return;
        _lastDragTicks = Environment.TickCount64;
        if (!_isShown)
        {
            Show();
        }

        _dragWatch.Start();
    }

    /// <summary>Opens or closes the shelf by hand.</summary>
    public void Toggle()
    {
        if (!Enabled || _disposed) return;
        if (_isShown && _isPinned)
        {
            Hide();
            return;
        }

        _isPinned = true;
        Show();
    }

    private void OnDragWatch(object? sender, EventArgs e)
    {
        // Over for a moment now - the drag left the window, or was dropped.
        if (Environment.TickCount64 - _lastDragTicks < 400)
        {
            return;
        }

        DropHighlight.Visibility = Visibility.Collapsed;
        if (!_isPinned && !Board.IsDraggingOut && !Panel.IsMouseOver)
        {
            _dragWatch.Stop();
            Hide();
        }
        else if (_isPinned) _dragWatch.Stop();
    }

    private void Show()
    {
        _isShown = true;
        Panel.Visibility = Visibility.Visible;
        Tab.Visibility = Visibility.Collapsed;
        Ready = ActivateAsync();
        if (!_fitted)
        {
            _fitted = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Board.FitAll);
        }

        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Hide()
    {
        _isShown = false;
        _isPinned = false;
        var away = new DoubleAnimation(HiddenOffset, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            if (!_isShown)
            {
                Panel.Visibility = Visibility.Collapsed;
                UpdateCounts();
            }
        };
        Slide.BeginAnimation(TranslateTransform.XProperty, away);
    }

    /// <summary>
    /// The board brought in line with the shelf: a card for every new item,
    /// gone for every item let go of; a card that never had a place is
    /// given a free one, and kept there.
    /// </summary>
    private void SyncCards()
    {
        if (!Enabled || _disposed || _store is null || _viewModel is null)
        {
            return;
        }

        var entries = _store.Entries;
        var byPath = entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var card in Board.Cards.ToList())
        {
            if (!byPath.TryGetValue(card.Entry.Path, out var now))
            {
                Board.RemoveCard(card);
                continue;
            }

            card.Entry = now;
            if (now.HasPosition)
            {
                card.X = now.X;
                card.Y = now.Y;
            }
        }

        var placed = new List<ShelfCard>();
        foreach (var entry in entries)
        {
            if (Board.Cards.Any(card => string.Equals(card.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var card = new ShelfCard(entry, null, _viewModel.BrowseArchives);
            if (entry.HasPosition)
            {
                card.X = entry.X;
                card.Y = entry.Y;
            }
            else
            {
                var spot = Board.FreePlace();
                card.X = spot.X;
                card.Y = spot.Y;
                placed.Add(card);
            }

            Board.AddCard(card);
        }

        if (placed.Count > 0)
        {
            _store.SetPositions(placed.Select(card => (card.Entry.Path, card.X, card.Y)));
        }

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var count = _store?.Count ?? 0;
        TabCount.Text = count.ToString();
        HeaderCount.Text = count == 0 ? string.Empty : count.ToString();
        EmptyNote.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Tab.Visibility = !_isShown && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- putting things on the shelf -----------------------------------------------------

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        if (!Enabled) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        NotifyDragOver();
        e.Handled = true;
        if (!Board.IsDraggingOut && TryGetPaths(e.Data, reportError: false, out _))
        {
            e.Effects = ShelfEffect(e.AllowedEffects);
            DropHighlight.Visibility = e.Effects == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Panel_DragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropHighlight.Visibility = Visibility.Collapsed;
        var effect = ShelfEffect(e.AllowedEffects);
        e.Effects = DragDropEffects.None;
        if (!Enabled || Board.IsDraggingOut || _store is null || effect == DragDropEffects.None || !TryGetPaths(e.Data, reportError: true, out var paths))
        {
            return;
        }

        _isPinned = true;
        var at = Board.ToWorld(e.GetPosition(Board));
        bool done = CompleteDrop is { } complete
            ? complete(e.Data, paths, effect, () => PutOnShelfAsync(paths, at))
            : ExternalFileDrop.Complete(Dispatcher, () => PutOnShelfAsync(paths, at));
        e.Effects = done ? effect : DragDropEffects.None;
    }

    private bool TryGetPaths(IDataObject data, bool reportError, out string[] paths)
    {
        paths = [];
        try
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } found) return false;
            paths = found;
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (reportError && Enabled && !_disposed) _viewModel?.Toast.ShowError($"Could not read dropped files: {ex.Message}");
            return false;
        }
    }

    internal static DragDropEffects ShelfEffect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
        : (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private async Task<bool> PutOnShelfAsync(string[] paths, Point at)
    {
        if (_store is null) return false;
        try
        {
            // Every temporary input must be durable before releasing the OLE
            // source. The shared helper normally observes one Shell batch;
            // this shelf may copy several inputs into separate owned folders.
            var observer = NativeShellService.CopyStarted.Value;
            Task<(int Added, IReadOnlyList<string> Paths)> transfer;
            try
            {
                NativeShellService.CopyStarted.Value = null;
                transfer = _store.AddAsync(paths);
            }
            finally { NativeShellService.CopyStarted.Value = observer; }
            var (added, placed) = await transfer;
            if (!Enabled || _disposed) return placed.Count > 0;

            // The cards made now, then laid out together from where they were dropped.
            SyncCards();
            var group = placed
                .Select(path => Board.Cards.FirstOrDefault(card => string.Equals(card.Entry.Path, path, StringComparison.OrdinalIgnoreCase)))
                .OfType<ShelfCard>()
                .ToList();
            Board.PlaceGroup(group, new Point(at.X - 20, at.Y - 14));
            _store.SetPositions(group.Select(card => (card.Entry.Path, card.X, card.Y)));
            _ = _viewModel?.Toast.ShowSuccessAsync(added == 0
                ? "Moved on the shelf"
                : $"Put {added} item(s) on the shelf — kept for 7 days");
            return placed.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (Enabled && !_disposed) _viewModel?.Toast.ShowError($"Could not put it on the shelf: {ex.Message}");
            return false;
        }
    }

    // ---- what the board asks for -------------------------------------------------------

    private async void OnOpenRequested(ShelfCard card)
    {
        await card.MetadataReady;
        if (!Enabled || _disposed || !Board.Cards.Contains(card)) return;
        // A folder or an archive is gone to on the canvas; a file opens.
        if ((card.IsFolder || card.IsArchive) && _viewModel is not null)
        {
            _ = _viewModel.Tree.RevealPathAsync(card.Entry.Path);
            return;
        }

        try
        {
            NativeShellService.Open(card.Entry.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _viewModel?.Toast.ShowError(ex.Message);
        }
    }

    private void OnCopyPathRequested(string path)
    {
        if (!Enabled || _disposed) return;
        try
        {
            Clipboard.SetText(path);
            _ = _viewModel?.Toast.ShowSuccessAsync($"Copied: {path}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _viewModel?.Toast.ShowError("Another application is holding the clipboard — try again.");
        }
    }

    private void OnMenuRequested(ShelfCard? card, Point point)
    {
        if (!Enabled || _disposed || _store is null)
        {
            return;
        }

        if (_menu is not null) _menu.IsOpen = false;
        var menu = _menu = new ContextMenu { PlacementTarget = Board };
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        if (card is not null)
        {
            var selected = Board.Selected.Select(chosen => chosen.Entry.Path).ToArray();
            Add(menu, card.IsFolder || card.IsArchive ? "Show on the canvas" : "Open", "", () => OnOpenRequested(card));
            Add(menu, "Show in File Explorer", "", () => NativeShellService.ShowInExplorer(card.Entry.Path));
            Add(menu, "Copy path", "", () => OnCopyPathRequested(card.Entry.Path));
            menu.Items.Add(new Separator());
            Add(menu, selected.Length > 1 ? $"Remove {selected.Length} from the shelf" : "Remove from the shelf", "", () => _store.Remove(selected));
            var left = ShelfStore.KeepFor - (DateTime.UtcNow - card.Entry.AddedUtc);
            menu.Items.Add(new MenuItem
            {
                Header = $"{(card.Entry.IsCopy ? "A copy" : "A link")} · {Math.Max(0, (int)left.TotalDays)} d {Math.Max(0, left.Hours)} h left",
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
        }

        Add(menu, "Show every card", "", Board.FitAll);
        Add(menu, "Clear the shelf", "", () => _store.Clear());
        menu.IsOpen = true;
    }

    private void Add(ContextMenu menu, string header, string glyph, Action run)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 }
        };
        item.Click += (_, _) => { if (Enabled && !_disposed) run(); };
        menu.Items.Add(item);
    }

    // ---- the panel itself ---------------------------------------------------------------

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var room = (Parent as FrameworkElement)?.ActualWidth ?? ActualWidth;
        Panel.Width = Math.Clamp(Panel.Width - e.HorizontalChange, Panel.MinWidth, Math.Max(Panel.MinWidth, room - 60));
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e) => _store?.SetWidth(Panel.Width);

    private void Fit_Click(object sender, RoutedEventArgs e) => Board.FitAll();

    private void Clear_Click(object sender, RoutedEventArgs e) { if (Enabled && !_disposed) _store?.Clear(); }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Tab_Click(object sender, MouseButtonEventArgs e) => Toggle();

    private void Tab_DragEnter(object sender, DragEventArgs e) => NotifyDragOver();
}
