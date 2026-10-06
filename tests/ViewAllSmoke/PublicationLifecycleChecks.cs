using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PublicationLifecycleChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PublicationLifecycleChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(PublicationLifecycleChecks));
            return Task.CompletedTask;
        }

        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Publication lifecycle checks require genuinely isolated state and test-window mode.");
        PublicationScopePathChecks();
        RunOnSta("publication lifecycle, resource-only application", PublicationWindowChoiceChecks);
        return Task.CompletedTask;
    }

    private static void PublicationScopePathChecks()
    {
        Section("test-scope state isolation uses the app's actual resolved path");
        // Initialize the immutable StateDirectory on the owned profile BEFORE
        // temporarily testing other spellings. Resolve itself does no file I/O.
        var ownedState = AppPaths.StateDirectory;
        var resolve = typeof(AppPaths).GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var cases = new (string Name, string Raw, bool Isolated)[]
        {
            ("literal production root", real, false),
            ("expanded production root", @"%LOCALAPPDATA%\UltraExplorer", false),
            ("quoted expanded production root", "  \"%LOCALAPPDATA%\\UltraExplorer\"  ", false),
            ("case-insensitive production root", real.ToUpperInvariant(), false),
            ("production child with a separator boundary", Path.Combine(real, "nested-fixture"), false),
            ("trailing production separator", real + Path.DirectorySeparatorChar, false),
            ("same prefix but distinct sibling", real + "-isolated-fixture", true),
            ("owned state", ownedState, true)
        };
        try
        {
            foreach (var item in cases)
            {
                Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, item.Raw);
                var resolved = (string)resolve.Invoke(null, null)!;
                Check(item.Name + " is classified after canonical app resolution",
                    DialogFixtureProcessScope.IsIsolatedDirectory(resolved) == item.Isolated);
            }
            Check("temporary environment probes never retarget the immutable owned app state",
                AppPaths.StateDirectory == ownedState);
        }
        finally { Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, oldOverride); }
    }

    private static Task PublicationWindowChoiceChecks()
    {
        Section("direct native launch never routes into a closing, busy or picker window");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerPublicationLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        var main = new MainWindow();
        var other = new MainWindow();
        var picker = new MainWindow(new FileDialogSession(new FileDialogRequest
        {
            Mode = FileDialogMode.Open, InitialFolder = owned,
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.DontAddToRecent
        }));
        MainWindow[] windows = [main, picker, other];
        var starting = ExplorerLaunchRouter.StartSelf;
        var settingsPath = DialogIntegrationStore.SettingsPath;
        var oldSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            Check("all owned window objects remain unshown and have no native HWND",
                windows.All(window => new WindowInteropHelper(window).Handle == 0 && !window.IsVisible));
            Check("an available main window retains the existing direct-launch preference",
                ReferenceEquals(ExplorerLaunchRouter.ChooseDirectLaunchWindow(main, windows), main));
            typeof(MainWindow).GetField("_closeRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
            Check("a main window waiting for close routes to the other live normal window",
                ReferenceEquals(ExplorerLaunchRouter.ChooseDirectLaunchWindow(main, windows), other));
            Check("picker-only or closing-only candidates cannot consume a direct launch",
                ExplorerLaunchRouter.ChooseDirectLaunchWindow(main, [main, picker]) is null);
            var pending = typeof(MainWindow).GetField("_folderInvocationsPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            pending.SetValue(other, 1);
            Check("an occupied sibling is not reused for another request",
                ExplorerLaunchRouter.ChooseDirectLaunchWindow(main, windows) is null);
            pending.SetValue(other, 0);
            Check("once its earlier request finishes, the sibling is usable again",
                ReferenceEquals(ExplorerLaunchRouter.ChooseDirectLaunchWindow(main, windows), other));

            // No real launcher, broker, Registry or HWND is used. Intercept the
            // existing process seam and check the actual TryOpenFolder fallback.
            application.MainWindow = main;
            string[]? launched = null;
            ExplorerLaunchRouter.StartSelf = args => { launched = args; return Process.GetCurrentProcess(); };
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, "{\"Enabled\":true}");
            var accepted = ExplorerLaunchRouter.TryOpenFolder(owned);
            Check("with no registered live destination the usual launcher receives the exact folder instead of falsely accepting in the closing main",
                accepted && launched is not null && FolderCommandLine.TryParse(launched, out var invocation, out _)
                && invocation.FolderPath == owned && invocation.OriginIsShell);
        }
        finally
        {
            ExplorerLaunchRouter.StartSelf = starting;
            if (oldSettings is null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, oldSettings);
            foreach (var window in windows)
            {
                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close(); // Only the three owned, never-shown objects.
            }
            TryDelete(owned);
        }
        return Task.CompletedTask;
    }
}
