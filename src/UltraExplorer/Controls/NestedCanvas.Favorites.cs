using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>A navigation link to a folder, not another folder in the scene.</summary>
public sealed record NestedFavoriteLink(string Path, string Label, Color Colour);

public sealed partial class NestedCanvas
{
    private IReadOnlyList<NestedFavoriteLink> _favoriteLinks = [];
    private bool _showFavoriteLinks;

    /// <summary>Optional navigation circles above This PC. They do not change the tree or its selection.</summary>
    public bool ShowFavoriteLinks
    {
        get => _showFavoriteLinks;
        set
        {
            if (_showFavoriteLinks == value) return;
            var refit = IsAtFavoriteOverview;
            _showFavoriteLinks = value;
            FavoriteLinksChanged(refit);
        }
    }

    /// <summary>The window handles this just like its existing sidebar navigation.</summary>
    public event Action<string>? FavoriteLinkRequested;

    /// <summary>Sets shortcut metadata only. Drawing never resolves or reads their destinations.</summary>
    public void SetFavoriteLinks(IReadOnlyList<NestedFavoriteLink> links)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = links.Where(link => !string.IsNullOrWhiteSpace(link.Path)
                && !string.IsNullOrWhiteSpace(link.Label)
                && seen.Add(link.Path.TrimEnd('\\', '/')))
            .ToArray();
        if (_favoriteLinks.SequenceEqual(next)) return;
        var refit = IsAtFavoriteOverview;
        _favoriteLinks = next;
        FavoriteLinksChanged(refit);
    }

    private bool IsAtFavoriteOverview => _tree is not null && _hasCamera
        && ReferenceEquals(_anchor, _tree.Root) && Math.Abs(_aw - FitWidth()) < 0.1;

    private void FavoriteLinksChanged(bool refit)
    {
        if (refit) FitAll(animated: false);
        RequestFrame(Layers.All);
    }

    private const int FavoriteColumns = 8;
    private const double FavoriteRowHeight = 0.102;
    private double FavoriteStripHeight => !_showFavoriteLinks || _favoriteLinks.Count == 0 ? 0
        : 0.038 + ((_favoriteLinks.Count + FavoriteColumns - 1) / FavoriteColumns) * FavoriteRowHeight;

    private (double X, double Y, double W) OverviewFitRect()
    {
        var width = FitWidth();
        var totalHeight = NestedLayout.CellHeight + FavoriteStripHeight;
        return ((_viewWidth - width) / 2,
            (_viewHeight - width * totalHeight) / 2 + width * FavoriteStripHeight, width);
    }

    /// <summary>World-space link positions, anchored to This PC and following the same pan and zoom.</summary>
    internal IReadOnlyList<(NestedFavoriteLink Link, Point Centre, double Radius)> FavoriteLinkPositions()
    {
        if (FavoriteStripHeight == 0 || RootRect() is not { } root) return [];
        var positions = new List<(NestedFavoriteLink, Point, double)>(_favoriteLinks.Count);
        for (var index = 0; index < _favoriteLinks.Count; index++)
        {
            var row = index / FavoriteColumns;
            var first = row * FavoriteColumns;
            var columns = Math.Min(FavoriteColumns, _favoriteLinks.Count - first);
            var x = 0.5 + (index - first - (columns - 1) / 2.0) * 0.118;
            var y = -FavoriteStripHeight + 0.072 + row * FavoriteRowHeight;
            positions.Add((_favoriteLinks[index], new Point(root.X + x * root.W, root.Y + y * root.W), root.W * 0.024));
        }
        return positions;
    }

    private void DrawFavoriteLinks(DrawingContext dc)
    {
        if (FavoriteStripHeight == 0 || RootRect() is not { } root || root.W < 100
            || root.Y <= 0 || root.Y - FavoriteStripHeight * root.W >= _viewHeight
            || root.X + root.W <= 0 || root.X >= _viewWidth) return;
        var title = Text("Favorites", Math.Min(18, root.W * 0.015), TextDimBrush, root.W, bold: false, scaled: true);
        DrawTextAt(dc, title, new Point(root.X + root.W * 0.03, root.Y - FavoriteStripHeight * root.W));
        foreach (var (link, centre, radius) in FavoriteLinkPositions())
        {
            var labelSize = Math.Min(17, root.W * 0.0135);
            var label = Text(link.Label, labelSize, TextBrush, root.W * 0.108, bold: false, scaled: true);
            var labelAt = new Point(centre.X - label.Width / 2, centre.Y + radius + root.W * 0.005);
            var bounds = new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);
            var labelBounds = new Rect(labelAt, new Size(label.Width, label.Height));
            if (bounds.Bottom < 0 || labelBounds.Top > _viewHeight || bounds.Right < 0 || bounds.Left > _viewWidth) continue;

            var colour = link.Colour;
            var tint = Color.FromArgb(0x38, colour.R, colour.G, colour.B);
            var rim = new Pen(BrushFor(colour), Math.Clamp(radius * 0.065, 1, 2));
            rim.Freeze();
            dc.DrawEllipse(BrushFor(tint), rim, centre, radius, radius);
            var glyph = Text("\uE71B", Math.Min(26, radius * 0.92), TextBrush, double.MaxValue, bold: false, icon: true, scaled: true);
            DrawTextAt(dc, glyph, new Point(centre.X - glyph.Width / 2, centre.Y - glyph.Height / 2));
            DrawTextAt(dc, label, labelAt);

            // A first click does not clear a chosen file. A double click is
            // navigation only, never OpenRequested or a file operation.
            Action navigate = () => FavoriteLinkRequested?.Invoke(link.Path);
            var tip = $"{link.Label}\n{link.Path}\nDouble-click to go to this folder";
            _hotspots.Add(new Hotspot(bounds, static () => { }, null, tip, navigate));
            _hotspots.Add(new Hotspot(labelBounds, static () => { }, null, tip, navigate));
        }
    }
}
