using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// One thing on the shelf as a card: its name and icon on a coloured band, a
/// line of facts, and - for a folder or an archive - the first things inside
/// it, so a glance says what it is.  A copy-path button shows on the band
/// while the pointer is over the card.
/// </summary>
public sealed class ShelfCard : Border
{
    private const int ShownChildren = 8;

    private static readonly Brush FolderBand = Frozen(0xE3, 0xB3, 0x41);
    private static readonly Brush ArchiveBand = Frozen(0xA7, 0x8B, 0xFA);
    private static readonly Brush FileBand = Frozen(0x4F, 0xB3, 0xD9);
    private static readonly Brush CardBrush = Frozen(0x26, 0x28, 0x2C);
    private static readonly Brush HeaderBrush = Frozen(0x30, 0x33, 0x38);
    private static readonly Brush EdgeBrush = Frozen(0x44, 0x47, 0x4D);
    private static readonly Brush SelectedEdge = Frozen(0x60, 0xA5, 0xFA);
    private static readonly Brush DimText = Frozen(0x9A, 0x9E, 0xA6);
    private static readonly Brush CopyHotBrush = Frozen(0x2F, 0x4E, 0x7A);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly StackPanel _children;
    private readonly TextBlock _detail;

    public ShelfCard(ShelfEntry entry, ImageSource? icon)
    {
        Entry = entry;
        IsFolder = Directory.Exists(entry.Path);
        IsArchive = !IsFolder && Services.Archives.ArchiveService.IsArchiveFile(entry.Path);
        Width = CardWidth;
        CornerRadius = new CornerRadius(7);
        Background = CardBrush;
        BorderBrush = EdgeBrush;
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 };

        var band = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 7, 2), Background = IsFolder ? FolderBand : IsArchive ? ArchiveBand : FileBand };
        var image = new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = MiddleTrim(
                System.IO.Path.GetFileName(entry.Path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : entry.Path,
                // Two lines of it: long names wrap rather than lose their middle.
                NameRoom * 2 - 12,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            Margin = new Thickness(7, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.Path
        };
        CopyButton = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Copy path",
            Visibility = Visibility.Hidden,
            Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        CopyButton.MouseEnter += (_, _) => CopyButton.Background = CopyHotBrush;
        CopyButton.MouseLeave += (_, _) => CopyButton.Background = Brushes.Transparent;

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(band, Dock.Left);
        DockPanel.SetDock(image, Dock.Left);
        DockPanel.SetDock(CopyButton, Dock.Right);
        header.Children.Add(band);
        header.Children.Add(image);
        header.Children.Add(CopyButton);
        header.Children.Add(name);

        _detail = new TextBlock { FontSize = 10.5, Foreground = DimText, Margin = new Thickness(10, 3, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _children = new StackPanel { Margin = new Thickness(10, 4, 8, 0) };

        Child = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new Border { Background = HeaderBrush, CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(6, 5, 4, 5), Child = header },
                _detail,
                _children
            }
        };

        MouseEnter += (_, _) => CopyButton.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => CopyButton.Visibility = Visibility.Hidden;
        UpdateDetail(null);
        if (IsFolder || IsArchive)
        {
            _ = LoadChildrenAsync();
        }
    }

    public ShelfEntry Entry { get; set; }

    public const double CardWidth = 210;

    /// <summary>What the name has on the band: the card less the stripe, the icon, the copy button and their margins.</summary>
    private const double NameRoom = CardWidth - 3 - 7 - 16 - 7 - 4 - 22 - 6 - 4 - 4;

    /// <summary>The room a card is laid out with: a folder's grows when what is inside it has been read.</summary>
    public double ReservedHeight => IsFolder || IsArchive ? 210 : 62;

    /// <summary>
    /// A long name cut in the middle, not at the end: files dropped together
    /// often share their start, and only the end tells them apart.
    /// </summary>
    internal static string MiddleTrim(string name, double room, Typeface face, double size, double pixelsPerDip = 1)
    {
        double Width(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, pixelsPerDip).WidthIncludingTrailingWhitespace;
        if (Width(name) <= room)
        {
            return name;
        }

        // As many characters as fit, two fifths from the start and the rest
        // from the end - the end is where names dropped together differ.
        var low = 2;
        var high = name.Length - 1;
        var best = MiddleTrim(name, low);
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var trimmed = MiddleTrim(name, middle);
            if (Width(trimmed) <= room)
            {
                best = trimmed;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    internal static string MiddleTrim(string name, int length = 24)
    {
        if (name.Length <= length)
        {
            return name;
        }

        var head = (length - 1) * 2 / 5;
        return name[..head] + "…" + name[^(length - 1 - head)..];
    }

    public bool IsFolder { get; }

    public bool IsArchive { get; }

    /// <summary>The copy-path button on the card's band.</summary>
    public Border CopyButton { get; }

    public bool IsSelected
    {
        get => BorderBrush == SelectedEdge;
        set
        {
            BorderBrush = value ? SelectedEdge : EdgeBrush;
            BorderThickness = new Thickness(value ? 2 : 1);
            Padding = new Thickness(value ? 0 : 1);
        }
    }

    public double X
    {
        get => Canvas.GetLeft(this);
        set => Canvas.SetLeft(this, value);
    }

    public double Y
    {
        get => Canvas.GetTop(this);
        set => Canvas.SetTop(this, value);
    }

    public Rect Bounds => new(X, Y, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : 60);

    private void UpdateDetail(int? count)
    {
        var age = DateTime.UtcNow - Entry.AddedUtc;
        var left = ShelfStore.KeepFor - age;
        var keeps = left.TotalDays >= 1 ? $"{(int)left.TotalDays} d left" : $"{Math.Max(1, (int)left.TotalHours)} h left";
        string what;
        if (IsFolder || IsArchive)
        {
            what = count is { } items ? $"{(IsArchive ? "archive" : "folder")} · {items:N0} items" : IsArchive ? "archive" : "folder";
        }
        else
        {
            what = File.Exists(Entry.Path) ? ShelfStore.SizeText(new FileInfo(Entry.Path).Length) : "gone";
        }

        _detail.Text = $"{what} · {(Entry.IsCopy ? "copy" : "link")} · {keeps}";
    }

    /// <summary>The first things inside a folder or an archive, read off the window's thread.</summary>
    private async Task LoadChildrenAsync()
    {
        var path = Entry.Path;
        var listing = await Task.Run(() => NestedDirectoryReader.Read(path, CancellationToken.None));
        if (!string.IsNullOrEmpty(listing.ErrorMessage))
        {
            _children.Children.Add(Line("", listing.ErrorMessage));
            return;
        }

        var shown = 0;
        foreach (var folder in listing.Folders.Take(ShownChildren))
        {
            _children.Children.Add(Line(folder.IsArchive ? "" : "", folder.Name));
            shown++;
        }

        foreach (var file in listing.Files.Take(ShownChildren - shown))
        {
            _children.Children.Add(Line("", file.Name));
            shown++;
        }

        var total = listing.Folders.Count + listing.FileCount;
        if (total > shown)
        {
            _children.Children.Add(new TextBlock { Text = $"+{total - shown:N0} more", FontSize = 10.5, Foreground = DimText, Margin = new Thickness(18, 2, 0, 0) });
        }

        UpdateDetail(total);
    }

    private static FrameworkElement Line(string glyph, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 1, 0, 1),
        Children =
        {
            new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 10.5, Foreground = glyph == "" ? FolderBand : DimText, Width = 16, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.Gainsboro, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis }
        }
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The shelf's board: cards lying where they were put, on a surface that
/// pans and zooms like the canvas.  Shift + left drag moves cards about on
/// it; a plain left drag takes them out of the window as files; a drag on
/// the empty board draws a rectangle that selects; the right or middle
/// button pans, the wheel scrolls and Ctrl + wheel zooms; Delete takes the
/// selected cards off the shelf.
/// </summary>
public sealed class ShelfBoard : Grid
{
    private const double MinimumScale = 0.25;
    private const double MaximumScale = 2.5;

    private enum Press
    {
        None,
        MoveCards,
        DragPending,
        Marquee,
        PanPending,
        Pan
    }

    private readonly Canvas _world = new();
    private readonly MatrixTransform _view = new();
    private readonly Rectangle _marquee = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
        Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xA5, 0xFA)),
        StrokeThickness = 1,
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false
    };

    private readonly List<ShelfCard> _cards = [];
    private double _scale = 1;
    private Vector _offset = new(16, 16);
    private Press _press;
    private Point _pressPoint;
    private Point _last;
    private ShelfCard? _pressCard;
    private MouseButton _pressButton;
    private HashSet<ShelfCard> _marqueeBase = [];

    public ShelfBoard()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        _world.RenderTransform = _view;
        Children.Add(_world);
        Children.Add(_marquee);
        ApplyView();
    }

    public IReadOnlyList<ShelfCard> Cards => _cards;

    public IEnumerable<ShelfCard> Selected => _cards.Where(card => card.IsSelected);

    /// <summary>A card was double-clicked, or Enter pressed on the selection.</summary>
    public event Action<ShelfCard>? OpenRequested;

    /// <summary>The copy-path button on a card was pressed.</summary>
    public event Action<string>? CopyPathRequested;

    /// <summary>Delete: these come off the shelf.</summary>
    public event Action<IReadOnlyList<string>>? RemoveRequested;

    /// <summary>Cards were moved: their new places, to keep.</summary>
    public event Action<IReadOnlyList<ShelfCard>>? CardsMoved;

    /// <summary>A right click without a drag: the card under it, or null for the board.</summary>
    public event Action<ShelfCard?, Point>? MenuRequested;

    /// <summary>A plain left drag left the cards: the paths go out as files.  True while the drag runs.</summary>
    public bool IsDraggingOut { get; private set; }

    // ---- cards -------------------------------------------------------------------------

    public void AddCard(ShelfCard card)
    {
        _cards.Add(card);
        _world.Children.Add(card);
    }

    public void RemoveCard(ShelfCard card)
    {
        _cards.Remove(card);
        _world.Children.Remove(card);
    }

    /// <summary>A point on the board, from one on screen.</summary>
    public Point ToWorld(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    /// <summary>Every card in view, as large as fits up to full size.</summary>
    public void FitAll()
    {
        UpdateLayout();
        if (_cards.Count == 0 || ActualWidth <= 0)
        {
            _scale = 1;
            _offset = new Vector(16, 16);
            ApplyView();
            return;
        }

        var bounds = _cards.Select(card => card.Bounds).Aggregate(Rect.Union);
        bounds.Inflate(16, 16);
        _scale = Math.Clamp(Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height), MinimumScale, 1);
        _offset = new Vector(-bounds.X * _scale + Math.Max(0, (ActualWidth - bounds.Width * _scale) / 2), -bounds.Y * _scale);
        ApplyView();
    }

    /// <summary>
    /// Cards dropped together laid out from where they were dropped: in
    /// columns of the board's visible width, each card going to the column
    /// that ends highest - so short file cards and tall folder cards pack
    /// without gaps - and stepping down past any card already lying there.
    /// Nothing ends up on top of anything.
    /// </summary>
    public void PlaceGroup(IReadOnlyList<ShelfCard> group, Point origin)
    {
        if (group.Count == 0)
        {
            return;
        }

        UpdateLayout();
        const double gap = 10;
        var width = ShelfCard.CardWidth;
        var left = ToWorld(new Point(0, 0)).X + gap;
        var right = ToWorld(new Point(Math.Max(ActualWidth, width), 0)).X - gap;
        var x0 = Math.Clamp(origin.X, left, Math.Max(left, right - width));
        var columns = Math.Clamp((int)((right - x0 + gap) / (width + gap)), 1, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(group.Count * 2.0))));
        var obstacles = _cards.Where(card => !group.Contains(card)).Select(card => card.Bounds).ToList();
        var bottoms = Enumerable.Repeat(origin.Y, columns).ToArray();
        foreach (var card in group)
        {
            var column = Array.IndexOf(bottoms, bottoms.Min());
            var x = x0 + column * (width + gap);
            var y = bottoms[column];
            var height = Math.Max(card.ActualHeight, card.ReservedHeight);
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var other in obstacles)
                {
                    if (other.IntersectsWith(new Rect(x, y, width, height + gap)))
                    {
                        y = other.Bottom + gap;
                        moved = true;
                    }
                }
            }

            card.X = x;
            card.Y = y;
            obstacles.Add(new Rect(x, y, width, height));
            bottoms[column] = y + height + gap;
        }
    }

    /// <summary>A free place for a card that has none: below and beside the others, in columns of the board's width.</summary>
    public Point FreePlace()
    {
        const double step = 222;
        var columns = Math.Max(1, (int)((ActualWidth > 0 ? ActualWidth / _scale : 460) / step));
        for (var row = 0; ; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var spot = new Rect(12 + column * step, 12 + row * 150, 210, 140);
                if (!_cards.Any(card => card.Bounds.IntersectsWith(spot)))
                {
                    return spot.TopLeft;
                }
            }
        }
    }

    private void ApplyView() => _view.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);

    private static ShelfCard? CardOf(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is ShelfCard card)
            {
                return card;
            }
        }

        return null;
    }

    private static bool IsInside(object source, DependencyObject container)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    private void SelectOnly(ShelfCard? only)
    {
        foreach (var card in _cards)
        {
            card.IsSelected = ReferenceEquals(card, only);
        }
    }

    // ---- the mouse -----------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_press != Press.None)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var card = CardOf(e.OriginalSource);
        _pressPoint = _last = point;
        _pressCard = card;
        _pressButton = e.ChangedButton;
        e.Handled = true;

        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _press = e.ChangedButton == MouseButton.Middle ? Press.Pan : Press.PanPending;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (card is not null && IsInside(e.OriginalSource, card.CopyButton))
        {
            CopyPathRequested?.Invoke(card.Entry.Path);
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (card is not null)
        {
            if (e.ClickCount == 2)
            {
                OpenRequested?.Invoke(card);
                return;
            }

            if ((modifiers & ModifierKeys.Control) != 0)
            {
                card.IsSelected = !card.IsSelected;
                return;
            }

            if (!card.IsSelected)
            {
                SelectOnly(card);
            }

            // Shift + left moves the cards on the board; a plain left drag takes them out.
            _press = (modifiers & ModifierKeys.Shift) != 0 ? Press.MoveCards : Press.DragPending;
            if (_press == Press.MoveCards)
            {
                Cursor = Cursors.SizeAll;
            }

            CaptureMouse();
            return;
        }

        _marqueeBase = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ? [.. Selected] : [];
        if (_marqueeBase.Count == 0)
        {
            SelectOnly(null);
        }

        _press = Press.Marquee;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var moved = point - _pressPoint;
        var far = Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;
        switch (_press)
        {
            case Press.MoveCards:
                var step = (point - _last) / _scale;
                foreach (var card in Selected)
                {
                    card.X += step.X;
                    card.Y += step.Y;
                }

                break;
            case Press.DragPending when far:
                _press = Press.None;
                ReleaseMouseCapture();
                DragOut();
                break;
            case Press.Marquee when far:
                var rect = new Rect(_pressPoint, point);
                _marquee.Margin = new Thickness(rect.X, rect.Y, 0, 0);
                _marquee.Width = rect.Width;
                _marquee.Height = rect.Height;
                _marquee.Visibility = Visibility.Visible;
                var world = new Rect(ToWorld(rect.TopLeft), ToWorld(rect.BottomRight));
                foreach (var card in _cards)
                {
                    card.IsSelected = _marqueeBase.Contains(card) || card.Bounds.IntersectsWith(world);
                }

                break;
            case Press.PanPending when moved.Length >= 4:
                _press = Press.Pan;
                goto case Press.Pan;
            case Press.Pan:
                _offset += point - _last;
                ApplyView();
                Cursor = Cursors.SizeAll;
                break;
        }

        _last = point;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != _pressButton || _press == Press.None)
        {
            return;
        }

        var press = _press;
        _press = Press.None;
        ReleaseMouseCapture();
        ClearValue(CursorProperty);
        _marquee.Visibility = Visibility.Collapsed;
        e.Handled = true;
        switch (press)
        {
            case Press.MoveCards:
                CardsMoved?.Invoke([.. Selected]);
                break;
            case Press.DragPending when _pressCard is not null:
                // A click on a card of a selection makes it the selection.
                SelectOnly(_pressCard);
                break;
            case Press.PanPending:
                if (_pressCard is not null && !_pressCard.IsSelected)
                {
                    SelectOnly(_pressCard);
                }

                MenuRequested?.Invoke(_pressCard, e.GetPosition(this));
                break;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_press == Press.MoveCards)
        {
            CardsMoved?.Invoke([.. Selected]);
        }

        if (_press != Press.DragPending)
        {
            _press = Press.None;
        }

        _marquee.Visibility = Visibility.Collapsed;
        ClearValue(CursorProperty);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        var point = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta / 120.0), MinimumScale, MaximumScale);
            var anchor = ToWorld(point);
            _scale = scale;
            _offset = new Vector(point.X - anchor.X * scale, point.Y - anchor.Y * scale);
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _offset.X += e.Delta * 0.6;
        }
        else
        {
            _offset.Y += e.Delta * 0.6;
        }

        ApplyView();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Delete when Selected.Any():
                RemoveRequested?.Invoke([.. Selected.Select(card => card.Entry.Path)]);
                e.Handled = true;
                break;
            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                foreach (var card in _cards)
                {
                    card.IsSelected = true;
                }

                e.Handled = true;
                break;
            case Key.Enter when Selected.FirstOrDefault() is { } first:
                OpenRequested?.Invoke(first);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The selected cards out of the window, as the files they stand for.</summary>
    private void DragOut()
    {
        var paths = Selected.Select(card => card.Entry.Path).Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        IsDraggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            IsDraggingOut = false;
        }
    }
}
