using System.Windows.Controls;

namespace UltraExplorer;

/// <summary>
/// One pane of the nested canvas as the window lays it out: a header that
/// says where the pane is - kept for a split view, and collapsed while the
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
}
