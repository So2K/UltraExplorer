using System.IO;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// J034/J035: file-command disk and Shell waits never hold the dispatcher.
/// The fixtures replace only the final clipboard and command boundaries; the
/// command snapshot, validation, CF_HDROP/Preferred DropEffect construction,
/// prompts, continuations and messages are the production paths.
/// </summary>
internal static partial class Program
{
    private static Task FileCommandReview2Checks()
    {
        RunOnSta("review 2 file commands", FileCommandReview2OnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task FileCommandReview2OnStaAsync()
    {
        Section("file commands: disk and Shell waits leave the UI responsive (J034/J035)");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFileCommands", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "folder");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "first.txt");
        var second = Path.Combine(folder, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");

        var existsBefore = MainViewModel.ItemExists;
        var createFolderBefore = MainViewModel.CreateFolderOnDisk;
        var createNoteBefore = MainViewModel.CreateNoteFileOnDisk;
        var openBefore = MainViewModel.OpenKnownItem;
        var clipboardBefore = NativeShellService.ClipboardPublisherForTests;
        try
        {
            using var model = new MainViewModel(Path.Combine(root, "commands.workspace.json"));
            await model.InitializeAsync(folder);
            await model.Tree.RevealPathAsync(folder);
            await Until(() => ViewAllPath.Equals(model.Tree.TargetDirectory ?? string.Empty, folder), 5_000);
            var uiThread = Environment.CurrentManagedThreadId;
            var dispatcher = Dispatcher.CurrentDispatcher;

            await FileCommandClipboardAsync(model, folder, first, second, uiThread, dispatcher);
            await FileCommandCreateAsync(model, folder, uiThread, dispatcher,
                createFolderBefore, createNoteBefore);
            await FileCommandOpenAsync(model, folder, first, second, uiThread, dispatcher);
        }
        finally
        {
            MainViewModel.ItemExists = existsBefore;
            MainViewModel.CreateFolderOnDisk = createFolderBefore;
            MainViewModel.CreateNoteFileOnDisk = createNoteBefore;
            MainViewModel.OpenKnownItem = openBefore;
            NativeShellService.ClipboardPublisherForTests = clipboardBefore;
            TryDelete(root);
        }
    }

    private static async Task FileCommandClipboardAsync(
        MainViewModel model,
        string folder,
        string first,
        string second,
        int uiThread,
        Dispatcher dispatcher)
    {
        using var asked = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var existenceThreads = new List<int>();
        var publishedOn = 0;
        string[] publishedPaths = [];
        var publishedEffect = -1;
        var publishes = 0;
        MainViewModel.ItemExists = path =>
        {
            lock (existenceThreads)
            {
                existenceThreads.Add(Environment.CurrentManagedThreadId);
            }

            asked.Set();
            released.Wait(TimeSpan.FromSeconds(15));
            return path == first || path == second;
        };
        NativeShellService.ClipboardPublisherForTests = data =>
        {
            publishes++;
            publishedOn = Environment.CurrentManagedThreadId;
            publishedPaths = data.GetData(DataFormats.FileDrop) as string[] ?? [];
            if (data.GetData("Preferred DropEffect") is MemoryStream stream)
            {
                var at = stream.Position;
                stream.Position = 0;
                publishedEffect = stream.ReadByte();
                stream.Position = at;
            }

            return true;
        };
        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added =
            [
                new SelectionItem(first, false, 1),
                new SelectionItem(second, false, 1)
            ],
            Source = SelectionSource.Canvas
        });

        var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
        model.CutCommand.Execute(null);
        var uiAnswered = await answered;
        var completed = await Until(() => model.CutCommand.CanExecute(null), 10_000);
        Check("Copy/Cut validates every selected path off the UI thread exactly once",
            completed && existenceThreads.Count == 2 && existenceThreads.All(thread => thread != uiThread));
        Check("the shared dispatcher answers while a share validation is held", uiAnswered);
        Check("the actual clipboard publication returns to the STA UI thread",
            publishes == 1 && publishedOn == uiThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);
        Check("CF_HDROP and Preferred DropEffect keep the exact Cut selection",
            publishedPaths.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase) && publishedEffect == 2);

        var publishedUnsafe = false;
        NativeShellService.ClipboardPublisherForTests = _ => publishedUnsafe = true;
        IOException? unsafePath = null;
        try
        {
            NativeShellService.CopyPathsToClipboard([Path.Combine(folder, "notes ")], cut: false);
        }
        catch (IOException ex)
        {
            unsafePath = ex;
        }

        Check("a trailing-dot/space path is refused by cheap name validation before clipboard publication",
            unsafePath?.Message.Contains("ends in a dot or a space", StringComparison.Ordinal) == true && !publishedUnsafe);

        MainViewModel.ItemExists = _ => throw new IOException("owned validation failure");
        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [new SelectionItem(first, false, 1)],
            Source = SelectionSource.Canvas
        });
        model.CopyCommand.Execute(null);
        var failedCleanly = await Until(() => model.CopyCommand.CanExecute(null), 5_000);
        Check("an existence provider failure becomes a command error instead of escaping async void",
            failedCleanly && model.Toast.Message == "owned validation failure" && !publishedUnsafe);
    }

    private static async Task FileCommandCreateAsync(
        MainViewModel model,
        string folder,
        int uiThread,
        Dispatcher dispatcher,
        Func<string, string, string> createFolder,
        Func<string, string, string> createNote)
    {
        model.PromptRequested += (title, _, _, _) => title == "New folder" ? "made-off-thread" : "made-off-thread.txt";

        using (var asked = new ManualResetEventSlim())
        using (var released = new ManualResetEventSlim())
        {
            var worker = 0;
            MainViewModel.CreateFolderOnDisk = (parent, name) =>
            {
                worker = Environment.CurrentManagedThreadId;
                asked.Set();
                released.Wait(TimeSpan.FromSeconds(15));
                return createFolder(parent, name);
            };
            var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
            model.NewFolderCommand.Execute(null);
            var uiAnswered = await answered;
            var completed = await Until(() => model.NewFolderCommand.CanExecute(null), 10_000);
            Check("New folder performs its filesystem call off the UI thread",
                completed && worker != 0 && worker != uiThread && Directory.Exists(Path.Combine(folder, "made-off-thread")));
            Check("the UI answers while New folder waits for its target disk", uiAnswered);
        }

        using (var asked = new ManualResetEventSlim())
        using (var released = new ManualResetEventSlim())
        {
            var createThread = 0;
            var openThread = 0;
            bool? openedAsFolder = null;
            string? opened = null;
            MainViewModel.CreateNoteFileOnDisk = (parent, name) =>
            {
                createThread = Environment.CurrentManagedThreadId;
                asked.Set();
                released.Wait(TimeSpan.FromSeconds(15));
                return createNote(parent, name);
            };
            MainViewModel.OpenKnownItem = (path, isDirectory) =>
            {
                opened = path;
                openedAsFolder = isDirectory;
                openThread = Environment.CurrentManagedThreadId;
            };
            var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
            model.NewTextFileCommand.Execute(null);
            var uiAnswered = await answered;
            var completed = await Until(() => model.NewTextFileCommand.CanExecute(null), 10_000);
            var expected = Path.Combine(folder, "made-off-thread.txt");
            Check("New text file creates and default-opens in order off the UI thread",
                completed && createThread != 0 && createThread != uiThread
                && openThread == createThread && openedAsFolder == false
                && ViewAllPath.Equals(opened ?? string.Empty, expected) && File.Exists(expected));
            Check("the UI answers while New text file waits for its target disk", uiAnswered);
        }

        var closingFolder = Path.Combine(folder, "closing");
        Directory.CreateDirectory(closingFolder);
        using (var closing = new MainViewModel(Path.Combine(folder, "closing.workspace.json")))
        using (var asked = new ManualResetEventSlim())
        using (var released = new ManualResetEventSlim())
        {
            await closing.InitializeAsync(closingFolder);
            await closing.Tree.RevealPathAsync(closingFolder);
            await Until(() => ViewAllPath.Equals(closing.Tree.TargetDirectory ?? string.Empty, closingFolder), 5_000);
            closing.PromptRequested += (_, _, _, _) => "made-while-closing.txt";
            var openedAfterClose = 0;
            MainViewModel.CreateNoteFileOnDisk = (parent, name) =>
            {
                asked.Set();
                released.Wait(TimeSpan.FromSeconds(15));
                return createNote(parent, name);
            };
            MainViewModel.OpenKnownItem = (_, _) => Interlocked.Increment(ref openedAfterClose);
            closing.NewTextFileCommand.Execute(null);
            var entered = await Task.Run(() => asked.Wait(TimeSpan.FromSeconds(10)));
            closing.Dispose();
            released.Set();
            var completed = await Until(() => closing.NewTextFileCommand.CanExecute(null), 10_000);
            Check("closing during a slow create lets the requested write finish but does not launch an app afterwards",
                entered && completed && File.Exists(Path.Combine(closingFolder, "made-while-closing.txt"))
                && Volatile.Read(ref openedAfterClose) == 0);
        }
    }

    private static async Task FileCommandOpenAsync(
        MainViewModel model,
        string folder,
        string first,
        string second,
        int uiThread,
        Dispatcher dispatcher)
    {
        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added =
            [
                new SelectionItem(first, false, 1),
                new SelectionItem(second, false, 1)
            ],
            Source = SelectionSource.Canvas
        });
        using var asked = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var opened = new List<(string Path, bool? IsDirectory, int Thread)>();
        MainViewModel.OpenKnownItem = (path, isDirectory) =>
        {
            lock (opened)
            {
                opened.Add((path, isDirectory, Environment.CurrentManagedThreadId));
            }

            asked.Set();
            released.Wait(TimeSpan.FromSeconds(15));
        };

        var answered = TopReviewAnswersWhileHeld(dispatcher, asked, released);
        model.OpenCommand.Execute(null);
        var uiAnswered = await answered;
        var completed = await Until(() => model.OpenCommand.CanExecute(null), 10_000);
        Check("Open launches every cached file kind off the UI thread without probing it as a folder",
            completed && opened.Select(item => item.Path).SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase)
            && opened.All(item => item.Thread != uiThread && item.IsDirectory == false));
        Check("the UI answers while a default program launch is held", uiAnswered);
    }
}
