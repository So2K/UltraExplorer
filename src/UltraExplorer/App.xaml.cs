using System.Runtime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nodify;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

using UltraExplorer.Services;
using UltraExplorer.Picker.Integration;

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
        SuppressDriveErrorBoxes();

        var isTestCopy = DialogIntegrationRuntime.IsRole(e.Args)
            || Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            || e.Args.Any(argument => argument.Equals("--nested-bench", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--nested-snapshots", StringComparison.OrdinalIgnoreCase));

        // Before anything that can fail: whatever nothing else catches is
        // written down, and survived where that is safe.  A benchmark,
        // snapshot or test copy never survives one: a run that carried on
        // after a failure would report timings and pixels of a broken window,
        // and a message box would wait on a window nobody is looking at.
        CrashReporter.Install(this, survive: !isTestCopy);

        // First, before any window exists: the id is read when a window is shown.
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

        // A dialog worker or proxy shows the canvas as the app's window does,
        // so its GPU is readied as the window's is, before the role runs: the
        // label atlases made and the card's pipelines warmed.  Left to the
        // first frame of a dialog, the GPU started without the atlases -
        // every name was drawn with WPF - and built its pipelines in that
        // frame.  The other roles show no canvas, and make no device.
        if (e.Args.Contains("--dialog-worker") || e.Args.Contains("--dialog-proxy"))
        {
            Rendering.Gpu.GpuBootstrap.WarmOtherAdapters = false;
            StartGpu();
        }

        if (DialogIntegrationRuntime.TryRun(this, e.Args)) return;

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

        var hasPreparedFolder = FastEntryPoint.TryTakePreparedFolder(e.Args, out var preparedFolder);
        FolderInvocation? folderInvocation = null;
        Mutex? folderStartup = null;
        if (!FileDialogCommandLine.IsPickerInvocation(e.Args)
            && (hasPreparedFolder || FolderCommandLine.IsInvocation(e.Args)))
        {
            FolderInvocation invocation;
            if (hasPreparedFolder)
            {
                invocation = preparedFolder;
            }
            else
            {
                if (ExplorerLaunchRouter.TryHandleShellFallback(e.Args))
                {
                    Shutdown(FileDialogCommandLine.ExitAccepted);
                    return;
                }

                if (!FolderCommandLine.TryParse(e.Args, out invocation, out var error))
                {
                    FileDialogHost.WriteConsole(error);
                    Shutdown(FileDialogCommandLine.ExitError);
                    return;
                }
            }

            folderInvocation = invocation;
            // Several folders opened together start a launch each: with no
            // window listening, the first builds one and the others wait to
            // hand it their folders, rather than each building its own.
            folderStartup = ExplorerLaunchRouter.EnterFolderStartup();
            if (ExplorerLaunchRouter.TryForward(invocation))
            {
                ExplorerLaunchRouter.LeaveFolderStartup(folderStartup, whenListening: false);
                Shutdown(FileDialogCommandLine.ExitAccepted);
                return;
            }
            if (invocation.OriginIsShell && ExplorerLaunchRouter.TryHandleShellFallback(e.Args))
            {
                ExplorerLaunchRouter.LeaveFolderStartup(folderStartup, whenListening: false);
                Shutdown(FileDialogCommandLine.ExitAccepted);
                return;
            }
        }

        // The tray's "Open settings" while a window is open is shown by that
        // window, not by a second UltraExplorer on the same workspace.
        if (folderInvocation is null && !FileDialogCommandLine.IsPickerInvocation(e.Args)
            && e.Args.Contains("--settings") && ExplorerLaunchRouter.TryForwardSettings())
        {
            Shutdown(FileDialogCommandLine.ExitAccepted);
            return;
        }

        StartJitProfile();
        StartGpu();
        if (FileDialogCommandLine.IsPickerInvocation(e.Args))
        {
            StartPicker(e.Args);
            return;
        }

        var window = folderInvocation is { DestinationId: var destination } && destination != Guid.Empty
            ? new MainWindow(null, ExplorerLaunchRouter.FolderWorkspacePath(destination)) : new MainWindow();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        MainWindow = window;
        if (folderInvocation is not null) window.FolderDestinationId = folderInvocation.DestinationId;
        if (folderInvocation is not null) window.PrepareFolderInvocation(folderInvocation);
        ExplorerLaunchRouter.Attach(window);
        ExplorerLaunchRouter.LeaveFolderStartup(folderStartup, whenListening: true);
        window.Show();
        if (folderInvocation is not null) _ = window.ApplyFolderInvocationAsync(folderInvocation);
        DialogIntegrationController.OnApplicationStarted();
        if (e.Args.Contains("--settings")) window.Loaded += (_, _) => window.OpenSettings();
    }

    /// <summary>
    /// A drive with no media in it - a card pulled out, an empty DVD drive -
    /// answers this process with an error, never with Windows' "There is no
    /// disk in the drive" box.  A process started at sign-in begins with those
    /// boxes on, and passes that to every worker and window it starts; a box
    /// would also hold the thread that touched the drive - a watch retrying,
    /// a right-click, a folder described or an icon read - until someone
    /// answers it.  Whatever else the process was started with is kept.
    /// </summary>
    internal static void SuppressDriveErrorBoxes()
    {
        const uint FailCriticalErrors = 0x0001, NoOpenFileErrorBox = 0x8000;
        _ = SetErrorMode(GetErrorMode() | FailCriticalErrors | NoOpenFileErrorBox);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

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

    /// <summary>
    /// True when <paramref name="name"/> is given as a switch (<c>--name</c>,
    /// <c>-name</c> or <c>/name</c>).  An argument without the dash is some
    /// switch's value, and a picker's arguments are all its own: without both
    /// rules <c>--pick --title Embedding</c> started an invisible COM server
    /// that exited with 0, "accepted", and <c>--file-name register-picker</c>
    /// rewrote the registry and printed that as the chosen path.
    /// </summary>
    private static bool HasSwitch(IReadOnlyList<string> args, string name)
    {
        if (FileDialogCommandLine.IsPickerInvocation(args))
        {
            return false;
        }

        foreach (var argument in args)
        {
            if (argument.Length > 1
                && (argument[0] is '-' or '/')
                && argument.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase))
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

        // The caller has its answer; the window's close-time save is let
        // finish before the process ends, a few seconds at most.
        await UltraExplorer.MainWindow.WhenClosingWindowsClosedAsync(this, TimeSpan.FromSeconds(3));
        Shutdown(FileDialogHost.ExitCodeFor(result));
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
