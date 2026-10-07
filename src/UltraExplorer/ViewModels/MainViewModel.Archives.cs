using System.Diagnostics;
using System.Windows;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;

namespace UltraExplorer.ViewModels;

/// <summary>
/// Archives as folders, from the window's side: a file inside one opened,
/// items copied, dragged or sent out of one, a whole archive unpacked - and
/// everything that would write into one refused with a word, rather than
/// failing somewhere in the Shell.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// True - and says why - when any of the paths is inside an archive, for
    /// what cannot be done there: rename, delete, cut, make something new.
    /// </summary>
    internal bool RefuseInsideArchive(IEnumerable<string> paths, string what)
    {
        if (paths.FirstOrDefault(ArchiveService.IsInsideArchive) is not { } inside)
        {
            return false;
        }

        Toast.ShowError($"{what} inside an archive is not possible — it is read-only here. Copy or drag the items out instead.");
        return true;
    }

    /// <summary>True - and says why - when the folder is inside an archive or is one: nothing can be put there.</summary>
    internal bool RefuseArchiveTarget(string folder)
    {
        if (!ArchiveService.IsInsideArchive(folder) && !ArchiveService.IsArchiveFile(folder))
        {
            return false;
        }

        Toast.ShowError("Archives are read-only here: nothing can be put inside one.");
        return true;
    }

    /// <summary>A file inside an archive, taken out to the temporary folder and opened in its program.</summary>
    public async Task OpenFromArchiveAsync(string path)
    {
        try
        {
            var file = await ArchiveService.ExtractForOpenAsync(path, ArchiveProgressFor($"Opening {Path.GetFileName(path)}"), CancellationToken.None);
            Toast.Hide();
            NativeShellService.Open(file);
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            Toast.ShowError(ArchiveMessage(ex));
        }
    }

    /// <summary>
    /// Items inside archives copied into a folder on disk - a drop, a paste,
    /// a send to the other pane.  Never a move: the archive keeps them.
    /// </summary>
    internal async Task<bool> ExtractIntoAsync(IReadOnlyList<string> paths, string targetDirectory)
    {
        if (RefuseArchiveTarget(targetDirectory))
        {
            return false;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var made = await ArchiveService.ExtractToAsync(paths, targetDirectory, ArchiveProgressFor("Extracting"), CancellationToken.None);
            await Tree.RefreshPathAsync(targetDirectory);
            var name = Path.GetFileName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar));
            _ = Toast.ShowSuccessAsync($"Extracted {made.Count} item(s) to {(name.Length == 0 ? targetDirectory : name)} in {Seconds(watch)}");
            return true;
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            Toast.ShowError(ArchiveMessage(ex));
            return false;
        }
    }

    /// <summary>
    /// Archives unpacked next to themselves: into a folder named after the
    /// archive, or - unless <paramref name="ownFolder"/> - straight beside it
    /// when what is inside is a single folder or file already.
    /// </summary>
    public async Task ExtractArchivesAsync(IReadOnlyList<string> archives, bool ownFolder, string? destination = null)
    {
        var made = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (var archive in archives)
        {
            try
            {
                made.Add(await ArchiveService.ExtractArchiveAsync(
                    archive, destination, ownFolder, ArchiveProgressFor($"Extracting {Path.GetFileName(archive)}"), CancellationToken.None));
            }
            catch (Exception ex) when (IsArchiveFailure(ex))
            {
                Toast.ShowError(ArchiveMessage(ex));
                return;
            }
        }

        foreach (var parent in made.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(parent))
            {
                await Tree.RefreshPathAsync(parent);
            }
        }

        if (made.Count == 1)
        {
            await Tree.RevealPathAsync(made[0], focus: false);
        }

        await Toast.ShowSuccessAsync(made.Count == 1
            ? $"Extracted to {Path.GetFileName(made[0])} in {Seconds(watch)}"
            : $"Extracted {made.Count} archives in {Seconds(watch)}");
    }

    /// <summary>Items inside archives, or whole archives, extracted into a folder the user picks.</summary>
    public async Task ExtractToChosenFolderAsync(IReadOnlyList<string> paths)
    {
        var first = paths.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to" };
        var beside = ArchiveBeside(first);
        if (beside is not null)
        {
            dialog.InitialDirectory = beside;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (paths.All(ArchiveService.IsArchiveFile))
        {
            await ExtractArchivesAsync(paths, ownFolder: true, dialog.FolderName);
            return;
        }

        await ExtractIntoAsync(paths, dialog.FolderName);
    }

    /// <summary>Items inside an archive extracted into the folder the archive is in.</summary>
    public Task<bool> ExtractBesideArchiveAsync(IReadOnlyList<string> paths) =>
        ArchiveBeside(paths.FirstOrDefault() ?? string.Empty) is { } folder ? ExtractIntoAsync(paths, folder) : Task.FromResult(false);

    /// <summary>The folder on disk the outermost archive of a path is in.</summary>
    internal static string? ArchiveBeside(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && !ArchiveService.IsArchiveFile(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>Items inside archives taken out to the temporary folder and put on the clipboard, to paste anywhere.</summary>
    internal async Task CopyFromArchiveAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var made = await ArchiveService.ExtractToTempAsync(paths, ArchiveProgressFor("Copying out of the archive"), CancellationToken.None);
            if (!NativeShellService.CopyPathsToClipboard(made, cut: false))
            {
                Toast.ShowError("Another application is holding the clipboard — try again.");
                return;
            }

            await Toast.ShowSuccessAsync($"Copied {made.Count} item(s) from the archive");
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            Toast.ShowError(ArchiveMessage(ex));
        }
    }

    /// <summary>
    /// What a drag out of an archive carries: the items taken out first, to
    /// the temporary folder.  Null when that failed (and was said).
    /// </summary>
    internal string[]? ExtractForDrag(IReadOnlyList<string> paths)
    {
        try
        {
            // The drag has to start while the button is still down: waited
            // for here, pumping the window's messages so the toast still moves.
            var task = ArchiveService.ExtractToTempAsync(paths, ArchiveProgressFor("Preparing the drag"), CancellationToken.None);
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            var made = task.GetAwaiter().GetResult();
            Toast.Hide();
            return [.. made];
        }
        catch (Exception ex) when (IsArchiveFailure(ex))
        {
            Toast.ShowError(ArchiveMessage(ex));
            return null;
        }
    }

    /// <summary>Progress in the toast: how far, and how fast - a tenth of a second apart at most.</summary>
    private IProgress<ArchiveProgress> ArchiveProgressFor(string verb)
    {
        var started = Stopwatch.StartNew();
        var shown = -1L;
        Toast.ShowBusy($"{verb}…");
        return new Progress<ArchiveProgress>(value =>
        {
            var now = started.ElapsedMilliseconds;
            if (now - shown < 100 || value.Total <= 0)
            {
                return;
            }

            shown = now;
            var percent = Math.Min(100, value.Done * 100 / value.Total);
            var speed = now > 0 ? value.Done / 1048576.0 / (now / 1000.0) : 0;
            Toast.ShowBusy($"{verb}… {percent}% · {speed:N0} MB/s");
        });
    }

    private static string Seconds(Stopwatch watch) =>
        watch.Elapsed.TotalSeconds < 1 ? $"{watch.ElapsedMilliseconds} ms" : $"{watch.Elapsed.TotalSeconds:N1} s";

    private static bool IsArchiveFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or System.Runtime.InteropServices.COMException or ArgumentException;

    private static string ArchiveMessage(Exception ex) => ex switch
    {
        ArchivePasswordException password => $"{Path.GetFileName(password.ArchivePath)}: wrong password, or none was given.",
        OperationCanceledException => "Cancelled.",
        _ => ex.Message
    };

    /// <summary>Asks for an archive's password on the window's thread, from whichever thread 7-Zip asked on.</summary>
    internal string? AskArchivePassword(string archiveName)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return null;
        }

        return dispatcher.CheckAccess()
            ? PromptRequested?.Invoke("Password", $"Password for {archiveName}", string.Empty, false)
            : dispatcher.Invoke(() => PromptRequested?.Invoke("Password", $"Password for {archiveName}", string.Empty, false));
    }
}
