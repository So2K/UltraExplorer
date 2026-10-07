using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task OwnerClosePreviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(OwnerClosePreviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(OwnerClosePreviewChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Owner close preview checks require isolated state and test-window mode.");
        RunOnSta("owner close preview transaction", OwnerClosePreviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task OwnerClosePreviewOnStaAsync()
    {
        Section("owner close: note input and actions stay frozen across the real workspace save");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var app = Application.Current!;
        var priorMain = app.MainWindow;
        var renderer = GpuBootstrap.Preference;
        var statePath = AppPaths.State("workspace.json");
        var priorState = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
        var priorTime = priorState is null ? (DateTime?)null : File.GetLastWriteTimeUtc(statePath);
        var root = Path.Combine(AppPaths.StateDirectory, "owner-close-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var secondPath = Path.Combine(root, "other.txt");
        await File.WriteAllTextAsync(secondPath, "other untouched file\r\n");
        var fixtures = new List<(MainWindow Owner, MainViewModel Model, QuickPreviewWindow Preview, TextEditor Editor, string Path)>();
        var closed = new HashSet<MainWindow>();
        var held = new HashSet<SemaphoreSlim>();
        async Task<(MainWindow Owner, MainViewModel Model, QuickPreviewWindow Preview, TextEditor Editor, string Path)> Fixture(string name)
        {
            var path = Path.Combine(root, name + ".txt");
            await File.WriteAllTextAsync(path, name + " baseline\r\n");
            var owner = new MainWindow(null, Path.Combine(root, name + "-window")) { SuppressQuickPreviewPresentationForChecks = true };
            owner.Closed += (_, _) => closed.Add(owner);
            var model = (MainViewModel)owner.DataContext;
            await model.InitializeAsync(root);
            await owner.StartNestedForChecksAsync();
            await model.SaveNowAsync();
            _ = new WindowInteropHelper(owner).EnsureHandle();
            owner.OpenQuickPreview(path);
            var preview = (QuickPreviewWindow)OwnerCloseField(typeof(MainWindow), "_quickPreview").GetValue(owner)!;
            await preview.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            var editor = QuickDescendants((DependencyObject)preview.Content).OfType<TextEditor>().Single();
            if (editor.IsReadOnly) throw new InvalidOperationException("The owned note fixture did not become editable.");
            var fixture = (owner, model, preview, editor, path);
            fixtures.Add(fixture);
            return fixture;
        }
        async Task<SemaphoreSlim> HoldWorkspace(MainViewModel model)
        {
            var gate = (SemaphoreSlim)OwnerCloseField(typeof(MainViewModel), "_stateSaving").GetValue(model)!;
            await gate.WaitAsync();
            held.Add(gate);
            return gate;
        }
        void Release(SemaphoreSlim gate) { if (held.Remove(gate)) gate.Release(); }
        SessionEndingCancelEventArgs SessionEnd(MainWindow owner, SessionEndingCancelEventArgs? shared = null)
        {
            var args = shared ?? (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [ReasonSessionEnding.Logoff], null)!;
            typeof(MainWindow).GetMethod("OnSessionEnding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [null, args]);
            return args;
        }
        try
        {
            await new WorkspaceStore().SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
            var normal = await Fixture("normal");
            normal.Editor.Text += "note before owner close\r\n";
            var expectedBefore = normal.Editor.Text;
            var normalGate = await HoldWorkspace(normal.Model);
            normal.Owner.Close();
            Check("owner close freezes the editable owned note while the actual workspace save waits",
                await HoverWait(() => normal.Preview.IsOwnerCloseFrozen && !normal.Preview.HasUnsavedChanges
                    && File.ReadAllText(normal.Path) == expectedBefore, 5000)
                && normal.Editor.IsReadOnly && !normal.Preview.IsEnabled && !closed.Contains(normal.Owner));
            normal.Editor.CaretOffset = normal.Editor.Text.Length;
            normal.Editor.TextArea.PerformTextInput("typing while closing");
            Check("real mini-editor text input cannot change the note during the held owner save", normal.Editor.Text == expectedBefore);
            normal.Preview.OpenFile(secondPath);
            var recycled = false;
            await normal.Preview.DeleteCurrentAsync(_ => { recycled = true; return Task.CompletedTask; });
            Check("retarget and delete are rejected throughout the owner close transaction", normal.Preview.FilePath == normal.Path && !recycled && File.Exists(normal.Path));
            var concurrentEnd = SessionEnd(normal.Owner);
            Check("shutdown cannot bypass a regular owner close whose note transaction is still pending", concurrentEnd.Cancel && normal.Preview.IsOwnerCloseFrozen);
            // A controlled document mutation is stronger than user input:
            // the final recommit must preserve it even though input is frozen.
            normal.Editor.Text += "late internal buffer change\r\n";
            var expectedLate = normal.Editor.Text;
            Check("a late buffer change remains unsaved until the workspace gate is released", normal.Preview.HasUnsavedChanges && File.ReadAllText(normal.Path) == expectedBefore);
            Release(normalGate);
            Check("actual owner close recommits the final buffer before closing the owner and its preview",
                await HoverWait(() => closed.Contains(normal.Owner), 5000) && File.ReadAllText(normal.Path) == expectedLate
                && !app.Windows.OfType<QuickPreviewWindow>().Contains(normal.Preview));

            var conflict = await Fixture("conflict");
            var conflictGate = await HoldWorkspace(conflict.Model);
            conflict.Owner.Close();
            await HoverWait(() => conflict.Preview.IsOwnerCloseFrozen, 5000);
            await File.WriteAllTextAsync(conflict.Path, "external saved change\r\n");
            conflict.Editor.Text = "late buffer which must be retained\r\n";
            Release(conflictGate);
            Check("a final-note conflict cancels owner close and restores editing without losing either version",
                await HoverWait(() => !conflict.Preview.IsOwnerCloseFrozen, 5000)
                && !closed.Contains(conflict.Owner) && conflict.Preview.IsEnabled && !conflict.Editor.IsReadOnly
                && conflict.Preview.HasUnsavedChanges && conflict.Editor.Text == "late buffer which must be retained\r\n"
                && File.ReadAllText(conflict.Path) == "external saved change\r\n"
                && !(bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(conflict.Owner)!);

            var ending = await Fixture("session");
            var endingGate = await HoldWorkspace(ending.Model);
            var endingInputBlocked = false;
            var endingFrozen = false;
            var finalSessionText = ending.Editor.Text + "late session buffer\r\n";
            _ = ending.Owner.Dispatcher.BeginInvoke(() =>
            {
                endingFrozen = ending.Preview.IsOwnerCloseFrozen && !ending.Preview.IsEnabled;
                var before = ending.Editor.Text;
                ending.Editor.TextArea.PerformTextInput("must not type during session close");
                endingInputBlocked = ending.Editor.Text == before;
                ending.Editor.Text = finalSessionText;
                Release(endingGate);
            });
            var ended = SessionEnd(ending.Owner);
            Check("the actual SessionEnding frame keeps the preview frozen while the real workspace save waits", endingFrozen && endingInputBlocked);
            Check("SessionEnding recommits the final buffer and permits shutdown only after it is on disk", !ended.Cancel
                && ending.Preview.IsOwnerCloseFrozen && File.ReadAllText(ending.Path) == finalSessionText);
            ended.Cancel = true; // A later main window can refuse the same shutdown event.
            Check("a later window's shutdown refusal restores this already-saved preview too",
                await HoverWait(() => !ending.Preview.IsOwnerCloseFrozen, 5000) && ending.Preview.IsEnabled && !ending.Editor.IsReadOnly
                && !(bool)OwnerCloseField(typeof(MainWindow), "_allowClose").GetValue(ending.Owner)!);

            var failedEnd = await Fixture("session-conflict");
            failedEnd.Editor.Text = "unsaved session note\r\n";
            await File.WriteAllTextAsync(failedEnd.Path, "external session note\r\n");
            var failedArgs = SessionEnd(failedEnd.Owner);
            Check("an initial session-note conflict refuses shutdown and unfreezes the intact buffer", failedArgs.Cancel
                && !failedEnd.Preview.IsOwnerCloseFrozen && failedEnd.Preview.IsEnabled && !failedEnd.Editor.IsReadOnly
                && failedEnd.Editor.Text == "unsaved session note\r\n" && File.ReadAllText(failedEnd.Path) == "external session note\r\n");

            var timedOut = await Fixture("session-timeout");
            var timeoutGate = await HoldWorkspace(timedOut.Model);
            var timeoutArgs = SessionEnd(timedOut.Owner);
            Check("the existing bounded session-save timeout refuses shutdown and restores note input", timeoutArgs.Cancel
                && !timedOut.Preview.IsOwnerCloseFrozen && timedOut.Preview.IsEnabled && !timedOut.Editor.IsReadOnly
                && !(bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(timedOut.Owner)!);
            Release(timeoutGate);
            await timedOut.Model.SaveNowAsync();

            var earlierOwner = await Fixture("shared-session-first");
            var laterOwner = await Fixture("shared-session-second");
            var sharedArgs = SessionEnd(earlierOwner.Owner);
            var laterGate = await HoldWorkspace(laterOwner.Model);
            var observedInsideLaterFrame = false;
            _ = laterOwner.Owner.Dispatcher.BeginInvoke(() =>
            {
                // The first handler's normal-priority callbacks have already
                // run in this second handler's nested workspace-save frame.
                observedInsideLaterFrame = earlierOwner.Preview.IsOwnerCloseFrozen && laterOwner.Preview.IsOwnerCloseFrozen;
                laterOwner.Editor.Text = "late note from second owner\r\n";
                File.WriteAllText(laterOwner.Path, "external second-owner change\r\n");
                Release(laterGate);
            });
            SessionEnd(laterOwner.Owner, sharedArgs);
            Check("a second owner's late conflict broadcasts shutdown cancellation after its nested save frame",
                observedInsideLaterFrame && sharedArgs.Cancel && !earlierOwner.Preview.IsOwnerCloseFrozen
                && !laterOwner.Preview.IsOwnerCloseFrozen && earlierOwner.Preview.IsEnabled && laterOwner.Preview.IsEnabled
                && !(bool)OwnerCloseField(typeof(MainWindow), "_allowClose").GetValue(earlierOwner.Owner)!
                && !(bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(earlierOwner.Owner)!
                && laterOwner.Editor.Text == "late note from second owner\r\n"
                && File.ReadAllText(laterOwner.Path) == "external second-owner change\r\n");
            var earlierBefore = earlierOwner.Editor.Text;
            earlierOwner.Editor.CaretOffset = earlierBefore.Length;
            earlierOwner.Editor.TextArea.PerformTextInput("input restored");
            Check("the first owner's real mini-editor accepts input again after the later owner refuses shutdown",
                !earlierOwner.Editor.IsReadOnly && earlierOwner.Editor.Text == earlierBefore + "input restored");

            var externalObserver = await Fixture("late-external-cancel");
            var externalArgs = SessionEnd(externalObserver.Owner);
            await externalObserver.Owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            externalArgs.Cancel = true;
            Check("a later external handler cancellation is still observed after early dispatcher callbacks have run",
                await HoverWait(() => !externalObserver.Preview.IsOwnerCloseFrozen, 5000)
                && externalObserver.Preview.IsEnabled && !externalObserver.Editor.IsReadOnly
                && !(bool)OwnerCloseField(typeof(MainWindow), "_allowClose").GetValue(externalObserver.Owner)!
                && !(bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(externalObserver.Owner)!);

            var deleteCancelledOwner = await Fixture("delete-owner-cancel");
            var recycleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recycleRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recycling = deleteCancelledOwner.Preview.DeleteCurrentAsync(async _ =>
            {
                recycleEntered.TrySetResult();
                await recycleRelease.Task;
                throw new IOException("Owned recycle boundary failed after owner-close cancellation.");
            });
            await recycleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deleteGate = await HoldWorkspace(deleteCancelledOwner.Model);
            deleteCancelledOwner.Owner.Close();
            await HoverWait(() => deleteCancelledOwner.Preview.IsOwnerCloseFrozen, 5000);
            deleteCancelledOwner.Editor.Text = "preserved late note during held delete\r\n";
            await File.WriteAllTextAsync(deleteCancelledOwner.Path, "external held-delete change\r\n");
            Release(deleteGate);
            await HoverWait(() => !deleteCancelledOwner.Preview.IsOwnerCloseFrozen, 5000);
            recycleRelease.TrySetResult();
            await recycling.WaitAsync(TimeSpan.FromSeconds(5));
            Check("a late recycle failure restores the same current note's edit permission after owner close was cancelled",
                !closed.Contains(deleteCancelledOwner.Owner) && !deleteCancelledOwner.Preview.IsOwnerCloseFrozen
                && deleteCancelledOwner.Preview.IsEnabled && !deleteCancelledOwner.Editor.IsReadOnly
                && deleteCancelledOwner.Preview.HasUnsavedChanges
                && deleteCancelledOwner.Editor.Text == "preserved late note during held delete\r\n"
                && File.ReadAllText(deleteCancelledOwner.Path) == "external held-delete change\r\n");

            Check("owner-close fixtures keep all windows unshown and leave unrelated file bytes untouched", fixtures.All(item => !item.Owner.IsVisible && !item.Preview.IsVisible)
                && File.ReadAllText(secondPath) == "other untouched file\r\n" && app.GetType() == typeof(Application));
        }
        finally
        {
            foreach (var gate in held.ToArray()) Release(gate);
            foreach (var fixture in fixtures.Where(item => !closed.Contains(item.Owner)))
            {
                fixture.Preview.EndOwnerClose();
                var session = (PreviewTextEditSession?)OwnerCloseField(typeof(QuickPreviewWindow), "_editor").GetValue(fixture.Preview);
                if (session is not null) fixture.Editor.Text = session.Text;
                fixture.Owner.Close();
            }
            await HoverWait(() => fixtures.All(item => closed.Contains(item.Owner)), 5000);
            foreach (var fixture in fixtures)
            {
                fixture.Preview.CompleteOwnerClose();
                fixture.Model.Dispose();
            }
            await SettingsSettle();
            app.MainWindow = priorMain;
            GpuBootstrap.UseSavedPreference(renderer);
            if (priorState is not null) { await File.WriteAllBytesAsync(statePath, priorState); File.SetLastWriteTimeUtc(statePath, priorTime!.Value); }
            else if (File.Exists(statePath)) File.Delete(statePath);
            TryDelete(root);
        }
    }

    private static FieldInfo OwnerCloseField(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
