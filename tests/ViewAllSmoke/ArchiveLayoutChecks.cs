using System.IO;
using System.IO.Compression;
using System.Windows;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ArchiveLayoutChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(ArchiveLayoutChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(ArchiveLayoutChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Archive layout checks require isolated state and test-window mode.");
        RunOnSta("archive layout transitions", ArchiveLayoutOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ArchiveLayoutOnStaAsync()
    {
        Section("archives: switch between nested and physical tree views");
        Check("archive layout fixtures have their bundled codec", ArchiveService.IsAvailable);
        if (!ArchiveService.IsAvailable) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var priorMain = Application.Current!.MainWindow;
        var priorRenderer = GpuBootstrap.Preference;
        var priorArchives = ArchiveService.BrowseArchives;
        var workspace = AppPaths.State("workspace.json");
        var priorState = File.Exists(workspace) ? File.ReadAllBytes(workspace) : null;
        var priorTime = priorState is null ? (DateTime?)null : File.GetLastWriteTimeUtc(workspace);
        var root = Path.Combine(AppPaths.StateDirectory, "archive-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "owned.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("folder/owned.txt").Open())) writer.Write("owned fixture");
        var original = File.ReadAllBytes(zip);
        MainWindow? main = null;
        MainViewModel? model = null;
        try
        {
            await new WorkspaceStore().SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", CanvasLayout = "Nested" });
            GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
            main = new MainWindow(null, Path.Combine(root, "owned-window"));
            model = (MainViewModel)main.DataContext;
            await model.InitializeAsync(root);
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(root, "Owned fixture", NestedFolderKind.Drive)]);
            LayOutWindow(main, 1400, 900);
            model.BrowseArchives = true;
            var pane = main.ActivePane;
            var physical = pane.Tree.Find(root)!;
            await pane.Tree.LoadAsync(physical);
            var inner = await pane.Tree.MaterializePathAsync(Path.Combine(zip, "folder"));
            Check("nested mode reads the virtual archive folder", inner is { IsInArchive: true } && pane.Tree.ArchivesEnabled);
            if (inner is null) return;
            await pane.Tree.LoadAsync(inner);
            pane.Canvas.FlyTo(inner, animated: false);
            var virtualFile = Path.Combine(zip, "folder", "owned.txt");
            model.Tree.Selection.ReplaceSingle(virtualFile, false, 13, SelectionSource.Canvas);
            Check("the owned nested camera and selection are inside the archive", pane.Canvas.FolderInView?.IsInArchive == true
                && model.Tree.Selection.Focus == virtualFile);

            model.Layout = CanvasLayout.Tree;
            await main.TreeEntryForChecks;
            await SettingsSettle();
            Check("tree mode suspends archive classification without changing the saved ON preference", model.BrowseArchives
                && !model.Tree.BrowseArchives && !pane.Tree.ArchivesEnabled);
            Check("tree transition leaves the virtual camera and clears virtual selection and focus", pane.Canvas.FolderInView?.IsInArchive != true
                && model.Tree.Selection.Paths.All(path => !ArchiveService.IsInsideArchive(path))
                && !ArchiveService.IsInsideArchive(model.Tree.Selection.Focus ?? string.Empty)
                && !ArchiveService.IsInsideArchive(model.Tree.FocusedPath));
            var archiveNode = await model.Tree.MaterializeAsync(zip);
            Check("the physical tree treats the archive itself as an ordinary file", archiveNode is { IsDirectory: false });
            model.Tree.Selection.ReplaceSingle(zip, true, 0, SelectionSource.Canvas);
            pane.ApplyArchivePreference();
            Check("an archive selected with an old folder kind becomes a file in tree mode", model.Tree.Selection.TryGetItem(zip, out var selected) && !selected.IsDirectory);

            model.Layout = CanvasLayout.Nested;
            await SettingsSettle();
            await pane.Tree.RefreshAsync(physical);
            var again = await pane.Tree.MaterializePathAsync(Path.Combine(zip, "folder"));
            Check("returning to nested mode re-enables archive folders without changing the preference", model.BrowseArchives
                && model.Tree.BrowseArchives && pane.Tree.ArchivesEnabled && again is { IsInArchive: true });
            Check("layout changes keep the original archive bytes", File.ReadAllBytes(zip).SequenceEqual(original));
        }
        finally
        {
            main?.Close();
            await SettingsSettle();
            model?.Dispose();
            Application.Current!.MainWindow = priorMain;
            ArchiveService.BrowseArchives = priorArchives;
            GpuBootstrap.UseSavedPreference(priorRenderer);
            if (priorState is not null) { await File.WriteAllBytesAsync(workspace, priorState); File.SetLastWriteTimeUtc(workspace, priorTime!.Value); }
            else if (File.Exists(workspace)) File.Delete(workspace);
            TryDelete(root);
        }
    }
}
