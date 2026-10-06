using System.Diagnostics;
using System.IO;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// A drive mapped to a server that is off takes some twenty seconds to say it
/// is not ready, and the navigation pane showed no drive at all - not even
/// C: - until it had, in every window and dialog.  Here the last drive listed
/// takes four: every other drive must be in the pane long before it, in the
/// order listed, and the last one join them when it answers (review 2,
/// J020).  Every file this touches is in the isolated state folder, put back
/// as it was at the end.
/// </summary>
internal static partial class Program
{
    private static Task DrivePaneChecks()
    {
        RunWithIsolatedState("navigation pane drives", DrivePaneChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task DrivePaneChecksAsync(string fixture)
    {
        Section("navigation pane: each drive comes in as it answers, not when the slowest has (J020)");
        await ResetIsolatedStateAsync();
        var folder = Path.Combine(fixture, "drives");
        Directory.CreateDirectory(folder);
        using var icons = new ShellIconService();
        var expected = new FileSystemService(icons).GetDrives().Select(item => item.Path).ToArray();
        if (expected.Length < 2)
        {
            Console.WriteLine("  note  fewer than two drives are ready here; the drives one by one are not checked");
            return;
        }

        var held = expected[^1];
        using var released = new ManualResetEventSlim();
        var exists = FileSystemService.FolderExists;
        FileSystemService.FolderExists = path =>
        {
            if (string.Equals(path, held, StringComparison.OrdinalIgnoreCase))
            {
                released.Wait(TimeSpan.FromSeconds(15));
            }

            return exists(path);
        };
        var releasing = Task.Delay(4_000).ContinueWith(_ => released.Set(), TaskScheduler.Default);
        try
        {
            var watch = Stopwatch.StartNew();
            using var window = new MainViewModel();
            await window.InitializeAsync(folder);
            var others = expected[..^1];
            string[] Shown() => [.. window.Drives.Select(item => item.Path)];
            await LiveWait(() => window.Drives.Count > 0 || released.IsSet, 10_000);
            var firstAt = watch.ElapsedMilliseconds;
            var firstShown = Shown();
            await LiveWait(() => Shown().SequenceEqual(others, StringComparer.OrdinalIgnoreCase) || released.IsSet, 10_000);
            var othersAt = watch.ElapsedMilliseconds;
            var othersShown = Shown();
            var stillHeld = !released.IsSet;
            Console.WriteLine($"        first drive in the pane at {firstAt:N0} ms, every drive but {held} at {othersAt:N0} ms; {held} answers at 4,000 ms");
            Check($"the first drive is in the pane before the slow one answers ({(firstShown.Length > 0 ? firstShown[0] : "none")} at {firstAt:N0} ms)",
                firstShown.Length > 0 && string.Equals(firstShown[0], expected[0], StringComparison.OrdinalIgnoreCase)
                && !firstShown.Contains(held, StringComparer.OrdinalIgnoreCase) && firstAt < 4_000);
            Check($"and every drive but the slow one, in the order listed ({othersShown.Length} of {others.Length} at {othersAt:N0} ms)",
                stillHeld && othersShown.SequenceEqual(others, StringComparer.OrdinalIgnoreCase));

            // A volume arriving or leaving meanwhile: the drives are asked
            // again, and the pane keeps what it shows until they answer.
            var refreshing = window.RefreshDrivesAsync();
            Check("asked again while one has not answered, the pane keeps every drive it shows",
                Shown().SequenceEqual(othersShown, StringComparer.OrdinalIgnoreCase));

            await LiveWait(() => Shown().SequenceEqual(expected, StringComparer.OrdinalIgnoreCase), 15_000);
            Check("once the slow drive answers, the pane has every drive in the order listed",
                Shown().SequenceEqual(expected, StringComparer.OrdinalIgnoreCase));
            await refreshing.WaitAsync(TimeSpan.FromSeconds(20));
            using var lineIcons = new ShellIconService();
            var lines = new FileSystemService(lineIcons).GetDrives().Select(PaneLine).ToArray();
            Check("with the same names, glyphs and icons as a listing of them all", window.Drives.Select(PaneLine).SequenceEqual(lines));
        }
        finally
        {
            released.Set();
            await releasing;
            FileSystemService.FolderExists = exists;
        }
    }

    /// <summary>One navigation pane entry as it is drawn, for comparing two panes.</summary>
    private static string PaneLine(FavoriteItemViewModel item) =>
        $"{item.Name}|{item.Path}|{item.Glyph}|{item.AccentHex}|{item.Kind}|{item.OpensInShell}|{item.IsCustom}|{item.Icon is null}";

    /// <summary>
    /// Runs <paramref name="body"/> on an STA thread of its own, in a fixture
    /// folder it is handed, with the state folder's shared files - the
    /// workspace, the marks and the canvas layout every window reads - put
    /// back as they were afterwards.  Only in test-window mode with the state
    /// kept apart from the user's: a window model writes those files.
    /// </summary>
    private static void RunWithIsolatedState(string name, Func<string, Task> body) => RunOnSta(name, async () =>
    {
        Section($"{name}: isolated state");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is { Length: > 0 } state
            && ViewAllPath.Equals(state, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check($"the {name} checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.StateDirectory);
        string[] owned = [AppPaths.State("workspace.json"), AppPaths.State("folder-marks.json"), AppPaths.State("view-all.workspace.json")];
        var originals = owned.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var rendererBefore = GpuBootstrap.Preference;
        // Short: a move by the Shell's own operation into a path past
        // MAX_PATH stops at a dialog asking what to do, with nobody to answer.
        var fixture = Path.Combine(Path.GetTempPath(), "uxi", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixture);
        try
        {
            await body(fixture);
        }
        finally
        {
            // Every model applied the fixture's processor choice to the
            // process; the checks after these must find it as it was.
            GpuBootstrap.UseSavedPreference(rendererBefore);
            foreach (var (path, bytes) in originals)
            {
                if (bytes is not null)
                {
                    File.WriteAllBytes(path, bytes);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            TryDelete(fixture);
        }
    });

    /// <summary>
    /// The shared state as a check starts it: a workspace with the processor
    /// as its renderer - so no model starts the graphics card - and nothing
    /// else unless given, no marks and no canvas layout.
    /// </summary>
    private static async Task ResetIsolatedStateAsync(WorkspaceState? workspace = null)
    {
        foreach (var name in new[] { "folder-marks.json", "view-all.workspace.json" })
        {
            if (File.Exists(AppPaths.State(name)))
            {
                File.Delete(AppPaths.State(name));
            }
        }

        workspace ??= new WorkspaceState();
        workspace.CanvasRenderer ??= "Cpu";
        await new WorkspaceStore().SaveAsync(workspace);
    }
}
