using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task FolderListStabilityChecks()
    {
        RunOnSta("folder list navigation supersession", FolderListSupersessionAsync);
        return Task.CompletedTask;
    }

    private static async Task FolderListSupersessionAsync()
    {
        Section("folder list: late failures do not overwrite newer navigation");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListStability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldPath = Path.Combine(root, "old");
            var newPath = Path.Combine(root, "new");
            Directory.CreateDirectory(oldPath);
            Directory.CreateDirectory(newPath);
            var file = Path.Combine(newPath, "new.txt");
            File.WriteAllText(file, "new");
            var snapshot = new ViewAllDirectorySnapshot([
                new ViewAllEntryDescriptor(file, "new.txt", ViewAllEntryKind.File, false, false, 3, DateTime.UtcNow)
            ], false, 1);

            foreach (var lateFailure in new Exception?[] { new IOException("delayed old failure"), new UnauthorizedAccessException("delayed access failure"), null })
            {
                using var icons = new ShellIconService();
                var oldRead = new TaskCompletionSource<ViewAllDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var activated = new List<string>();
                var list = new FolderListViewModel(
                    (path, token) => ViewAllPath.Equals(path, oldPath) ? oldRead.Task : Task.FromResult(snapshot),
                    (path, open) => { activated.Add(path); return Task.CompletedTask; }, _ => false, icons) { IsVisible = true };
                var oldNavigation = list.NavigateAsync(oldPath);
                await list.NavigateAsync(newPath);
                Check("new folder is displayed before the old provider completes", list.Items.Count == 1 && list.Items[0].FullPath == file);
                if (lateFailure is null) oldRead.SetResult(new ViewAllDirectorySnapshot([], false, 0));
                else oldRead.SetException(lateFailure);
                await oldNavigation;
                Check($"old {lateFailure?.GetType().Name ?? "success"} leaves the new folder rows and status intact",
                    list.FolderPath == newPath && list.Items.Count == 1 && list.Items[0].FullPath == file
                    && list.CountText == "1" && list.EmptyText.Length == 0 && !list.IsLoading);
                Check("overtaken navigation never activates the old canvas folder", activated.SequenceEqual([newPath]));
                list.IsVisible = false;
                await list.ReloadAsync();
            }
        }
        finally { TryDelete(root); }
    }
}
