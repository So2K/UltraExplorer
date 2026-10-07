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
public partial class DropShelf : UserControl
{
    private readonly DispatcherTimer _dragWatch;
    private ShelfStore? _store;
    private MainViewModel? _viewModel;
    private long _lastDragTicks;
    private bool _isShown;
    private bool _isPinned;
    private bool _fitted;

    public DropShelf()
    {
        InitializeComponent();
        _dragWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnDragWatch, Dispatcher);
        Board.OpenRequested += OnOpenRequested;
        Board.CopyPathRequested += OnCopyPathRequested;
        Board.RemoveRequested += paths => _store?.Remove(paths);
        Board.CardsMoved += cards => _store?.SetPositions(cards.Select(card => (card.Entry.Path, card.X, card.Y)));
        Board.MenuRequested += OnMenuRequested;
    }

    private double HiddenOffset => Panel.Width + 40;

    public void Initialize(ShelfStore store, MainViewModel viewModel)
    {
        _store = store;
        _viewModel = viewModel;
        Panel.Width = Math.Max(Panel.MinWidth, store.Width);
        Slide.X = HiddenOffset;
        store.Prune();
        store.Changed += () => Dispatcher.BeginInvoke(SyncCards);
        SyncCards();
    }

    /// <summary>A drag is over the window: out it comes, and stays while the drag goes on.</summary>
    public void NotifyDragOver()
    {
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

        _dragWatch.Stop();
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!_isPinned && !Board.IsDraggingOut && !Panel.IsMouseOver)
        {
            Hide();
        }
    }

    private void Show()
    {
        _isShown = true;
        Panel.Visibility = Visibility.Visible;
        Tab.Visibility = Visibility.Collapsed;
        _store?.Prune();
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
        if (_store is null || _viewModel is null)
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

            var card = new ShelfCard(entry, SafeIcon(entry.Path));
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

    private ImageSource? SafeIcon(string path)
    {
        try
        {
            return _viewModel?.Icons.GetSmallIcon(path, Directory.Exists(path));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException)
        {
            return null;
        }
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
        NotifyDragOver();
        e.Handled = true;
        if (!Board.IsDraggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 })
        {
            e.Effects = (DragDropEffects.Link | DragDropEffects.Copy) & e.AllowedEffects;
            if (e.Effects == DragDropEffects.None)
            {
                e.Effects = DragDropEffects.Copy;
            }

            DropHighlight.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Panel_DragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropHighlight.Visibility = Visibility.Collapsed;
        if (Board.IsDraggingOut || _store is null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        _isPinned = true;
        var at = Board.ToWorld(e.GetPosition(Board));
        try
        {
            var (added, placed) = await _store.AddAsync(paths);

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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            _viewModel?.Toast.ShowError($"Could not put it on the shelf: {ex.Message}");
        }
    }

    // ---- what the board asks for -------------------------------------------------------

    private void OnOpenRequested(ShelfCard card)
    {
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
        if (_store is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = Board };
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

    private static void Add(ContextMenu menu, string header, string glyph, Action run)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 }
        };
        item.Click += (_, _) => run();
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

    private void Clear_Click(object sender, RoutedEventArgs e) => _store?.Clear();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Tab_Click(object sender, MouseButtonEventArgs e) => Toggle();

    private void Tab_DragEnter(object sender, DragEventArgs e) => NotifyDragOver();
}
