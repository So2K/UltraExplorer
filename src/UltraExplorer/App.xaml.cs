using System.Runtime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nodify;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

using UltraExplorer.Services;

namespace UltraExplorer;

public partial class App : Application
{
    /// <summary>
    /// The taskbar identity of the everyday copy.  The Start menu shortcut
    /// that scripts/install.ps1 makes carries the same id, so a pinned button
    /// and the running window are one taskbar entry.
    /// </summary>
    internal const string AppUserModelId = "UltraExplorer.App";

    /// <summary>
    /// The taskbar identity of benchmark, snapshot and test copies.  Being a
    /// different id keeps them out of the user's taskbar group: clicking or
    /// pinning a test copy's button can never re-point the user's own pin at
    /// a throwaway build, which is what happened when they shared one.
    /// </summary>
    internal const string TestAppUserModelId = "UltraExplorer.Test";

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, before any window exists: the id is read when a window is shown.
        var isTestCopy = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            || e.Args.Any(argument => argument.Equals("--nested-bench", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--nested-snapshots", StringComparison.OrdinalIgnoreCase));
        _ = SetCurrentProcessExplicitAppUserModelID(isTestCopy ? TestAppUserModelId : AppUserModelId);

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

        // Deliberately off as well.  Nodify caches the whole container host into
        // one bitmap below a zoom threshold; with this window's minimum zoom that
        // band is 30-56%, exactly where the app still renders real containers,
        // and the surface it asks for is the viewport divided by the zoom - large
        // enough to exceed what the graphics stack will cache, which is what
        // turned the canvas black mid-drag.  Culling and the batched overview
        // layer already do this job, better and at every zoom.
        NodifyEditor.EnableRenderingContainersOptimizations = false;
        PerfLog.Configure(Environment.GetCommandLineArgs());

        // Before any menu exists: the Shell's context menus, and every other
        // native menu of the process, come out dark like the window.
        DarkMenus.UseForProcess();

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

        StartJitProfile();
        StartGpu();
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
    /// Gets the nested canvas's GPU ready while the window is still being
    /// built, on threads of their own: the atlases the names are drawn from
    /// (glyphs and icons, from their caches on disk), and the card that
    /// drives the monitor the window opens on - its devices, the shaders
    /// loaded or compiled, the pipelines, its copies of the atlases, and one
    /// frame through every pipeline drawn offscreen - so the first frame the
    /// user sees, and the first zoom, find everything warm.  The canvas draws
    /// on the CPU until then and moves over at the next frame, and draws on
    /// the CPU for good when that renderer is chosen (<c>--renderer cpu</c>,
    /// <c>ULTRAEXPLORER_RENDERER=cpu</c>, or Canvas options, Renderer,
    /// Processor).  Chosen, nothing of the GPU is made at all - the saved
    /// setting is read here, before the workspace is, because someone who
    /// picked the processor may have done so because the graphics path
    /// misbehaves on their driver - and a later switch back starts it all.
    /// </summary>
    private static void StartGpu()
    {
        if (Rendering.Gpu.GpuBootstrap.IsStarted)
        {
            return;
        }

        if (Rendering.Gpu.GpuBootstrap.ParsePreference(WorkspaceStore.PeekCanvasRenderer()) is { } saved)
        {
            Rendering.Gpu.GpuBootstrap.UseSavedPreference(saved);
        }

        // In this order: a card's renderer draws its warm-up frame from the
        // atlases' textures the step before makes on it.  Registered even
        // for the CPU: choosing the GPU later starts the warm-up with them.
        Rendering.Gpu.GpuLabelAtlases.RegisterWarmUp();
        Rendering.Gpu.NestedGpuRenderer.RegisterWarmUp();
        if (Rendering.Gpu.GpuBootstrap.Preference == Rendering.Gpu.RendererPreference.Cpu)
        {
            return;
        }

        // Where WPF itself does not draw on the card - a remote session, or
        // software rendering asked for - the automatic choice is the CPU, so
        // nothing is prepared that would not be used.  Should the session
        // turn local, the canvas's first frame that could use the GPU starts
        // it all (GpuBootstrap.Decide).
        if (Rendering.Gpu.GpuBootstrap.Preference == Rendering.Gpu.RendererPreference.Auto
            && (SystemParameters.IsRemoteSession || RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly))
        {
            return;
        }

        Rendering.Gpu.GpuLabelAtlases.StartWarmUp();
        Rendering.Gpu.GpuBootstrap.Start(UltraExplorer.MainWindow.StartupMonitor());
    }

    /// <summary>
    /// Multicore JIT: the methods this run compiles are recorded in the state
    /// folder, and the next start compiles them on spare cores before the UI
    /// thread asks for them - the frame loop, the walk, the label target -
    /// so the first frames of a session run optimised code, not stubs.
    /// </summary>
    private static void StartJitProfile()
    {
        try
        {
            var folder = AppPaths.State("jit");
            Directory.CreateDirectory(folder);
            ProfileOptimization.SetProfileRoot(folder);
            ProfileOptimization.StartProfile("startup.jitprofile");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the folder there is no profile: the JIT compiles as it
            // always has.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The cards first - their textures read the atlases - then what the
        // atlases learned this run, for the next start.
        Rendering.Gpu.GpuBootstrap.Shutdown();
        Rendering.Gpu.GpuLabelAtlases.Shutdown();
        base.OnExit(e);
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

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
