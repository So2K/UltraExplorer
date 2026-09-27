using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// What WPF draws over the cells whichever renderer drew them: the
// selection and the drop target, the pointer's outline and tag, the
// selection rectangle being drawn, the beacons with their arrows at the
// edge, and the trail of folders the view is inside.
public sealed partial class NestedCanvas
{
    // The selection's colours: the accent the canvas already outlines the
    // selection with, as a tint for tiles and as the rectangle's fill.
    private static readonly Brush SelectedTileBrush = Frozen(Color.FromArgb(0x2E, 0x60, 0xCD, 0xFF));
    private static readonly Pen SelectedTilePen = FrozenPen(Color.FromRgb(0x60, 0xCD, 0xFF), 1.5);
    private static readonly Brush SelectedCellBrush = Frozen(Color.FromArgb(0x38, 0x60, 0xCD, 0xFF));
    private static readonly Brush SelectedRunBrush = Frozen(Color.FromArgb(0x66, 0x60, 0xCD, 0xFF));
    private static readonly Brush SelectedSpeckBrush = Frozen(Color.FromArgb(0xCC, 0x60, 0xCD, 0xFF));
    private static readonly Pen MarqueeScopePen = FrozenPen(Color.FromArgb(0x66, 0x60, 0xCD, 0xFF), 1);

    /// <summary>Tiles drawn one by one while at most this many selected ones are on screen in a folder; past it, as runs.</summary>
    private const int MaximumSelectedOneByOne = 400;

    /// <summary>
    /// Whether runs of selected tiles too small to outline one by one are
    /// filled with a brush that repeats one tile's highlight - so they still
    /// read as separate items - rather than a plain wash.
    /// </summary>
    internal static bool TiledSelectionRuns { get; set; } = true;

    /// <summary>
    /// The selection's picture, recorded apart from the marks around it: the
    /// marks' layer draws it in its place under the drop target and the
    /// beacons, and it is re-recorded on its own when only the selection or a
    /// rectangle changed, leaving the marks, the names and the cells as they
    /// were.  Not frozen, so re-recording it is all a change takes.
    /// </summary>
    private readonly DrawingGroup _selectionDrawing = new();

    /// <summary>What the selection's picture was last recorded for; the same again and it is not recorded again.</summary>
    private (long Frame, long Selection, long Marquee, int Filter, double Width, double Height, double Scale, int Tree) _selectionKey;

    private int _prunedTreeVersion = -1;
    private Pen? _marqueePen;
    private double _marqueePenScale;
    private (double StepX, double StepY, double TileWidth, double TileHeight, double Scale) _tileKey;
    private Drawing? _tileDrawing;

    /// <summary>For tests: how many times the selection's picture was recorded.</summary>
    internal int SelectionRecordCount { get; private set; }

    /// <summary>For tests: shapes the last recording of the selection drew.</summary>
    internal int LastSelectionPrimitives { get; private set; }

    /// <summary>How long the last recording of the selection took on the UI thread.</summary>
    internal double LastSelectionMilliseconds { get; private set; }

    /// <summary>For tests: records the selection's picture now, whatever it was last recorded for, and says what that cost.</summary>
    internal (double Milliseconds, long Bytes) RecordSelectionForTests()
    {
        _selectionKey = default;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        RefreshSelectionLayer();
        return (LastSelectionMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    /// <summary>The marks' layer's part: the selection, in its place, as last recorded (see <see cref="RefreshSelectionLayer"/>).</summary>
    private void DrawSelection(DrawingContext dc)
    {
        RefreshSelectionLayer();
        dc.DrawDrawing(_selectionDrawing);
    }

    /// <summary>
    /// Records the selection's picture again if anything it shows moved:
    /// the camera or the tree (a new scene), the selection, the rectangle,
    /// the filter, the size of the view.  Only the folders that hold
    /// something selected are visited, only when on screen, and in each only
    /// the rows and columns on screen - so ten thousand selected files cost
    /// what can be seen of them.
    /// </summary>
    private void RefreshSelectionLayer()
    {
        if (_tree is not { } tree)
        {
            return;
        }

        TakeSetSelection();
        UpdateMarquee();
        var key = (RenderCount, _selection.Version, _marquee?.Version ?? -1, _filterStamp, _viewWidth, _viewHeight, _scaleX, tree.Version);
        if (key == _selectionKey && ReferenceEquals(_selectionMarquee, _marquee))
        {
            return;
        }

        _selectionKey = key;
        _selectionMarquee = _marquee;
        // What vanished is not cleared here: bringing the rectangle up to
        // date, just above, and starting it may already have found files
        // gone, and those are reported below with the rest.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (tree.Version != _prunedTreeVersion)
        {
            _prunedTreeVersion = tree.Version;
            _selection.DropDetachedFolders(_vanished);
        }

        var primitives = 0;
        using (var dc = _selectionDrawing.Open())
        {
            var marquee = _marquee;
            foreach (var container in _selection.Containers)
            {
                // A replacement being drawn shows nothing but itself; the
                // rectangle's own folder is drawn below, live.
                if (marquee is not null && (marquee.Mode == NestedSelectMode.Replace || ReferenceEquals(container, marquee.Container)))
                {
                    continue;
                }

                primitives += DrawSelectedIn(dc, container, null);
            }

            if (marquee is not null)
            {
                primitives += DrawSelectedIn(dc, marquee.Container, marquee);
            }

            primitives += DrawFocusRing(dc);
        }

        SelectionRecordCount++;
        LastSelectionPrimitives = primitives;
        LastSelectionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // With those the gestures found gone since - a Ctrl+click, a range,
        // a rectangle let go of - which had nowhere to say so.
        _selection.TakeVanished(_vanished);
        if (_vanished.Count > 0)
        {
            // Gone from disk since they were selected: one edit for all of
            // them, so no command acts on something that is not there.
            var gone = _vanished.ToArray();
            _vanished.Clear();
            SelectionCommitted?.Invoke(new SelectionEdit { Removed = gone, Source = SelectionSource.Command });
        }
    }

    private NestedMarquee? _selectionMarquee;

    /// <summary>What is selected directly inside one folder, or lit by the rectangle drawn over it; returns the shapes drawn.</summary>
    private int DrawSelectedIn(DrawingContext dc, NestedFolder container, NestedMarquee? marquee)
    {
        if (CellOf(container, place: false) is not { } cell)
        {
            // Not on the canvas: filtered out, hidden, or gone - which the
            // tiles' own catching up reports.
            _selection.FilesOf(container, _vanished);
            return 0;
        }

        var (x, y, w) = cell;
        if (x >= _viewWidth || y >= _viewHeight || x + w <= 0 || y + w * NestedLayout.CellHeight <= 0)
        {
            return 0;
        }

        var drawn = 0;
        if (marquee is not null || _selection.FoldersIn(container) > 0)
        {
            drawn += DrawSelectedFolders(dc, container, x, y, w, marquee);
        }

        var files = _selection.FilesOf(container, _vanished);
        if (marquee is not null || files is not null)
        {
            drawn += DrawSelectedFiles(dc, container, x, y, w, files, marquee);
        }

        return drawn;
    }

    /// <summary>
    /// Selected sub-folders on screen.  Big enough to see, each gets the
    /// selection's outline, as a single selected folder always had; specks
    /// too small for one are filled with the accent instead, so a rectangle
    /// dragged over an overview lights what it takes.
    /// </summary>
    private int DrawSelectedFolders(DrawingContext dc, NestedFolder container, double x, double y, double w, NestedMarquee? marquee)
    {
        var grid = container.Grid;
        var children = container.Children;
        if (grid.IsEmpty || w * grid.Scale < MinimumCellPixels)
        {
            return 0;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(-x / w, -y / w, (_viewWidth - x) / w, (_viewHeight - y) / w);
        var visible = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = grid.IndexOf(row, column);
                if (index >= children.Count)
                {
                    break;
                }

                if (marquee is not null ? marquee.IsFolderSelected(index) : _selection.IsSelected(children[index]))
                {
                    visible++;
                }
            }
        }

        if (visible == 0)
        {
            return 0;
        }

        var cellWidth = w * grid.Scale;
        var outlined = cellWidth >= 3 && visible <= MaximumSelectedOneByOne;
        var speck = cellWidth * _scaleX < 4;
        var drawn = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = grid.IndexOf(row, column);
                if (index >= children.Count)
                {
                    break;
                }

                var child = children[index];
                if (!(marquee is not null ? marquee.IsFolderSelected(index) : _selection.IsSelected(child)))
                {
                    continue;
                }

                Place(container, child, x, y, w, out var childX, out var childY, out var childWidth);
                var rect = new Rect(childX, childY, childWidth, childWidth * NestedLayout.CellHeight);
                if (speck)
                {
                    dc.DrawRectangle(SelectedSpeckBrush, null, MinimumDeviceRect(rect));
                }
                else if (outlined)
                {
                    var radius = rect.Width >= 40 ? Math.Min(6, rect.Width * 0.03) : 1;
                    rect.Inflate(1, 1);
                    dc.DrawRoundedRectangle(null, SelectionPen, rect, radius, radius);
                }
                else
                {
                    dc.DrawRectangle(SelectedCellBrush, null, rect);
                }

                drawn++;
            }
        }

        return drawn;
    }

    /// <summary>
    /// Selected file tiles on screen.  Up to <see cref="MaximumSelectedOneByOne"/>
    /// tiles big enough to read are tinted and outlined one by one.  More, or
    /// smaller, and each unbroken run of selected tiles along a row becomes
    /// one rectangle - rows with the same runs merged into one - filled with
    /// a brush that repeats one tile's highlight, so a block of ten thousand
    /// is a handful of shapes that still look like ten thousand items; below
    /// four pixels, or where the files are only a wash, the runs are a plain
    /// fill.
    /// </summary>
    private int DrawSelectedFiles(DrawingContext dc, NestedFolder container, double x, double y, double w, FileSet? set, NestedMarquee? marquee)
    {
        var grid = container.FileGrid;
        var files = container.Files;
        if (grid.IsEmpty || files.Count == 0)
        {
            return 0;
        }

        var tileWidth = grid.TileWidth * w;
        var tileHeight = grid.TileHeight * w;
        var wash = tileWidth * _scaleX < 2.5;
        if (wash && w * _scaleX < 12)
        {
            return 0;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(-x / w, -y / w, (_viewWidth - x) / w, (_viewHeight - y) / w);
        if (firstColumn > lastColumn || firstRow > lastRow)
        {
            return 0;
        }

        bool IsSelected(int index) => marquee is not null ? marquee.IsFileSelected(index) : set![index];

        var oneByOne = tileHeight >= 12;
        if (oneByOne)
        {
            var visible = 0;
            for (var row = firstRow; row <= lastRow && visible <= MaximumSelectedOneByOne; row++)
            {
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var index = grid.IndexOf(row, column);
                    if (index >= files.Count)
                    {
                        break;
                    }

                    if (IsSelected(index))
                    {
                        visible++;
                    }
                }
            }

            oneByOne = visible <= MaximumSelectedOneByOne;
        }

        if (oneByOne)
        {
            var drawn = 0;
            var radius = Math.Min(3, tileHeight * 0.2);
            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var index = grid.IndexOf(row, column);
                    if (index >= files.Count)
                    {
                        break;
                    }

                    if (!IsSelected(index))
                    {
                        continue;
                    }

                    PlaceFile(container, index, x, y, w, out var fx, out var fy, out var fw, out var fh);
                    var rect = new Rect(fx, fy, fw, fh);
                    rect.Inflate(1, 1);
                    dc.DrawRoundedRectangle(SelectedTileBrush, SelectedTilePen, rect, radius, radius);
                    drawn++;
                }
            }

            return drawn;
        }

        // Runs.  Grid-relative, then moved to where the grid is on screen.
        var brush = !wash && tileHeight * _scaleY >= 4 && TiledSelectionRuns
            ? TileBrush(x + grid.Left * w, y + grid.Top * w, grid.StepX * w, grid.StepY * w, tileWidth, tileHeight)
            : SelectedRunBrush;
        var shapes = 0;
        var runs = _runs;
        var previous = _previousRuns;
        runs.Clear();
        previous.Clear();
        var openFrom = -1;
        for (var row = firstRow; row <= lastRow + 1; row++)
        {
            runs.Clear();
            if (row <= lastRow)
            {
                var start = -1;
                for (var column = firstColumn; column <= lastColumn + 1; column++)
                {
                    var index = grid.IndexOf(row, column);
                    var selected = column <= lastColumn && index < files.Count && IsSelected(index);
                    if (selected && start < 0)
                    {
                        start = column;
                    }
                    else if (!selected && start >= 0)
                    {
                        runs.Add((start, column - 1));
                        start = -1;
                    }
                }
            }

            // Rows with the same runs as the one above extend its rectangles.
            if (openFrom >= 0 && SameRuns(runs, previous))
            {
                continue;
            }

            if (openFrom >= 0)
            {
                foreach (var (from, to) in previous)
                {
                    var left = x + (grid.Left + from * grid.StepX) * w;
                    var right = x + (grid.Left + to * grid.StepX) * w + tileWidth;
                    var top = y + (grid.Top + openFrom * grid.StepY) * w;
                    var bottom = y + (grid.Top + (row - 1) * grid.StepY) * w + tileHeight;
                    dc.DrawRectangle(brush, null, new Rect(left, top, right - left, bottom - top));
                    shapes++;
                }
            }

            previous.Clear();
            previous.AddRange(runs);
            openFrom = runs.Count > 0 ? row : -1;
        }

        return shapes;
    }

    private readonly List<(int From, int To)> _runs = [];
    private readonly List<(int From, int To)> _previousRuns = [];

    private static bool SameRuns(List<(int From, int To)> first, List<(int From, int To)> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A brush that repeats one tile's highlight over a grid of tiles: the
    /// tint and a one-pixel accent edge on the tile, clear over the gap.  Its
    /// tile is laid out only when the grid's step changes - a zoom - and the
    /// brush is fixed to where the grid starts on screen, so a run filled
    /// with it lights each tile under it exactly.
    /// </summary>
    private Brush TileBrush(double originX, double originY, double stepX, double stepY, double tileWidth, double tileHeight)
    {
        var key = (stepX, stepY, tileWidth, tileHeight, _scaleX);
        if (_tileDrawing is null || key != _tileKey)
        {
            var edge = 1 / _scaleX;
            var tile = new DrawingGroup();
            using (var context = tile.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, stepX, stepY));
                var pen = new Pen(SelectedTilePen.Brush, edge);
                pen.Freeze();
                var radius = Math.Min(3, tileHeight * 0.2);
                context.DrawRoundedRectangle(
                    SelectedCellBrush,
                    pen,
                    new Rect(edge / 2, edge / 2, Math.Max(0, tileWidth - edge), Math.Max(0, tileHeight - edge)),
                    radius,
                    radius);
            }

            tile.Freeze();
            _tileDrawing = tile;
            _tileKey = key;
        }

        var brush = new DrawingBrush(_tileDrawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Viewbox = new Rect(0, 0, stepX, stepY),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(originX, originY, stepX, stepY),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The focus among several selected items: the ring the canvas has for
    /// it, round the one the address bar names.  With one item selected the
    /// selection itself says which.
    /// </summary>
    private int DrawFocusRing(DrawingContext dc)
    {
        if (_selection.Count < 2 || _marquee is not null || _selection.Active is not { } active || !_selection.Contains(active))
        {
            return 0;
        }

        Rect rect;
        if (active.Folder is { } folder)
        {
            if (CellOf(folder, place: false) is not { } cell)
            {
                return 0;
            }

            rect = new Rect(cell.X, cell.Y, cell.W, cell.W * NestedLayout.CellHeight);
        }
        else
        {
            var index = NestedTree.FileIndexAsPlaced(active.Container, active.FileName!);
            if (index < 0 || index >= active.Container.FileGrid.Count || CellOf(active.Container, place: false) is not { } cell)
            {
                return 0;
            }

            PlaceFile(active.Container, index, cell.X, cell.Y, cell.W, out var fx, out var fy, out var fw, out var fh);
            rect = new Rect(fx, fy, fw, fh);
        }

        if (rect.Width < 6 || rect.Right < 0 || rect.Bottom < 0 || rect.Left > _viewWidth || rect.Top > _viewHeight)
        {
            return 0;
        }

        var radius = active.IsFile ? Math.Min(3, rect.Height * 0.2) : rect.Width >= 40 ? Math.Min(6, rect.Width * 0.03) : 1;
        rect.Inflate(3, 3);
        dc.DrawRoundedRectangle(null, ActivePen, rect, radius + 2, radius + 2);
        return 1;
    }

    /// <summary>A rectangle no smaller than one device pixel each way, so a selected speck always shows.</summary>
    private Rect MinimumDeviceRect(Rect rect)
    {
        var minimumWidth = 1 / _scaleX;
        var minimumHeight = 1 / _scaleY;
        if (rect.Width < minimumWidth)
        {
            rect.X -= (minimumWidth - rect.Width) / 2;
            rect.Width = minimumWidth;
        }

        if (rect.Height < minimumHeight)
        {
            rect.Y -= (minimumHeight - rect.Height) / 2;
            rect.Height = minimumHeight;
        }

        return rect;
    }

    /// <summary>
    /// The rectangle being drawn: from its start - on the content, through
    /// this frame's camera - to the pointer, cut to the folder it is drawn in
    /// and to the view, its edges on whole device pixels.  A one-pixel accent
    /// border over a fifth of the accent, as Windows 11 draws it; the
    /// system's highlight colour in high contrast.  Round the folder, a faint
    /// outline, so it is clear which folder's items it takes.
    /// </summary>
    private void DrawMarquee(DrawingContext dc)
    {
        if (_marquee is not { } marquee || CellOf(marquee.Container, place: true) is not { } cell)
        {
            return;
        }

        var cellRect = new Rect(cell.X, cell.Y, cell.W, cell.W * NestedLayout.CellHeight);
        var scope = cellRect;
        if (scope.Width < 8 || scope.Height < 8)
        {
            scope = new Rect(
                scope.X + scope.Width / 2 - Math.Max(4, scope.Width / 2),
                scope.Y + scope.Height / 2 - Math.Max(4, scope.Height / 2),
                Math.Max(8, scope.Width),
                Math.Max(8, scope.Height));
        }

        var scopeRadius = scope.Width >= 40 ? Math.Min(6, scope.Width * 0.03) : 1;
        dc.DrawRoundedRectangle(null, MarqueeScopePen, scope, scopeRadius, scopeRadius);

        var start = new Point(cell.X + marquee.StartX * cell.W, cell.Y + marquee.StartY * cell.W);
        var band = new Rect(start, marquee.Pointer);
        band.Intersect(cellRect);
        band.Intersect(new Rect(0, 0, _viewWidth, _viewHeight));
        if (band.IsEmpty)
        {
            return;
        }

        var left = Math.Round(band.Left * _scaleX) / _scaleX;
        var right = Math.Round(band.Right * _scaleX) / _scaleX;
        var top = Math.Round(band.Top * _scaleY) / _scaleY;
        var bottom = Math.Round(band.Bottom * _scaleY) / _scaleY;
        if (right - left <= 0 || bottom - top <= 0)
        {
            return;
        }

        var highContrast = SystemParameters.HighContrast;
        var accent = highContrast ? SystemColors.HighlightColor : Color.FromRgb(0x60, 0xCD, 0xFF);
        var fill = new SolidColorBrush(Color.FromArgb(highContrast ? (byte)0x40 : (byte)0x33, accent.R, accent.G, accent.B));
        fill.Freeze();
        dc.DrawRectangle(fill, null, new Rect(left, top, right - left, bottom - top));
        if (_marqueePen is null || _marqueePenScale != _scaleX || _marqueePen.Brush is SolidColorBrush { Color: var penColour } && penColour != accent)
        {
            _marqueePen = new Pen(new SolidColorBrush(accent), 1 / _scaleX);
            _marqueePen.Freeze();
            _marqueePenScale = _scaleX;
        }

        var half = 0.5 / _scaleX;
        if (right - left > 2 * half && bottom - top > 2 * half)
        {
            dc.DrawRectangle(null, _marqueePen, new Rect(left + half, top + half, right - left - 2 * half, bottom - top - 2 * half));
        }
    }

    /// <summary>
    /// How many items the rectangle would select, by the pointer - "N of M"
    /// while the filter is on, M being how many in the folder match - in the
    /// style of the hover's tag, kept inside the view.
    /// </summary>
    private void DrawMarqueeCount(DrawingContext dc)
    {
        if (_marquee is not { HitCount: > 0 } marquee)
        {
            return;
        }

        var caption = marquee.MatchCount >= 0
            ? $"{marquee.HitCount:N0} of {marquee.MatchCount:N0}"
            : marquee.HitCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        var text = Text(caption, 11, TextBrush, 240, bold: false);
        var box = new Rect(
            Math.Clamp(marquee.Pointer.X + 14, 4, Math.Max(4, _viewWidth - text.Width - 20)),
            Math.Clamp(marquee.Pointer.Y + 18, 4, Math.Max(4, _viewHeight - text.Height - 12)),
            text.Width + 12,
            text.Height + 6);
        dc.DrawRoundedRectangle(TipBrush, TipPen, box, 4, 4);
        DrawTextAt(dc, text, new Point(box.X + 6, box.Y + 3));
    }

    private void DrawDropTarget(DrawingContext dc)
    {
        if (_dropTarget is null || ScreenRect(_dropTarget, place: false) is not { } rect)
        {
            return;
        }

        if (rect.Width < 6)
        {
            rect = new Rect(rect.X + rect.Width / 2 - 8, rect.Y + rect.Height / 2 - 8, 16, 16);
        }

        dc.DrawRoundedRectangle(DropFillBrush, DropPen, rect, 5, 5);
    }

    /// <summary>
    /// The pointer's outline and name tag live on their own layer: moving the
    /// mouse redraws two shapes there, not the whole canvas under them.  So
    /// does a selection rectangle being drawn - with the selection's own
    /// picture brought up to date first, and nothing else: moving the
    /// rectangle's corner with the camera still redraws no cells, no names
    /// and no marks.  Last of all, the view takes its step of sliding on at
    /// an edge, for the next frame to draw.
    /// </summary>
    private void RenderOverlay()
    {
        RefreshSelectionLayer();
        if (_marquee is { } marquee)
        {
            CountMarquee(marquee);
        }

        // A tag is one or two texts; it never waits on the frame's budget.
        var budget = _textBudget;
        _textBudget = int.MaxValue;
        using (var dc = _overlay.RenderOpen())
        {
            DrawHover(dc);
            DrawHoverTip(dc);
            DrawMarquee(dc);
            DrawMarqueeCount(dc);
        }

        _textBudget = budget;
        SlideAtEdges();
    }

    private void DrawHover(DrawingContext dc)
    {
        if (_hover is not { } hover || _press != PressKind.None || TargetRect(hover.Folder, hover.FileIndex, place: false) is not { } rect || rect.Width < 3)
        {
            return;
        }

        var radius = hover.IsFile ? Math.Min(3, rect.Height * 0.2) : rect.Width >= 40 ? Math.Min(6, rect.Width * 0.03) : 1;
        dc.DrawRoundedRectangle(null, HoverPen, rect, radius, radius);
    }

    /// <summary>
    /// A name next to the pointer for a folder too small to carry its own:
    /// with the whole disk on screen almost everything is that small, and
    /// this is how it is explored without zooming into every speck.
    /// </summary>
    private void DrawHoverTip(DrawingContext dc)
    {
        if (_hover is null && _press == PressKind.None && HotspotAt(_hoverPoint) is { Tip: { Length: > 0 } tip })
        {
            var tipText = Text(tip, 12, TextBrush, 360, bold: false);
            var tipBox = new Rect(
                Math.Clamp(_hoverPoint.X - tipText.Width / 2 - 8, 4, Math.Max(4, _viewWidth - tipText.Width - 20)),
                Math.Clamp(_hoverPoint.Y + 16, 4, Math.Max(4, _viewHeight - tipText.Height - 14)),
                tipText.Width + 16,
                tipText.Height + 8);
            dc.DrawRoundedRectangle(TipBrush, TipPen, tipBox, 5, 5);
            DrawTextAt(dc, tipText, new Point(tipBox.X + 8, tipBox.Y + 4));
            return;
        }

        if (_hover is not { } hover || _press != PressKind.None || hover.Folder.IsComputer)
        {
            return;
        }

        string name;
        string detail;
        if (hover.IsFile)
        {
            if (hover.Bounds.Height >= FileLabelPixels)
            {
                return;
            }

            name = hover.File.Name;
            detail = FormatSize(hover.File.Length);
        }
        else
        {
            if (_labelled.Contains(hover.Folder))
            {
                return;
            }

            name = hover.Folder.Name;
            detail = DetailText(hover.Folder);
        }

        var title = Text(name, 12, TextBrush, 360, bold: true);
        var mark = Shows(CanvasLayer.Marks) ? _markLookup?.Invoke(hover.Path) ?? FolderMark.None : FolderMark.None;
        var noteText = string.IsNullOrWhiteSpace(mark.Note) ? string.Empty : mark.Note.Trim();
        var second = noteText.Length > 0 ? noteText : detail;
        ScaledText? sub = second.Length > 0 ? Text(second, 11, noteText.Length > 0 ? TextBrush : TextDimBrush, 360, bold: false) : null;

        var width = Math.Max(title.Width, sub?.Width ?? 0) + 16;
        var height = title.Height + (sub?.Height ?? 0) + 10;
        var x = _hoverPoint.X + 16;
        var y = _hoverPoint.Y + 18;
        if (x + width > _viewWidth - 4) x = _hoverPoint.X - width - 8;
        if (y + height > _viewHeight - 4) y = _hoverPoint.Y - height - 8;
        var box = new Rect(Math.Max(4, x), Math.Max(4, y), width, height);
        dc.DrawRoundedRectangle(TipBrush, TipPen, box, 5, 5);
        DrawTextAt(dc, title, new Point(box.X + 8, box.Y + 5));
        if (sub is not null)
        {
            DrawTextAt(dc, sub.Value, new Point(box.X + 8, box.Y + 5 + title.Height));
        }
    }

    // ---- beacons -------------------------------------------------------------

    private bool IsPinned(string path) => _pinned.Count > 0 && Shows(CanvasLayer.Marks) && _pinned.Contains(path);

    /// <summary>
    /// Reads the folders on the way to every beacon, one path at a time, so a
    /// marked folder deep in the tree has a place before anyone zooms to it.
    /// One run at a time: asked for again while one runs - new beacons, or
    /// the files shown again - that run goes round once more when it is
    /// done, with the beacons as they are then, rather than the ask being
    /// lost.
    /// </summary>
    private async Task ResolveBeaconsAsync()
    {
        if (_tree is null)
        {
            return;
        }

        if (_beaconResolverRunning)
        {
            _beaconResolveAgain = true;
            return;
        }

        _beaconResolverRunning = true;
        try
        {
            do
            {
                _beaconResolveAgain = false;
                for (var pass = 0; pass < 4; pass++)
                {
                    var pending = _beacons
                        .Where(IsShown)
                        .Select(beacon => beacon.Path)
                        .Where(path => !_unresolvable.Contains(path) && (ResolveAsPlaced(path) is not { } found || !NestedTree.IsOnCanvas(found.Folder)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (pending.Count == 0)
                    {
                        break;
                    }

                    foreach (var path in pending)
                    {
                        // The canvas may have let go of its tree while the
                        // last folder was read.
                        if (_tree is not { } tree)
                        {
                            return;
                        }

                        if (!_resolving.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            // A folder resolves to itself; a file to the folder it
                            // is in, once that folder's listing has it.
                            await tree.RevealAsync(path);
                            if (ResolveAsPlaced(path) is null)
                            {
                                _unresolvable.Add(path);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                        {
                            _unresolvable.Add(path);
                        }
                        finally
                        {
                            _resolving.Remove(path);
                        }
                    }

                    RequestFrame(Layers.Decor);
                }
            }
            while (_beaconResolveAgain && _tree is not null);
        }
        finally
        {
            _beaconResolverRunning = false;
        }
    }

    /// <summary>Set when <see cref="ResolveBeaconsAsync"/> is asked for while it runs: the run goes round again.</summary>
    private bool _beaconResolveAgain;

    /// <summary>The beacons the last picture of the marks placed, on the view or at its edges.</summary>
    internal int BeaconsPlaced { get; private set; }

    private void DrawBeacons(DrawingContext dc)
    {
        BeaconsPlaced = 0;
        if (_tree is null || _beacons.Count == 0)
        {
            return;
        }

        var pins = new List<Pin>();
        var offscreen = new List<Pin>();
        foreach (var beacon in _beacons)
        {
            if (!IsShown(beacon))
            {
                continue;
            }

            if (ResolveAsPlaced(beacon.Path) is not { } target || TargetRect(target.Folder, target.FileIndex, place: false) is not { } rect)
            {
                continue;
            }

            // Anything big enough to carry its own name carries its own mark,
            // and a folder the whole view is inside is not "somewhere else".
            if (target.FileIndex < 0
                    ? _labelled.Contains(target.Folder) && rect.Width >= 90 || Covers(rect.X, rect.Y, rect.Width)
                    : rect.Height >= FileLabelPixels)
            {
                continue;
            }

            var centre = new Point(rect.X + rect.Width / 2, rect.Y + Math.Min(rect.Height / 2, 10));
            if (centre.X < -8 || centre.Y < -8 || centre.X > _viewWidth + 8 || centre.Y > _viewHeight + 8)
            {
                offscreen.Add(new Pin(beacon, target.Folder, target.FileIndex, centre, Priority(beacon.Kind)));
                continue;
            }

            pins.Add(new Pin(beacon, target.Folder, target.FileIndex, centre, Priority(beacon.Kind)));
        }

        BeaconsPlaced = pins.Count + offscreen.Count;
        DrawEdgeMarkers(dc, offscreen);
        if (pins.Count == 0)
        {
            return;
        }

        // Nearby pins merge: twenty marks inside one small folder are one
        // badge with a count, not twenty dots on top of each other.
        pins.Sort((left, right) => right.Priority.CompareTo(left.Priority));
        var clusters = new List<List<Pin>>();
        foreach (var pin in pins)
        {
            // Close enough that their circles would overlap: one badge.
            var home = clusters.FirstOrDefault(cluster => (cluster[0].Centre - pin.Centre).Length < 26);
            if (home is null)
            {
                clusters.Add([pin]);
            }
            else
            {
                home.Add(pin);
            }
        }

        var placedLabels = new List<Rect>();
        foreach (var cluster in clusters)
        {
            var lead = cluster[0];
            var brush = BrushFor(lead.Beacon.Colour);
            var centre = lead.Centre;
            if (cluster.Count == 1)
            {
                dc.DrawEllipse(null, BeaconHaloPen, centre, 8.5, 8.5);
                dc.DrawEllipse(brush, BeaconRimPen, centre, 6, 6);
                var glyph = BeaconGlyph(lead.Beacon.Kind);
                if (glyph.Length > 0)
                {
                    var icon = Text(glyph, 7.5, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: false, icon: true);
                    DrawTextAt(dc, icon, new Point(centre.X - icon.Width / 2, centre.Y - icon.Height / 2));
                }
            }
            else
            {
                var count = Text(cluster.Count.ToString(CultureInfo.CurrentCulture), 10, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: true);
                var radius = Math.Max(8.5, count.Width / 2 + 5);
                dc.DrawEllipse(null, BeaconHaloPen, centre, radius + 2.5, radius + 2.5);
                dc.DrawEllipse(brush, BeaconRimPen, centre, radius, radius);
                DrawTextAt(dc, count, new Point(centre.X - count.Width / 2, centre.Y - count.Height / 2));
            }

            var hit = new Rect(centre.X - 10, centre.Y - 10, 20, 20);
            var members = cluster.ToList();
            _hotspots.Add(new Hotspot(hit, () => OnBeaconClicked(members), null));

            // A label beside it, if it does not run into one already placed.
            var caption = cluster.Count == 1
                ? lead.Beacon.Label
                : $"{lead.Beacon.Label} +{cluster.Count - 1}";
            var text = Text(caption, 11, TextBrush, 220, bold: false);
            var labelRect = new Rect(centre.X + 12, centre.Y - text.Height / 2 - 2, text.Width + 10, text.Height + 4);
            if (labelRect.Right > _viewWidth - 2)
            {
                labelRect.X = centre.X - 12 - labelRect.Width;
            }

            if (placedLabels.Any(placed => placed.IntersectsWith(labelRect)) || clusters.Any(other => other != cluster && labelRect.Contains(other[0].Centre)))
            {
                continue;
            }

            placedLabels.Add(labelRect);
            dc.DrawRoundedRectangle(PillBrush, null, labelRect, 4, 4);
            DrawTextAt(dc, text, new Point(labelRect.X + 5, labelRect.Y + 2));
            _hotspots.Add(new Hotspot(labelRect, () => OnBeaconClicked(members), null));
        }
    }

    /// <summary>
    /// Marks that are off screen, as arrows on the edge of the view pointing
    /// the way to them.  Deep inside one folder, everything the user marked
    /// elsewhere would otherwise be out of sight and out of mind; this is the
    /// "it is over there" that makes zooming out to look for it unnecessary.
    /// </summary>
    private void DrawEdgeMarkers(DrawingContext dc, List<Pin> offscreen)
    {
        if (offscreen.Count == 0 || _viewWidth < 80 || _viewHeight < 80)
        {
            return;
        }

        const double inset = 16;
        var centre = new Point(_viewWidth / 2, _viewHeight / 2);
        var halfWidth = _viewWidth / 2 - inset;
        var halfHeight = _viewHeight / 2 - inset;

        // Where the ray from the middle of the view to each mark leaves the
        // inset frame; marks in the same direction share one arrow.
        offscreen.Sort((left, right) => right.Priority.CompareTo(left.Priority));
        var groups = new List<(Point At, Vector Direction, List<Pin> Members)>();
        foreach (var pin in offscreen)
        {
            var direction = pin.Centre - centre;
            if (double.IsNaN(direction.X) || double.IsNaN(direction.Y) || direction.Length < 1e-9)
            {
                continue;
            }

            var scale = Math.Min(
                Math.Abs(direction.X) < 1e-12 ? double.MaxValue : halfWidth / Math.Abs(direction.X),
                Math.Abs(direction.Y) < 1e-12 ? double.MaxValue : halfHeight / Math.Abs(direction.Y));
            var at = new Point(centre.X + direction.X * scale, centre.Y + direction.Y * scale);
            direction.Normalize();

            var home = groups.FindIndex(group => (group.At - at).Length < 22);
            if (home < 0)
            {
                groups.Add((at, direction, [pin]));
            }
            else
            {
                groups[home].Members.Add(pin);
            }
        }

        foreach (var (at, direction, members) in groups)
        {
            var lead = members[0];
            var brush = BrushFor(lead.Beacon.Colour);

            // A small arrowhead pointing out of the view, and the mark's dot behind it.
            var tip = at + direction * 9;
            var side = new Vector(-direction.Y, direction.X) * 5;
            var arrow = new StreamGeometry();
            using (var context = arrow.Open())
            {
                context.BeginFigure(tip, isFilled: true, isClosed: true);
                context.LineTo(at + direction * 2 + side, isStroked: false, isSmoothJoin: false);
                context.LineTo(at + direction * 2 - side, isStroked: false, isSmoothJoin: false);
            }

            arrow.Freeze();
            dc.DrawGeometry(brush, null, arrow);
            dc.DrawEllipse(brush, BeaconRimPen, at, 5.5, 5.5);
            if (members.Count > 1)
            {
                var count = Text(members.Count.ToString(CultureInfo.CurrentCulture), 8.5, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: true);
                DrawTextAt(dc, count, new Point(at.X - count.Width / 2, at.Y - count.Height / 2));
            }

            _hotspots.Add(new Hotspot(new Rect(at.X - 11, at.Y - 11, 22, 22), () => OnBeaconClicked(members), null, EdgeTip(members)));
        }
    }

    private static string EdgeTip(List<Pin> members) =>
        members.Count == 1 ? members[0].Beacon.Label : $"{members[0].Beacon.Label} and {members.Count - 1} more";

    private void OnBeaconClicked(IReadOnlyList<Pin> pins)
    {
        // Found again by path, for the current order: a mark is drawn where
        // the picture had its folder, and the folder may have been placed
        // again since - the tile a file's mark was drawn on can hold another
        // file by the time it is clicked.
        var targets = new List<(NestedFolder Folder, int FileIndex)>(pins.Count);
        foreach (var pin in pins)
        {
            if (Resolve(pin.Beacon.Path) is { } target)
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        if (targets.Count == 1)
        {
            var (folder, fileIndex) = targets[0];
            var path = fileIndex >= 0 && fileIndex < folder.Files.Count ? folder.PathOf(folder.Files[fileIndex]) : folder.FullPath;
            MarkSelected(path);
            SelectRequested?.Invoke(path, false);
            if (fileIndex >= 0)
            {
                FlyToReadable(folder, fileIndex);
            }
            else
            {
                FlyTo(folder, 0.6);
            }

            return;
        }

        // Several marks in one spot: go to the smallest folder that holds all of them.
        var common = targets[0].Folder;
        foreach (var (folder, _) in targets.Skip(1))
        {
            while (!common.Contains(folder) && common.Parent is not null)
            {
                common = common.Parent;
            }
        }

        FlyTo(common, 0.92);
    }

    private static int Priority(NestedBeaconKind kind) =>
        ((kind & NestedBeaconKind.Active) != 0 ? 16 : 0)
        + ((kind & NestedBeaconKind.Pinned) != 0 ? 8 : 0)
        + ((kind & NestedBeaconKind.Note) != 0 ? 4 : 0)
        + ((kind & NestedBeaconKind.Colour) != 0 ? 2 : 0)
        + ((kind & NestedBeaconKind.Search) != 0 ? 1 : 0);

    private static string BeaconGlyph(NestedBeaconKind kind) =>
        (kind & NestedBeaconKind.Pinned) != 0 ? ""
        : (kind & NestedBeaconKind.Note) != 0 ? ""
        : (kind & NestedBeaconKind.Search) != 0 ? ""
        : string.Empty;

    private static Brush BeaconGlyphBrush(Color colour)
    {
        var luminance = 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
        return luminance > 140 ? Brushes.Black : Brushes.White;
    }

    // ---- the trail -------------------------------------------------------------

    /// <summary>
    /// The folders the view is inside whose names have scrolled off the top:
    /// deep in, every visible cell belongs to something whose title is far
    /// above the screen, and this says what.
    /// </summary>
    private void DrawTrail(DrawingContext dc)
    {
        if (_tree is null)
        {
            return;
        }

        var trail = _trail;
        trail.Clear();
        var folder = _tree.Root;
        var (x, y, w) = _chain[folder];
        var probe = new Point(_viewWidth / 2, Math.Min(_viewHeight / 2, 80));
        while (true)
        {
            if (y + w * NestedLayout.HeaderHeight < 0 && !folder.IsComputer)
            {
                trail.Add(folder);
            }

            // As placed, not placed here: the trail names the folders the
            // picture shows under the probe, and a folder the frame drew in
            // its previous order is still in its previous place there.
            var grid = folder.Grid;
            if (grid.IsEmpty)
            {
                break;
            }

            var index = grid.IndexAt((probe.X - x) / w, (probe.Y - y) / w);
            if (index < 0 || index >= folder.Children.Count)
            {
                break;
            }

            var child = folder.Children[index];
            if (_chain.TryGetValue(child, out var exact))
            {
                (x, y, w) = exact;
            }
            else
            {
                (x, y, w) = (x + child.OffsetX * w, y + child.OffsetY * w, w * child.Scale);
            }

            if (y + w * NestedLayout.HeaderHeight >= 0)
            {
                break;
            }

            folder = child;
        }

        if (trail.Count == 0)
        {
            return;
        }

        // Long trails keep the ends: where it starts and where the view is.
        var shown = _trailShown;
        shown.Clear();
        if (trail.Count <= 5)
        {
            shown.AddRange(trail);
        }
        else
        {
            shown.Add(trail[0]);
            shown.Add(null);
            for (var index = trail.Count - 3; index < trail.Count; index++)
            {
                shown.Add(trail[index]);
            }
        }

        if (!_labelsOnGpu)
        {
            DrawTrailPieces(dc, shown, _hotspots);
            return;
        }

        // With the names on the GPU the trail is the one text a moving frame
        // still has WPF lay out and draw.  It only changes when the folders
        // in it do, so it is recorded once and replayed: no text is formatted
        // again for a frame that shows the same trail.
        if (_trailDrawing is null || !IsSameTrail(shown))
        {
            _trailHotspots.Clear();
            var drawing = new DrawingGroup();

            // At most seven fixed-size texts, laid out whatever the frame's
            // budget of new layouts: the beacons before it may have spent it
            // all, and a trail recorded without its names would be replayed
            // empty, and unclickable, until the folders in it change.
            var budget = _textBudget;
            _textBudget = int.MaxValue;
            try
            {
                using var context = drawing.Open();
                DrawTrailPieces(context, shown, _trailHotspots);
            }
            finally
            {
                _textBudget = budget;
            }

            drawing.Freeze();
            _trailDrawing = drawing;
            _trailScale = _scaleY;
            _trailKey.Clear();
            foreach (var step in shown)
            {
                _trailKey.Add((step, step?.Name));
            }
        }

        dc.DrawDrawing(_trailDrawing);
        _hotspots.AddRange(_trailHotspots);
    }

    private readonly List<NestedFolder> _trail = [];
    private readonly List<NestedFolder?> _trailShown = [];
    private readonly List<(NestedFolder? Folder, string? Name)> _trailKey = [];
    private readonly List<Hotspot> _trailHotspots = [];
    private DrawingGroup? _trailDrawing;
    private double _trailScale;

    /// <summary>Whether the recorded trail shows these folders, by these names, at this scale.</summary>
    private bool IsSameTrail(List<NestedFolder?> shown)
    {
        if (_trailKey.Count != shown.Count || _trailScale != _scaleY)
        {
            return false;
        }

        for (var index = 0; index < shown.Count; index++)
        {
            var step = shown[index];
            if (!ReferenceEquals(_trailKey[index].Folder, step) || !ReferenceEquals(_trailKey[index].Name, step?.Name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The trail's pill, names and separators, and a hotspot on each name to fly to its folder.</summary>
    private void DrawTrailPieces(DrawingContext dc, List<NestedFolder?> shown, List<Hotspot> hotspots)
    {
        const double pad = 8;
        var cursor = 14 + pad;
        var top = 12.0;
        var pieces = new List<(ScaledText Text, NestedFolder? Folder)>();
        foreach (var step in shown)
        {
            var label = step is null ? "…" : step.Name;
            pieces.Add((Text(label, 12, step is null ? TextDimBrush : TextBrush, 240, bold: step is not null), step));
        }

        var separator = Text("  ›  ", 12, TextDimBrush, double.MaxValue, bold: false);
        var total = pieces.Sum(piece => piece.Text.Width) + separator.Width * (pieces.Count - 1) + 2 * pad;
        var height = pieces.Max(piece => piece.Text.Height) + 8;
        dc.DrawRoundedRectangle(PillBrush, null, new Rect(14, top, total, height), 6, 6);
        for (var index = 0; index < pieces.Count; index++)
        {
            var (text, step) = pieces[index];
            var at = new Point(cursor, top + 4);
            DrawTextAt(dc, text, at);
            if (step is not null)
            {
                var target = step;
                hotspots.Add(new Hotspot(new Rect(at, new Size(text.Width, text.Height)), () => FlyTo(target), null));
            }

            cursor += text.Width;
            if (index < pieces.Count - 1)
            {
                DrawTextAt(dc, separator, new Point(cursor, top + 4));
                cursor += separator.Width;
            }
        }
    }

    private readonly record struct Pin(NestedBeacon Beacon, NestedFolder Folder, int FileIndex, Point Centre, int Priority);

    /// <summary>A clickable spot drawn this frame (a beacon, a trail step) or a handle to grab a folder by.</summary>
    private sealed record Hotspot(Rect Bounds, Action? Click, NestedFolder? Grab, string? Tip = null);
}
