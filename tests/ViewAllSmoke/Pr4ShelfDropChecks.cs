using System.IO;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task Pr4ShelfDropChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(Pr4ShelfDropChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(Pr4ShelfDropChecks));
            return Task.CompletedTask;
        }
        RunOnSta("PR4 shelf OLE drop", Pr4ShelfDropOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task Pr4ShelfDropOnStaAsync()
    {
        Section("PR4 shelf: sequential TEMP copies complete before the drop releases its source");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check("shelf drop fixture owns an isolated profile", isolated);
        if (!isolated) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var fixture = Path.Combine(AppPaths.StateDirectory, "shelf-drop-" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(Path.GetTempPath(), "UltraExplorerShelfDrop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(temporary);
        var workspace = Path.Combine(fixture, "workspace.json");
        await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
        var inputs = new[] { Path.Combine(temporary, "first.txt"), Path.Combine(temporary, "second.txt") };
        File.WriteAllText(inputs[0], "first temporary source");
        File.WriteAllText(inputs[1], "second temporary source");
        var existsBefore = NativeShellService.OperationItemExists;
        var copiesStarted = 0;
        var main = new MainWindow(null, workspace);
        (Application.Current ?? throw new InvalidOperationException("The owned shelf Application was not initialized.")).MainWindow = main;
        var shell = (MainViewModel)main.DataContext;
        var store = new ShelfStore(Path.Combine(fixture, "Shelf"));
        try
        {
            new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
            await shell.InitializeAsync(fixture);
            main.Shelf.Initialize(store, shell);
            shell.ShowDropShelf = true;
            await main.Shelf.Ready;
            LayOutWindow(main, 1400, 900);
            main.Shelf.Toggle();
            await SettingsSettle();
            NativeShellService.OperationItemExists = path =>
            {
                if (inputs.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Thread.Sleep(120);
                    Interlocked.Increment(ref copiesStarted);
                }
                return existsBefore(path);
            };
            var pumped = false;
            _ = main.Dispatcher.InvokeAsync(() => pumped = true, DispatcherPriority.Normal);
            var data = new DataObject(DataFormats.FileDrop, inputs);
            var dropped = ArchiveDragArgs(DragDrop.DropEvent, data, main.Shelf.Board, new Point(20, 20));
            main.Shelf.Panel.RaiseEvent(dropped);
            // This is what an archive manager can do as soon as OLE Drop
            // returns. A second deferred copy must already own its bytes.
            foreach (var input in inputs) File.Delete(input);
            Check("the shelf reports a single allowed Copy effect", dropped.Handled && dropped.Effects == DragDropEffects.Copy);
            Check("both Shell operations finish before the source cleans up", copiesStarted == 2 && store.Count == 2);
            Check("both durable copies contain the complete source bytes after immediate source deletion",
                store.Entries.All(entry => entry.IsCopy && File.Exists(entry.Path))
                && store.Entries.Select(entry => File.ReadAllText(entry.Path)).Order().SequenceEqual(new[] { "first temporary source", "second temporary source" }));
            Check("the UI dispatcher remains responsive while the drop holds its OLE source", pumped);
            Check("reopening preserves both copied entries after their original TEMP paths vanish", new ShelfStore(store.Root).Count == 2);
            await Task.WhenAll(main.Shelf.Board.Cards.Select(card => card.PreviewReady));
            main.Shelf.Board.UpdateLayout();
            ShootElement(main.Shelf.Panel, "pr4-shelf.png");
            var broken = new ArchiveDelayedData(fixture, [0], freshPaths: true) { ThrowOnDropRead = true };
            broken.BeginDrop();
            var failedOver = ArchiveDragArgs(DragDrop.DragOverEvent, broken, main.Shelf.Board, new Point(20, 20));
            main.Shelf.Panel.RaiseEvent(failedOver);
            var failedDrop = ArchiveDragArgs(DragDrop.DropEvent, broken, main.Shelf.Board, new Point(20, 20));
            main.Shelf.Panel.RaiseEvent(failedDrop);
            Check("a failed COM data provider is refused without escaping the routed event",
                failedOver.Handled && failedOver.Effects == DragDropEffects.None
                && failedDrop.Handled && failedDrop.Effects == DragDropEffects.None && store.Count == 2
                && shell.Toast.Message.Contains("owned delayed data read failed", StringComparison.Ordinal));
            shell.ShowDropShelf = false;
            var rejected = ArchiveDragArgs(DragDrop.DropEvent, new DataObject(DataFormats.FileDrop, new[] { inputs[0] }), main.Shelf.Board, new Point(20, 20));
            main.Shelf.Panel.RaiseEvent(rejected);
            Check("a disabled shelf refuses routed drops and preserves accepted entries", rejected.Effects == DragDropEffects.None && store.Count == 2);
            Check("the owned fixture window was never shown", !main.IsVisible);
        }
        finally
        {
            NativeShellService.OperationItemExists = existsBefore;
            main.Shelf.Dispose();
            shell.Dispose();
            main.CloseFromCaller();
            await SettingsSettle();
            store.Clear();
            TryDelete(temporary);
            TryDelete(fixture);
        }
    }
}
