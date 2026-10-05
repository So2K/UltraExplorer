using System.IO;
using UltraExplorer;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The prepared picker of the resident dialog worker, as it waits: it must
/// cost nothing while no dialog needs it. Nothing on disk is watched for it
/// (no volume armed, no folder registered), and the tree canvas behind the
/// tiles runs no timer. Bound to a dialog, the canvas watches the dialog's
/// folder from the start, and the tree once the picker is on screen. Never
/// shown here: a window made on the settings checks' thread, which has the
/// application.
/// </summary>
internal static partial class Program
{
    private static async Task PreparedPickerWindowChecksAsync()
    {
        Section("prepared dialog picker: what it costs while it waits");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerPreparedPicker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sample.txt"), "prepared");
        MainWindow? picker = null;
        try
        {
            var placeholder = new FileDialogRequest
            {
                IsNativeProxy = true, InitialFolder = PreparedPicker.PlaceholderFolder,
                Options = FileDialogOptions.NoTestFileCreate | FileDialogOptions.DontAddToRecent
            };
            picker = new MainWindow(new FileDialogSession(placeholder));
            picker.PrepareAsCloakedPicker(_ => { });
            var shell = (MainViewModel)picker.DataContext;
            Check("a prepared picker watches nothing on disk while it waits: neither the tree nor the tiles",
                shell.Tree.Changes is null && picker.Panes.All(pane => pane.Tree.Changes is null));
            Check("the tree canvas's auto-pan timer runs only while something is dragged in it", picker.Editor.DisableAutoPanning);
            Check("the placeholder is the application's own folder, which never changes", Directory.Exists(PreparedPicker.PlaceholderFolder)
                && !PreparedPicker.PlaceholderFolder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase));

            var request = new FileDialogRequest
            {
                IsNativeProxy = true, InitialFolder = folder, Mode = FileDialogMode.Save, FileName = "export.txt",
                Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
            };
            request.Filters.Add(new FileDialogFilterSpec("Text documents (*.txt)", "*.txt"));
            await picker.RebindAsync(new FileDialogSession(request));
            Check("bound to a dialog, the tiles watch its folder from the start",
                picker.Panes.All(pane => ReferenceEquals(pane.Tree.Changes, shell.Changes)));
            Check("the tree behind the tiles waits until the picker is on screen", shell.Tree.Changes is null);
            picker.WatchChanges();
            Check("on screen, the tree is watched too", ReferenceEquals(shell.Tree.Changes, shell.Changes));
            picker.WatchChanges();
            Check("watching again changes nothing", ReferenceEquals(shell.Tree.Changes, shell.Changes)
                && picker.Panes.All(pane => ReferenceEquals(pane.Tree.Changes, shell.Changes)));
        }
        finally
        {
            picker?.CloseFromCaller();
            TryDelete(folder);
        }
        await PickerUxWindowChecksAsync();
        await NormalLazyOverviewWindowChecksAsync();
    }
}
