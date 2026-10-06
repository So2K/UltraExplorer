using System.IO;
using System.Reflection;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task LongPathShellOperationChecks()
    {
        Section("modern long-path Shell operations");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1")
        {
            Console.WriteLine("  note: long-path Shell checks require isolated state and ULTRAEXPLORER_TEST_WINDOW=1; skipped");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerLongShell", Guid.NewGuid().ToString("N"));
        try
        {
            var sourceFolder = DeepFolder(Path.Combine(root, "source"), "source.txt");
            var copyFolder = DeepFolder(Path.Combine(root, "copy"), "source.txt");
            var moveFolder = DeepFolder(Path.Combine(root, "move"), "source.txt");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(copyFolder);
            Directory.CreateDirectory(moveFolder);
            var source = Path.Combine(sourceFolder, "source.txt");
            File.WriteAllText(source, "long path payload");
            Check("the owned fixture actually exceeds the legacy MAX_PATH boundary",
                source.Length > 260 && Path.Combine(copyFolder, Path.GetFileName(source)).Length > 260);

            Task? observedCopy = null;
            var prior = NativeShellService.CopyStarted.Value;
            NativeShellService.CopyStarted.Value = operation =>
            {
                observedCopy = operation;
                return operation;
            };
            try
            {
                await new NativeShellService().CopyOrMoveAsync([source], copyFolder, move: false);
            }
            finally
            {
                NativeShellService.CopyStarted.Value = prior;
            }

            var copied = Path.Combine(copyFolder, Path.GetFileName(source));
            Check("IFileOperation copies an owned file whose source and destination exceed 260 characters",
                File.Exists(copied) && File.ReadAllText(copied) == "long path payload");
            Check("CopyStarted still observes the Shell operation itself through completion",
                observedCopy is { IsCompletedSuccessfully: true });

            var shell = new NativeShellService();
            var duplicatedFile = await shell.DuplicateAsync(source);
            var expectedFileDuplicate = Path.Combine(sourceFolder, "source - Copy.txt");
            Check("Duplicate uses modern CopyItem with the exact long-path file name chosen by the app",
                duplicatedFile == expectedFileDuplicate && duplicatedFile.Length > 260
                && File.Exists(duplicatedFile) && File.ReadAllText(duplicatedFile) == "long path payload");

            string? duplicateFolderError = null;
            string? duplicatedFolder = null;
            try { duplicatedFolder = await shell.DuplicateAsync(sourceFolder); }
            catch (IOException error) { duplicateFolderError = error.Message; }
            var expectedFolderDuplicate = Path.Combine(
                Path.GetDirectoryName(sourceFolder)!,
                Path.GetFileName(sourceFolder) + " - Copy");
            Check("Duplicate recursively copies an owned long-path folder without VisualBasic's legacy Shell path"
                  + (duplicateFolderError is null ? string.Empty : $" ({duplicateFolderError})"),
                duplicatedFolder is not null && duplicatedFolder == expectedFolderDuplicate && duplicatedFolder.Length > 260
                && Directory.Exists(duplicatedFolder)
                && File.Exists(Path.Combine(duplicatedFolder, "source.txt")));

            var duplicates = new[] { duplicatedFile, duplicatedFolder }.Where(path => path is not null).Cast<string>().ToArray();
            await shell.DeleteAsync(duplicates, permanently: true);
            Check("owned long-path duplicates can be removed as one modern operation",
                !File.Exists(duplicatedFile) && (duplicatedFolder is null || !Directory.Exists(duplicatedFolder)));

            await new NativeShellService().CopyOrMoveAsync([copied], moveFolder, move: true);
            var moved = Path.Combine(moveFolder, Path.GetFileName(source));
            Check("the same modern backend moves a long-path item",
                !File.Exists(copied) && File.Exists(moved) && File.ReadAllText(moved) == "long path payload");

            await new NativeShellService().DeleteAsync([moved], permanently: true);
            Check("permanent delete removes the owned long-path file without the legacy API", !File.Exists(moved));
            await new NativeShellService().DeleteAsync([sourceFolder], permanently: true);
            Check("permanent delete also removes an owned long-path folder tree", !Directory.Exists(sourceFolder));

            var parsing = ShellFileOperation.ParsingNames(source);
            Check("a long local path is offered to the Shell in extended form before its safe modern fallback",
                parsing.Count == 2 && parsing[0].StartsWith(@"\\?\", StringComparison.Ordinal)
                && parsing[1] == Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));

            var shortSource = Path.Combine(@"C:\", new string('s', 120), new string('n', 120) + ".txt");
            var shortDestination = Path.Combine(@"C:\", new string('d', 140));
            var resultingDestination = Path.Combine(shortDestination, Path.GetFileName(shortSource));
            Check("a default-name copy gets long-operation guards when only its resulting destination crosses 260 characters",
                shortSource.Length < 260 && shortDestination.Length < 260 && resultingDestination.Length >= 260
                && ShellFileOperation.IsLongOperation([shortSource], shortDestination, targetNames: null));

            var copyFlags = ShellFileOperation.TransferFlags(renameOnCollision: false);
            var collisionFlags = ShellFileOperation.TransferFlags(renameOnCollision: true);
            var recycleFlags = ShellFileOperation.DeleteFlags(permanently: false);
            var permanentFlags = ShellFileOperation.DeleteFlags(permanently: true);
            Check("copy and collision operations retain session undo and Explorer collision naming",
                copyFlags.HasFlag(ShellFileOperationFlags.AllowUndo)
                && copyFlags.HasFlag(ShellFileOperationFlags.AddUndoRecord)
                && collisionFlags.HasFlag(ShellFileOperationFlags.RenameOnCollision)
                && collisionFlags.HasFlag(ShellFileOperationFlags.PreserveFileExtensions));
            Check("Recycle and permanent delete keep distinct confirmation and undo contracts",
                recycleFlags.HasFlag(ShellFileOperationFlags.RecycleOnDelete)
                && recycleFlags.HasFlag(ShellFileOperationFlags.AllowUndo)
                && recycleFlags.HasFlag(ShellFileOperationFlags.WantNukeWarning)
                && !permanentFlags.HasFlag(ShellFileOperationFlags.RecycleOnDelete)
                && permanentFlags.HasFlag(ShellFileOperationFlags.NoConfirmation));

            var aborted = false;
            try { ShellFileOperation.ThrowForCompletion(0, 0, aborted: true); }
            catch (OperationCanceledException) { aborted = true; }
            var failed = false;
            try { ShellFileOperation.ThrowForCompletion(unchecked((int)0x80070005), 0, aborted: false); }
            catch (IOException) { failed = true; }
            Check("modern aborted and failed operations keep the existing cancellation/error contract", aborted && failed);

            Check("the compiled NativeShellService exposes no legacy SHFileOperation entry point",
                typeof(NativeShellService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .All(method => !method.Name.Contains("SHFileOperation", StringComparison.Ordinal)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string DeepFolder(string root, string leaf)
    {
        var folder = root;
        for (var index = 0; Path.Combine(folder, leaf).Length <= 275; index++)
        {
            folder = Path.Combine(folder, $"segment-{index:D2}-abcdefghijklmnop");
        }

        return folder;
    }
}
