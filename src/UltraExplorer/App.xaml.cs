using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nodify;

namespace UltraExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(Timeline),
            new FrameworkPropertyMetadata(60));

        // Deliberately off: this defers committing a dragged container's position
        // until the drag ends, so while dragging nothing downstream of the view
        // model moves - not the carried subtree, not the spatial index, and not
        // the batched canvas. Committing live is what makes a drag look live.
        NodifyEditor.EnableDraggingContainersOptimizations = false;
        NodifyEditor.EnableSnappingCorrection = true;

        // Between the batched overview and full zoom the editor still renders
        // real containers; let it simplify them once they get small.
        NodifyEditor.EnableRenderingContainersOptimizations = true;
        NodifyEditor.OptimizeRenderingMinimumContainers = 200;
        NodifyEditor.OptimizeRenderingZoomOutPercent = 0.6;
        base.OnStartup(e);
    }
}
