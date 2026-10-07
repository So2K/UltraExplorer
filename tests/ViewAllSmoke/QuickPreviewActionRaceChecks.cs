using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task QuickPreviewActionRaceChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(QuickPreviewActionRaceChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(QuickPreviewActionRaceChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Quick preview action races require isolated state and test-window mode.");
        RunOnSta("Quick preview action races", QuickPreviewActionRaceOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task QuickPreviewActionRaceOnStaAsync()
    {
        Section("Quick Look: close/save and delete/file-switch races");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var app = Application.Current ?? throw new InvalidOperationException("The owned Application was not initialized.");
        var priorMain = app.MainWindow;
        var priorShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.Combine(AppPaths.StateDirectory, "quick-action-races-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.txt");
        var secondPath = Path.Combine(directory, "second.txt");
        const string firstText = "first owned note\r\n";
        const string secondText = "second owned note\r\n";
        await File.WriteAllTextAsync(firstPath, firstText);
        await File.WriteAllTextAsync(secondPath, secondText);
        var windows = new List<QuickPreviewWindow>();
        var closed = new HashSet<QuickPreviewWindow>();
        var gates = new List<TaskCompletionSource<bool>>();
        async Task<(QuickPreviewWindow Window, TextEditor Editor)> OpenFirst()
        {
            var window = new QuickPreviewWindow();
            windows.Add(window);
            window.Closed += (_, _) => closed.Add(window);
            window.OpenFile(firstPath);
            await window.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            var editor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single();
            if (editor.IsReadOnly) throw new InvalidOperationException("The ordinary fixture did not become editable.");
            return (window, editor);
        }
        TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(gate);
            return gate;
        }
        try
        {
            var (dirtyWindow, dirtyEditor) = await OpenFirst();
            dirtyEditor.Text += "notes entered after the save snapshot\r\n";
            var dirtySave = Gate();
            QuickRaceField("_pendingSave").SetValue(dirtyWindow, dirtySave.Task);
            dirtyWindow.Close();
            dirtySave.SetResult(true);
            await QuickRaceUntil(() => !(bool)QuickRaceField("_askingClose").GetValue(dirtyWindow)!);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check("a completed old save cannot close a still-dirty note buffer",
                !closed.Contains(dirtyWindow) && dirtyWindow.HasUnsavedChanges
                && dirtyEditor.Text.Contains("notes entered after the save snapshot", StringComparison.Ordinal));
            dirtyEditor.Text = firstText;
            dirtyWindow.Close();

            var (switchWindow, switchEditor) = await OpenFirst();
            switchEditor.Text += "owned pending save\r\n";
            var switchSave = Gate();
            QuickRaceField("_pendingSave").SetValue(switchWindow, switchSave.Task);
            switchWindow.Close();
            switchWindow.OpenFile(secondPath);
            // The held save boundary is completed with a clean old buffer;
            // no source write is needed to test request/close ownership.
            switchEditor.Text = firstText;
            switchSave.SetResult(true);
            await switchWindow.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            await QuickRaceUntil(() => !(bool)QuickRaceField("_askingClose").GetValue(switchWindow)!);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var switchedEditor = QuickDescendants((DependencyObject)switchWindow.Content).OfType<TextEditor>().Single();
            Check("an old close request cannot close the file opened while its save was pending",
                !closed.Contains(switchWindow) && switchWindow.FilePath == secondPath
                && !switchedEditor.IsReadOnly && switchedEditor.Text == secondText);
            switchWindow.Close();

            var (deleteWindow, deleteEditor) = await OpenFirst();
            var failedRecycle = Gate();
            var recycling = 0;
            string? recycledPath = null;
            Task RecycleFailure(string path)
            {
                recycling++;
                recycledPath = path;
                return failedRecycle.Task;
            }
            var deleting = deleteWindow.DeleteCurrentAsync(RecycleFailure);
            await deleteWindow.DeleteCurrentAsync(RecycleFailure);
            Check("pending recycling makes the old note read-only and suppresses a duplicate Delete",
                deleteEditor.IsReadOnly && recycledPath == firstPath && recycling == 1 && !deleting.IsCompleted);
            failedRecycle.SetException(new IOException("owned recycle cancellation fixture"));
            await deleting.WaitAsync(TimeSpan.FromSeconds(10));
            var recoveredEditor = QuickDescendants((DependencyObject)deleteWindow.Content).OfType<TextEditor>().Single();
            Check("a failed recycle restores the ordinary note editor without losing its contents",
                !closed.Contains(deleteWindow) && deleteWindow.FilePath == firstPath
                && !recoveredEditor.IsReadOnly && recoveredEditor.Text == firstText
                && File.ReadAllText(firstPath) == firstText);
            deleteWindow.Close();

            var (retargetWindow, _) = await OpenFirst();
            var oldRecycle = Gate();
            string? oldTarget = null;
            var oldDelete = retargetWindow.DeleteCurrentAsync(path => { oldTarget = path; return oldRecycle.Task; });
            retargetWindow.OpenFile(secondPath);
            await retargetWindow.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            oldRecycle.SetResult(true);
            await oldDelete.WaitAsync(TimeSpan.FromSeconds(10));
            var targetEditor = QuickDescendants((DependencyObject)retargetWindow.Content).OfType<TextEditor>().Single();
            Check("completion of old-file recycling leaves the new file open and editable",
                oldTarget == firstPath && !closed.Contains(retargetWindow) && retargetWindow.FilePath == secondPath
                && !targetEditor.IsReadOnly && targetEditor.Text == secondText);
            retargetWindow.Close();
            Check("action race fixtures never recycle or overwrite either owned physical source",
                File.ReadAllText(firstPath) == firstText && File.ReadAllText(secondPath) == secondText);
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult(false);
            foreach (var window in windows.Where(window => !closed.Contains(window)))
            {
                var editor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().SingleOrDefault();
                if (editor is not null) editor.Text = window.FilePath == secondPath ? secondText : firstText;
                QuickRaceField("_pendingSave").SetValue(window, Task.FromResult(false));
                QuickRaceField("_allowClose").SetValue(window, true);
                window.Close();
            }
            app.MainWindow = priorMain;
            app.ShutdownMode = priorShutdown;
            TryDelete(directory);
        }
        Check("action race fixtures release all owned preview windows", windows.All(closed.Contains));
    }

    private static FieldInfo QuickRaceField(string name) => typeof(QuickPreviewWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(QuickPreviewWindow).FullName, name);

    private static async Task QuickRaceUntil(Func<bool> ready)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                throw new TimeoutException("The owned Quick Look action did not reach its boundary.");
            await Task.Delay(10);
        }
    }
}
