using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task NormalLazyOverviewWindowChecksAsync()
    {
        Section("normal window: partial folders fill automatically after the camera settles");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerNormalIdle", Guid.NewGuid().ToString("N"));
        var app = Application.Current;
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (var mode in new[] { "main", "open-folder", "home" })
                await NormalLazyOverviewCaseAsync(root, mode);
        }
        finally
        {
            app.ShutdownMode = shutdown;
            TryDelete(root);
        }
    }

    private static async Task NormalLazyOverviewCaseAsync(string root, string mode)
    {
        var fixture = Path.Combine(root, mode);
        var drive = Path.Combine(fixture, "C");
        var secondDrive = Path.Combine(fixture, "D");
        var users = Path.Combine(drive, "Users");
        var profile = Path.Combine(users, "Fixture");
        var leaf = Path.Combine(profile, ".codex", "long-lived");
        var windows = Path.Combine(drive, "Windows");
        var programs = Path.Combine(drive, "Program Files");
        foreach (var folder in new[] { leaf, windows, programs, Path.Combine(secondDrive, "Reports") })
            Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(leaf, "visible.txt"), "owned fixture");
        var target = mode == "home" ? profile : leaf;
        FolderInvocation? invocation = null;
        if (mode == "open-folder")
        {
            Check("--open-folder parses the owned destination", FolderCommandLine.TryParse(["--open-folder", target], out var parsed, out _));
            invocation = parsed;
        }
        else if (mode == "home")
        {
            Check("--home parses as a normal folder launch", FolderCommandLine.TryParse(["--home"], out var parsed, out _)
                && parsed.Kind == FolderInvocationKind.OpenFolder);
            // Keep the production home request shape while every content read
            // and directory change below stays in the owned profile fixture.
            invocation = parsed with { FolderPath = target };
        }

        MainWindow? window = null;
        try
        {
            window = new MainWindow(null, Path.Combine(fixture, "workspace.json"))
            { Width = 1280, Height = 860, WindowState = WindowState.Normal };
            var pane = window.ActivePane;
            var tree = pane.Tree;
            // Startup only discovers machine root metadata. Its contents are
            // disabled until the fixture replaces those roots with its own.
            tree.IsReadingOnDemand = false;
            if (invocation is not null) window.PrepareFolderInvocation(invocation);
            window.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using (ActivationGuard.GuardWindowsCreated()) window.Show();
            Check(mode + ": the real normal WPF window becomes ready", await WaitNormalIdleAsync(() => window.IsFolderWindowReady));
            var drives = new[]
            {
                new NestedRoot(drive, "Local Disk (C:)", NestedFolderKind.Drive),
                new NestedRoot(secondDrive, "Data (D:)", NestedFolderKind.Drive)
            };
            window.UseNestedDrivesForChecks(drives);
            pane.Canvas.FitAll(animated: false);
            if (invocation is not null)
                Check(mode + ": normal launch frames its requested target", await window.ApplyFolderInvocationAsync(invocation));
            else
            {
                await ((MainViewModel)window.DataContext).Tree.RevealPathAsync(target);
                await pane.FlyToAsync(target, gentle: false, animated: false);
            }
            Check(mode + ": the initial target loads without enumerating its sparse drive",
                await WaitNormalIdleAsync(() => tree.Find(target) is { IsLoaded: true })
                && tree.Find(drive) is { HasPartialListing: true, IsLoaded: false });
            Check(mode + ": the normal window restores its visible-root read policy after target framing",
                !window.IsPickerMode && pane.Canvas.LoadUnfocusedRoots);

            var partial = tree.Find(mode == "home" ? users : profile)!;
            Check(mode + ": the next viewed ancestor starts as a partial listing", partial is { HasPartialListing: true, IsLoaded: false });
            tree.IsReadingOnDemand = true;
            if (mode == "main")
            {
                var correctedPolicy = tree.PartialListingReadAllowed;
                tree.PartialListingReadAllowed = folder => !pane.Canvas.IsCameraMoving
                    && pane.Canvas.Anchor is { IsComputer: false } anchor
                    && MainWindow.IsNestedPathInside(folder.FullPath, anchor.FullPath);
                ((MainViewModel)window.DataContext).FitAllCommand.Execute(null);
                Check("the former policy reproduces a settled sparse Users-only drive with no Windows or Program Files",
                    await WaitNormalIdleAsync(() => pane.Canvas.IsIdle)
                    && tree.Find(drive) is { HasPartialListing: true, IsLoaded: false } sparse
                    && sparse.Children.Select(child => child.Name).SequenceEqual(["Users"]));
                SaveNormalIdleShot(window, "normal-idle-main-before-fix.png");
                tree.PartialListingReadAllowed = correctedPolicy;
            }
            var before = pane.Canvas.LoopFrameCount;
            pane.Canvas.FlyTo(partial, 0.92, animated: false);
            // No further input, explicit folder loads or manual rendering:
            // the actual WPF frame loop must retry when motion has settled.
            Check(mode + ": a settled partial folder fills automatically without a click",
                await WaitNormalIdleAsync(() => partial.IsLoaded && !partial.HasPartialListing)
                && pane.Canvas.LoopFrameCount > before && tree.Find(drive) is { IsLoaded: false });

            ((MainViewModel)window.DataContext).FitAllCommand.Execute(null);
            Check(mode + ": the idle This PC overview automatically lists all visible drive contents",
                await WaitNormalIdleAsync(() => tree.Find(drive) is { IsLoaded: true } c
                    && c.Children.Any(child => child.Name == "Windows") && c.Children.Any(child => child.Name == "Program Files")
                    && tree.Find(secondDrive) is { IsLoaded: true } d && d.Children.Any(child => child.Name == "Reports")));
            Check(mode + ": physical navigation never duplicates its target as a root",
                tree.Root.AllChildren.Select(folder => folder.FullPath).SequenceEqual(drives.Select(folder => folder.FullPath)));
            Check(mode + ": the normal canvas reaches idle after its deferred reads finish",
                await WaitNormalIdleAsync(() => pane.Canvas.IsIdle));
            SaveNormalIdleShot(window, "normal-idle-" + mode + ".png");
        }
        finally { window?.Close(); }
    }

    private static async Task<bool> WaitNormalIdleAsync(Func<bool> ready)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        return ready();
    }

    private static void SaveNormalIdleShot(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("PICKER_SHOTS") is not { Length: > 0 } output) return;
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }
}
