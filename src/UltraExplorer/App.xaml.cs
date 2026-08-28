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
        base.OnStartup(e);
    }
}
