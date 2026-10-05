using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Downloads or Documents moved to a NAS that is asleep, or a WSL slow to
/// start, held every window up until they answered - and an Explorer window
/// taken over, which the broker gives fifteen seconds, was closed and the
/// folder it was opened for lost.  The window must be ready without them, and
/// they join the pane - Home first, the known folders before the pins - as
/// soon as they answer; a pin made meanwhile is kept, after the file's
/// (review 2, J022).  Every file this touches is in the isolated state
/// folder, put back as it was at the end.
/// </summary>
internal static partial class Program
{
    private static Task SlowPlacesChecks()
    {
        RunWithIsolatedState("slow known folders", SlowPlacesChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task SlowPlacesChecksAsync(string fixture)
    {
        Section("folder window: known folders and WSL slow to answer do not hold the window up (J022)");
        var folder = Path.Combine(fixture, "places");
        var pinned = Path.Combine(folder, "pinned");
        var early = Path.Combine(folder, "pinned meanwhile");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(early);
        await ResetIsolatedStateAsync(new WorkspaceState { Favorites = [new FavoriteState("pinned", pinned, "", "#E3B341")] });

        using var icons = new ShellIconService();
        var expected = new FileSystemService(icons);
        var expectedPlaces = expected.GetQuickAccess().Select(PaneLine).ToArray();
        var expectedNetwork = expected.GetNetworkLocations().Select(PaneLine).ToArray();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        using var released = new ManualResetEventSlim();
        var heldAsked = 0;
        var exists = FileSystemService.FolderExists;
        FileSystemService.FolderExists = path =>
        {
            if (string.Equals(path, documents, StringComparison.OrdinalIgnoreCase) || string.Equals(path, @"\\wsl$", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref heldAsked);
                released.Wait(TimeSpan.FromSeconds(15));
            }

            return exists(path);
        };
        var releasing = Task.Delay(3_000).ContinueWith(_ => released.Set(), TaskScheduler.Default);
        try
        {
            // As the folder router makes one for an adopted Explorer window.
            var windowWorkspace = Path.Combine(fixture, "shell-windows", Guid.NewGuid().ToString("N") + ".workspace.json");
            var watch = Stopwatch.StartNew();
            using var window = new MainViewModel(windowWorkspace + ".tree.json", false, windowWorkspace);
            await window.InitializeAsync(folder);
            var readyAt = watch.ElapsedMilliseconds;
            var readyFirst = !released.IsSet;
            Console.WriteLine($"        window ready at {readyAt:N0} ms; the known folder and WSL answer at 3,000 ms");
            Check($"the window is ready before a known folder and WSL slow to answer have ({readyAt:N0} ms)",
                readyFirst && readyAt < 2_500 && Volatile.Read(ref heldAsked) > 0);
            Check("with its pin in the pane already", window.QuickAccess.Any(item => item.IsCustom && ViewAllPath.Equals(item.Path, pinned)));

            window.PinPath(early);
            await LiveWait(() => window.HomeItems.Count > 0 && window.NetworkLocations.Count > 0, 15_000);
            var known = window.HomeItems.Concat(window.QuickAccess.Where(item => !item.IsCustom)).Select(PaneLine).ToArray();
            Check("once they answer, Home and the known folders are in the pane as at any start", known.SequenceEqual(expectedPlaces));
            var customs = window.QuickAccess.Select((item, index) => (item, index)).Where(entry => entry.item.IsCustom).ToArray();
            Check("before the pins: the file's first, then the one made meanwhile",
                customs.Select(entry => entry.item.Path).SequenceEqual([pinned, early], StringComparer.OrdinalIgnoreCase)
                && customs.All(entry => entry.index >= window.QuickAccess.Count - customs.Length));
            Check("and the network places as at any start", window.NetworkLocations.Select(PaneLine).SequenceEqual(expectedNetwork));

            await window.SaveNowAsync();
            var saved = await new WorkspaceStore().LoadAsync();
            Check("its next save keeps the file's pin and writes the one made meanwhile",
                saved?.Favorites.Select(item => item.Path).SequenceEqual([pinned, early], StringComparer.OrdinalIgnoreCase) == true);
        }
        finally
        {
            released.Set();
            await releasing;
            FileSystemService.FolderExists = exists;
        }
    }
}
