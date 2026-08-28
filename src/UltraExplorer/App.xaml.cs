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

        NodifyEditor.EnableDraggingContainersOptimizations = true;
        NodifyEditor.EnableSnappingCorrection = true;

        // Between the batched overview and full zoom the editor still renders
        // real containers; let it simplify them once they get small.
        NodifyEditor.EnableRenderingContainersOptimizations = true;
        NodifyEditor.OptimizeRenderingMinimumContainers = 200;
        NodifyEditor.OptimizeRenderingZoomOutPercent = 0.6;
        base.OnStartup(e);
    }
}
