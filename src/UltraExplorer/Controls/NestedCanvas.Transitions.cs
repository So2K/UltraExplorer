using System.Runtime.CompilerServices;
using UltraExplorer.Models;

namespace UltraExplorer.Controls;

// Where a folder's children and files are on screen, worked out in one
// place.  Everything that draws a child, outlines it or walks the camera's
// chain through it asks here rather than reading the child's offsets itself,
// so a child that is moving from one place to another - a folder read again
// that gained a sub-folder, its files sliding to make room - moves everywhere
// at once, the camera's anchor staying put while its surroundings slide.
// Hit-testing is the exception, and reads the final places directly: a click
// always means what is about to be there.
public sealed partial class NestedCanvas
{
    /// <summary>
    /// <paramref name="child"/>'s rectangle on screen, from its parent's:
    /// its place in the parent's unit frame, scaled to the parent's width.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Place(NestedFolder parent, NestedFolder child, double px, double py, double pw, out double x, out double y, out double w)
    {
        x = px + child.OffsetX * pw;
        y = py + child.OffsetY * pw;
        w = pw * child.Scale;
    }

    /// <summary>
    /// The other way round: the parent's rectangle on screen from
    /// <paramref name="child"/>'s, for the walks up from the camera's anchor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PlaceParent(NestedFolder child, double x, double y, double w, out double px, out double py, out double pw)
    {
        var parentWidth = w / child.Scale;
        px = x - child.OffsetX * parentWidth;
        py = y - child.OffsetY * parentWidth;
        pw = parentWidth;
    }

    /// <summary>
    /// The tile of the file at <paramref name="index"/> among
    /// <paramref name="folder"/>'s shown files, on screen, from the folder's
    /// own rectangle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PlaceFile(NestedFolder folder, int index, double x, double y, double w, out double fx, out double fy, out double fw, out double fh)
    {
        var grid = folder.FileGrid;
        var (left, top) = grid.Origin(index);
        fx = x + left * w;
        fy = y + top * w;
        fw = grid.TileWidth * w;
        fh = grid.TileHeight * w;
    }
}
