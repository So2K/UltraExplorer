using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nodify;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

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

        if (HasSwitch(e.Args, "register-picker"))
        {
            FileDialogHost.WriteConsole(ComServerRegistration.Register(ExecutablePath));
            Shutdown(FileDialogCommandLine.ExitAccepted);
            return;
        }

        if (HasSwitch(e.Args, "unregister-picker"))
        {
            FileDialogHost.WriteConsole(ComServerRegistration.Unregister());
            Shutdown(FileDialogCommandLine.ExitAccepted);
            return;
        }

        // COM starts the server with -Embedding; a person testing it uses the
        // long switch.  Neither shows a window until a client asks for one.
        if (HasSwitch(e.Args, "com-server") || HasSwitch(e.Args, "embedding"))
        {
            if (!ComServerHost.Start(this))
            {
                FileDialogHost.WriteConsole("Could not register the dialog classes with COM.");
                Shutdown(FileDialogCommandLine.ExitError);
            }

            return;
        }

        if (FileDialogCommandLine.IsPickerInvocation(e.Args))
        {
            StartPicker(e.Args);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Where this build lives, for the COM registration to point at.  A
    /// single-file app has no assembly location to fall back on, so the app
    /// directory is the answer when the process path is unavailable.
    /// </summary>
    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "UltraExplorer.exe");

    private static bool HasSwitch(IReadOnlyList<string> args, string name)
    {
        foreach (var argument in args)
        {
            if (argument.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Another program asked us to pick something for it.  The window is the
    /// ordinary one with a dialog footer; the difference is that this process
    /// exists only to answer, so it shuts down explicitly once it has.
    /// </summary>
    private void StartPicker(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (FileDialogCommandLine.WantsHelp(args))
        {
            FileDialogHost.WriteConsole(FileDialogCommandLine.Usage);
            Shutdown(FileDialogCommandLine.ExitAccepted);
            return;
        }

        if (!FileDialogCommandLine.TryParse(args, out var invocation, out var error))
        {
            FileDialogHost.WriteConsole($"{error}{Environment.NewLine}{Environment.NewLine}{FileDialogCommandLine.Usage}");
            Shutdown(FileDialogCommandLine.ExitError);
            return;
        }

        var session = new FileDialogSession(invocation.Request);
        var window = new MainWindow(session);
        FileDialogHost.AttachOwner(window, invocation.Request.OwnerHandle);

        MainWindow = window;
        window.Show();
        window.Activate();

        _ = AnswerCallerAsync(window, invocation);
    }

    private async Task AnswerCallerAsync(MainWindow window, PickerInvocation invocation)
    {
        FileDialogResult result;
        try
        {
            result = await window.PickerResult;
        }
        catch (Exception exception)
        {
            result = FileDialogResult.Failed(exception.Message);
        }

        FileDialogHost.Deliver(invocation, result);
        Shutdown(FileDialogHost.ExitCodeFor(result));
    }
}
