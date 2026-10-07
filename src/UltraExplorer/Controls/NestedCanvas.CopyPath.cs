using System.Windows;
using System.Windows.Media;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// The copy-path button: on the folder or file under the pointer, at the
/// right end of a folder's title band or of a file's tile, a small button
/// that puts the item's full path on the clipboard.  Drawn on the pointer's
/// own layer, so it costs the canvas nothing while the mouse is elsewhere,
/// and only where there is room for it to be pressed.
/// </summary>
public partial class NestedCanvas
{
    private static readonly Brush CopyButtonHotBrush = Frozen(Color.FromRgb(0x2F, 0x4E, 0x7A));

    private Rect _copySpot = Rect.Empty;
    private string? _copyPath;

    /// <summary>The copy-path button was pressed: the path to put on the clipboard.</summary>
    public event Action<string>? CopyPathRequested;

    /// <summary>Whether the point is on the copy-path button now shown.</summary>
    private bool IsOnCopyButton(Point point) => _copyPath is not null && _copySpot.Contains(point);

    /// <summary>Presses the copy-path button if the point is on it; true when it was.</summary>
    private bool TryPressCopyButton(Point point)
    {
        if (!IsOnCopyButton(point))
        {
            return false;
        }

        CopyPathRequested?.Invoke(_copyPath!);
        return true;
    }

    private void DrawCopyButton(DrawingContext dc)
    {
        _copySpot = Rect.Empty;
        _copyPath = null;
        if (_hover is not { } hover || _press != PressKind.None || hover.Folder.IsComputer || _marquee is not null)
        {
            return;
        }

        Rect button;
        if (hover.IsFile)
        {
            var tile = hover.Bounds;
            if (tile.Height < 16 || tile.Width < 60)
            {
                return;
            }

            var size = Math.Clamp(tile.Height * 0.62, 14, 20);
            button = new Rect(tile.Right - size - 3, tile.Top + (tile.Height - size) / 2, size, size);
        }
        else
        {
            // A folder whose title is drawn and on screen: the button sits at
            // its right end, inside the band.
            if (!_labelled.Contains(hover.Folder) || TargetRect(hover.Folder, -1, place: false) is not { } cell)
            {
                return;
            }

            var header = cell.Width * NestedLayout.HeaderHeight;
            if (header < 14 || cell.Width < 100 || cell.Top < -header * 0.5 || cell.Top > _viewHeight)
            {
                return;
            }

            var size = Math.Clamp(header * 0.7, 14, 24);
            var right = Math.Min(cell.Right, _viewWidth);
            button = new Rect(right - size - Math.Max(4, header * 0.2), Math.Max(cell.Top, 0) + (header - size) / 2, size, size);
        }

        var hot = button.Contains(_hoverPoint);
        dc.DrawRoundedRectangle(hot ? CopyButtonHotBrush : TipBrush, TipPen, button, 4, 4);
        var glyph = Text("", button.Height * 0.5, TextBrush, double.MaxValue, bold: false, icon: true);
        DrawTextAt(dc, glyph, new Point(button.X + (button.Width - glyph.Width) / 2, button.Y + (button.Height - glyph.Height) / 2));
        if (hot)
        {
            var tip = Text("Copy path", 11, TextBrush, 200, bold: false);
            var box = new Rect(button.Left - tip.Width - 16, button.Y + (button.Height - tip.Height - 6) / 2, tip.Width + 10, tip.Height + 6);
            dc.DrawRoundedRectangle(TipBrush, TipPen, box, 4, 4);
            DrawTextAt(dc, tip, new Point(box.X + 5, box.Y + 3));
        }

        _copySpot = button;
        _copyPath = hover.Path;
    }
}
