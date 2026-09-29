using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UltraExplorer;

/// <summary>
/// One pane of the nested canvas as the window lays it out: a header that
/// says where the pane is - shown in a split view, and collapsed while the
/// window has a single pane - the strip that filters the pane by name and
/// orders it, and the canvas.  Only the parts: what they do, and the tree
/// the canvas draws, are the pane's (<see cref="NestedPane"/>), which wires
/// itself to them.
/// </summary>
public partial class NestedPaneView : UserControl
{
    public NestedPaneView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The header for a split view, or none for a window with one pane; the
    /// accent along its top and brighter words for the pane being worked
    /// with, and the pane's name to automation, which cannot tell two
    /// canvases apart by what they draw.
    /// </summary>
    internal void ShowAsPane(bool isSplit, bool isActive)
    {
        PaneHeader.Visibility = isSplit ? Visibility.Visible : Visibility.Collapsed;
        PaneAccent.Background = isSplit && isActive ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        PaneLocation.Foreground = (Brush)FindResource(isActive ? "TextBrush" : "TextDimBrush");
    }

    /// <summary>The pane's place in the header: <paramref name="trail"/> in words, the whole path as its tip.</summary>
    internal void ShowLocation(string trail, string fullPath)
    {
        PaneLocation.Text = trail;
        PaneHeader.ToolTip = fullPath;
    }
}
