using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ArchiveQuickLookWindowChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(ArchiveQuickLookWindowChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(ArchiveQuickLookWindowChecks));
            return Task.CompletedTask;
        }
        RunOnSta("archive Quick Look host", ArchiveQuickLookWindowOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ArchiveQuickLookWindowOnStaAsync()
    {
        Section("archive Quick Look: actual host and read-only window integration");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check("archive window integration requires an owned isolated profile", isolated);
        if (!isolated) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var root = Path.Combine(AppPaths.StateDirectory, "archive-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "source.zip");
        const string content = "// owned archive text\r\nclass Example { }\r\n";
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("code.cs").Open())) writer.Write(content);
        var archiveBefore = File.ReadAllBytes(archivePath);
        var virtualPath = Path.Combine(archivePath, "code.cs");
        var main = new MainWindow(null, Path.Combine(root, "view.json")) { SuppressQuickPreviewPresentationForChecks = true };
        var shell = (MainViewModel)main.DataContext;
        try
        {
            new WindowInteropHelper(main).EnsureHandle();
            await shell.InitializeAsync(root);
            await main.StartNestedForChecksAsync();
            shell.BrowseArchives = true;
            main.OpenQuickPreview(virtualPath);
            await main.ArchiveQuickPreviewLoadingForChecks;
            var preview = main.QuickPreviewForChecks ?? throw new InvalidOperationException("The explicit archive request did not create its preview.");
            await preview.Loading;
            var physical = preview.FilePath;
            var editor = QuickDescendants((DependencyObject)preview.Content).OfType<TextEditor>().Single();
            Check("the host resolves an archive entry to complete physical bytes", physical != virtualPath && File.ReadAllText(physical) == content);
            Check("the actual archive text remains read-only with its source origin", editor.IsReadOnly && preview.SourceOriginForChecks == virtualPath && !preview.HasUnsavedChanges);
            var deleteCalls = 0;
            await preview.DeleteCurrentAsync(_ => { deleteCalls++; return Task.CompletedTask; });
            Check("archive permission blocks the actual delete command boundary", deleteCalls == 0 && File.Exists(physical));
            await preview.RefreshTextAfterActivationAsync();
            await preview.Loading;
            Check("activation refresh retains archive origin and read-only permission", preview.SourceOriginForChecks == virtualPath && editor.IsReadOnly && !preview.HasUnsavedChanges);
            Check("closing read-only preview never saves into its extracted copy", await preview.CommitForCloseAsync() && File.ReadAllText(physical) == content);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            main.ArchivePreviewExtractorForChecks = (_, _, _) => { entered.TrySetResult(); return delayed.Task; };
            main.OpenQuickPreview(virtualPath);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var loading = main.ArchiveQuickPreviewLoadingForChecks;
            preview.Close();
            await SettingsSettle();
            delayed.TrySetResult(physical);
            await loading;
            Check("closing the actual Quick Look window prevents late extraction from reopening it", main.QuickPreviewForChecks is null);
            Check("integration leaves the original archive unchanged", File.ReadAllBytes(archivePath).SequenceEqual(archiveBefore));
            Check("the host and child fixtures never show a foreground window", !main.IsVisible && !preview.IsVisible);
        }
        finally
        {
            main.ArchivePreviewExtractorForChecks = null;
            main.QuickPreviewForChecks?.Close();
            main.CloseFromCaller();
            await SettingsSettle();
            shell.Dispose();
            TryDelete(root);
        }
    }
}
