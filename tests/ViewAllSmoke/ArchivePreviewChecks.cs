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
    private static Task ArchivePreviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(ArchivePreviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(ArchivePreviewChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Archive preview checks require isolated state and test-window mode.");
        RunOnSta("explicit archive preview", ArchivePreviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ArchivePreviewOnStaAsync()
    {
        Section("archive Quick Look: real extraction, read-only provenance and obsolete requests");
        Check("the archive preview gate has its required bundled codec", ArchiveService.IsAvailable);
        if (!ArchiveService.IsAvailable) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var applicationNow = Application.Current!;
        var priorMain = applicationNow.MainWindow;
        var renderer = GpuBootstrap.Preference;
        var priorArchives = ArchiveService.BrowseArchives;
        var statePath = AppPaths.State("workspace.json");
        var priorState = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
        var priorTime = priorState is null ? (DateTime?)null : File.GetLastWriteTimeUtc(statePath);
        var root = Path.Combine(AppPaths.StateDirectory, "archive-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "owned.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("folder/first.cs").Open())) writer.Write("// first owned preview\r\n");
            using (var writer = new StreamWriter(archive.CreateEntry("folder/second.cs").Open())) writer.Write("// second owned preview\r\n");
        }
        var originalArchive = File.ReadAllBytes(zip);
        var first = Path.Combine(zip, "folder", "first.cs");
        var second = Path.Combine(zip, "folder", "second.cs");
        var ordinary = Path.Combine(root, "ordinary.txt");
        await File.WriteAllTextAsync(ordinary, "ordinary owned file");
        var windows = new List<MainWindow>();
        var releases = new List<TaskCompletionSource>();
        var pending = new List<Task>();
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deliveries = new List<(string Path, bool ReadOnly, string Origin)>();
        void Present(string path, bool readOnly, string origin)
        {
            deliveries.Add((path, readOnly, origin));
            outputs.Add(path);
        }
        async Task<MainWindow> Window(string name)
        {
            var owner = new MainWindow(null, Path.Combine(root, name + "-window"));
            windows.Add(owner);
            var model = (MainViewModel)owner.DataContext;
            await model.InitializeAsync(root);
            await owner.StartNestedForChecksAsync();
            model.BrowseArchives = true;
            await model.SaveNowAsync();
            return owner;
        }
        async Task<(Task Loading, TaskCompletionSource Release, CancellationToken Token)> Held(MainWindow owner, string path)
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(release);
            owner.ArchivePreviewExtractorForChecks = async (requested, token, prompt) =>
            {
                entered.TrySetResult(token);
                await release.Task;
                // Deliberately let the real backend finish after cancellation:
                // the bridge must reject stale delivery even when native work
                // did not stop at the same moment as the user's new action.
                var output = await ArchiveService.ExtractForOpenAsync(requested, null, CancellationToken.None, prompt);
                outputs.Add(output);
                return output;
            };
            var handled = owner.TryBeginArchiveQuickPreview(path, Present);
            Check("a held explicit archive request stays handled without presenting a nonexistent virtual path", handled);
            var loading = owner.ArchiveQuickPreviewLoadingForChecks;
            pending.Add(loading);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.ArchivePreviewExtractorForChecks = null;
            return (loading, release, token);
        }
        try
        {
            await new WorkspaceStore().SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
            var owner = await Window("main");
            var model = (MainViewModel)owner.DataContext;
            Check("an ordinary physical file is left to the normal Quick Look path", !owner.TryBeginArchiveQuickPreview(ordinary, Present) && deliveries.Count == 0);

            var classificationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var classificationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(classificationRelease);
            owner.ArchivePreviewClassifierForChecks = (requested, token) => Task.Run(async () =>
            {
                classificationEntered.TrySetResult(!owner.Dispatcher.CheckAccess());
                await classificationRelease.Task;
                return ArchiveService.IsInsideArchive(requested);
            }, token);
            Check("an archive candidate returns handled while its filesystem classification is pending", owner.TryBeginArchiveQuickPreview(first, Present));
            var classificationLoading = owner.ArchiveQuickPreviewLoadingForChecks;
            pending.Add(classificationLoading);
            var offDispatcher = await classificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispatcherAnswered = false;
            await owner.Dispatcher.InvokeAsync(() => dispatcherAnswered = true);
            Check("the UI dispatcher responds while archive-path filesystem classification is held off-thread",
                offDispatcher && dispatcherAnswered && !classificationLoading.IsCompleted && deliveries.Count == 0);
            classificationRelease.TrySetResult();
            await classificationLoading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("a completed background classification delivers the real read-only ZIP entry", deliveries is [var classified] && classified.ReadOnly && classified.Origin == first);
            owner.ArchivePreviewClassifierForChecks = null;
            deliveries.Clear();

            Check("an explicit archive file is handled by the bridge", owner.TryBeginArchiveQuickPreview(first, Present));
            await owner.ArchiveQuickPreviewLoadingForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            Check("real ZIP extraction presents complete physical bytes with read-only permission and the exact origin",
                deliveries is [var preview] && preview.ReadOnly && preview.Origin == first
                && File.Exists(preview.Path) && File.ReadAllText(preview.Path) == "// first owned preview\r\n"
                && !string.Equals(preview.Path, first, StringComparison.Ordinal));
            Check("explicit preview extraction preserves the source archive byte for byte", File.ReadAllBytes(zip).SequenceEqual(originalArchive));
            deliveries.Clear();

            var cancelled = await Held(owner, first);
            owner.CancelArchiveQuickPreview();
            cancelled.Release.TrySetResult();
            await cancelled.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("explicit cancellation stops the token and suppresses even a late successful extraction", cancelled.Token.IsCancellationRequested && deliveries.Count == 0);

            var ordinaryRequest = await Held(owner, first);
            Check("a new ordinary preview request returns false and cancels the older archive request", !owner.TryBeginArchiveQuickPreview(ordinary, Present) && ordinaryRequest.Token.IsCancellationRequested);
            ordinaryRequest.Release.TrySetResult();
            await ordinaryRequest.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("late archive completion cannot replace a newer ordinary preview", deliveries.Count == 0);

            var stale = await Held(owner, first);
            owner.TryBeginArchiveQuickPreview(second, Present);
            await owner.ArchiveQuickPreviewLoadingForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            stale.Release.TrySetResult();
            await stale.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("the newest archive request alone delivers its own read-only entry", stale.Token.IsCancellationRequested
                && deliveries is [var newest] && newest.Origin == second && newest.ReadOnly
                && File.ReadAllText(newest.Path) == "// second owned preview\r\n");
            deliveries.Clear();

            var disabled = await Held(owner, first);
            model.BrowseArchives = false;
            disabled.Release.TrySetResult();
            await disabled.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("turning archive browsing off cancels pending extraction and rejects its eventual delivery", disabled.Token.IsCancellationRequested && deliveries.Count == 0);
            var noExtract = false;
            owner.ArchivePreviewExtractorForChecks = (_, _, _) => { noExtract = true; return Task.FromResult(ordinary); };
            var offHandled = owner.TryBeginArchiveQuickPreview(first, Present);
            await owner.ArchiveQuickPreviewLoadingForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            Check("OFF classifies and handles the virtual path without starting any extraction", offHandled && !noExtract && deliveries.Count == 0);
            var physicalArchiveHandled = owner.TryBeginArchiveQuickPreview(zip, Present);
            await owner.ArchiveQuickPreviewLoadingForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            Check("a real ZIP file remains normally previewable while archive browsing is OFF", physicalArchiveHandled
                && deliveries is [var physicalArchive] && physicalArchive.Path == zip && !physicalArchive.ReadOnly
                && physicalArchive.Origin.Length == 0 && !noExtract);
            deliveries.Clear();
            owner.ArchivePreviewExtractorForChecks = null;
            model.BrowseArchives = true;
            await model.SaveNowAsync();

            var changedSelection = await Held(owner, first);
            model.Tree.Selection.Apply(new SelectionEdit { Clear = true, Added = [new SelectionItem(ordinary, false, 1)], Focus = ordinary, Source = SelectionSource.Command });
            changedSelection.Release.TrySetResult();
            await changedSelection.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("changing selection cancels the pending request instead of opening its old file", changedSelection.Token.IsCancellationRequested && deliveries.Count == 0);

            model.IsSplit = true;
            await owner.StartNestedForChecksAsync();
            Check("the pane cancellation fixture has two real owned panes", owner.SecondPane is not null);
            if (owner.SecondPane is { } other)
            {
                var changedPane = await Held(owner, first);
                owner.ActivatePane(other);
                changedPane.Release.TrySetResult();
                await changedPane.Loading.WaitAsync(TimeSpan.FromSeconds(10));
                Check("switching the active pane cancels the pending archive preview", changedPane.Token.IsCancellationRequested && deliveries.Count == 0);
            }

            var reentered = false;
            owner.TryBeginArchiveQuickPreview(first, (physical, readOnly, origin) =>
            {
                Present(physical, readOnly, origin);
                reentered = !owner.TryBeginArchiveQuickPreview(physical, Present);
            });
            await owner.ArchiveQuickPreviewLoadingForChecks.WaitAsync(TimeSpan.FromSeconds(10));
            Check("a delivered physical preview can reenter the ordinary path without cancelling or duplicating its delivery", reentered && deliveries.Count == 1 && deliveries[0].ReadOnly);
            deliveries.Clear();

            var disposedOwner = await Window("disposed");
            var disposed = await Held(disposedOwner, first);
            ((MainViewModel)disposedOwner.DataContext).Dispose();
            disposed.Release.TrySetResult();
            await disposed.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("a disposed model cannot receive late extraction results", deliveries.Count == 0);

            var closing = await Held(owner, first);
            owner.Close();
            closing.Release.TrySetResult();
            await closing.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            Check("closing an unshown owner cancels extraction and never opens a late preview", closing.Token.IsCancellationRequested && deliveries.Count == 0);
            Check("the bridge fixtures never show a window or create a Quick Look window", windows.All(window => !window.IsVisible)
                && !applicationNow.Windows.OfType<QuickPreviewWindow>().Any());
            Check("all preview and cancellation routes retain the original archive bytes", File.ReadAllBytes(zip).SequenceEqual(originalArchive));
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            foreach (var owner in windows) owner.CancelArchiveQuickPreview();
            if (pending.Count > 0) await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var owner in windows)
            {
                owner.ArchivePreviewExtractorForChecks = null;
                owner.ArchivePreviewClassifierForChecks = null;
                var model = (MainViewModel)owner.DataContext;
                if (!model.IsDisposed) await model.SaveNowAsync();
                owner.Close();
            }
            await SettingsSettle();
            foreach (var owner in windows) ((MainViewModel)owner.DataContext).Dispose();
            foreach (var path in outputs)
                try { File.Delete(path); } catch (IOException) { }
            ArchiveService.BrowseArchives = priorArchives;
            GpuBootstrap.UseSavedPreference(renderer);
            applicationNow.MainWindow = priorMain;
            if (priorState is not null) { await File.WriteAllBytesAsync(statePath, priorState); File.SetLastWriteTimeUtc(statePath, priorTime!.Value); }
            else if (File.Exists(statePath)) File.Delete(statePath);
            TryDelete(root);
        }
    }
}
