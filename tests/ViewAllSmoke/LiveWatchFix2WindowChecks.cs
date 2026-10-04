using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The window's drives as the second review found them (cluster
/// k-live-watch): a drive plugged in beside one slow to answer reached the
/// canvas only once every drive had answered, twice over (J039), and a mapped
/// drive whose server did not answer when the drives were listed was dropped
/// with everything read in it, or left out, and never asked about again
/// (J040).  One drive is held from answering, or made to answer not ready and
/// to be taken for a mapped one, with no network involved.
///
/// <para>The windows need the app, of which a process can only ever have the
/// one, on the thread that made it: these checks always run in a process of
/// their own, so that the Settings checks after them still make theirs.  No
/// window is shown.</para>
/// </summary>
internal static partial class Program
{
    private static async Task LiveWatchFix2WindowChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(LiveWatchFix2WindowChecks), StringComparison.OrdinalIgnoreCase))
        {
            await Fix2WindowsInOwnProcessAsync();
            return;
        }

        RunOnSta("live watch, round 2: drives", Fix2WindowsOnStaAsync);
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task Fix2WindowsInOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only " + nameof(LiveWatchFix2WindowChecks))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        using var child = Process.Start(start)!;
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var finished = false;
        try
        {
            while (await child.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal))
                {
                    Check(line[8..], true);
                }
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal))
                {
                    Check(line[8..], false);
                }
                else if (line.EndsWith(" checks passed", StringComparison.Ordinal))
                {
                    finished = true;
                }
                else if (line.Length > 0)
                {
                    Console.WriteLine(line);
                }
            }

            await child.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Gone by itself meanwhile.
            }

            Check("the round-2 drive checks' own process finished within ten minutes", false);
            return;
        }

        Check("the round-2 drive checks' own process ran to the end", finished);
    }

    private static async Task Fix2WindowsOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the round-2 drive checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        // The windows read the drives as the app does; what that throws on
        // the interface thread outside these checks is written down and
        // survived, rather than ending them half way.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var drives = await new ViewAllFileSystemService().GetDriveRootsAsync();
        if (drives.Count < 2)
        {
            Check("(fewer than two drives are ready here: the drive checks need two)", true);
            return;
        }

        using (ActivationGuard.GuardWindowsCreated())
        {
            await Fix2DriveAnswerChecksAsync(drives);
            await Fix2MappedDriveChecksAsync(drives);
        }
    }

    private static Task Fix2RescanDrives(MainWindow main) =>
        (Task)typeof(MainWindow).GetMethod("RescanDrivesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;

    private static bool Fix2Shown(MainWindow main, string path) =>
        main.FirstPane.Tree.Root.AllChildren.Any(folder => ViewAllPath.Equals(folder.FullPath, path));

    private static NestedRoot[] Fix2Roots(IEnumerable<ViewAllEntryDescriptor> drives) =>
        [.. drives.Select(drive => new NestedRoot(drive.FullPath, drive.DisplayName, NestedFolderKind.Drive, drive.SecondaryText))];

    /// <summary>Stops a window's own retry of mapped drives, which outlives the checks on a window never closed.</summary>
    private static void Fix2StopRetry(MainWindow main) =>
        (typeof(MainWindow).GetField("_remoteDriveRetryTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main) as DispatcherTimer)?.Stop();

    /// <summary>
    /// A stick plugged in beside a drive that takes its time to answer - one
    /// mapped to a server that is off takes twenty seconds: the stick joins
    /// the canvas as soon as it answers itself (J039).
    /// </summary>
    private static async Task Fix2DriveAnswerChecksAsync(IReadOnlyList<ViewAllEntryDescriptor> drives)
    {
        Section("live watch, round 2: a drive plugged in beside one slow to answer (J039)");
        var held = drives[^1];
        var fresh = drives.First(drive => !ReferenceEquals(drive, held));
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks(Fix2Roots(drives.Where(drive => !ReferenceEquals(drive, fresh))));
            Check($"the canvas starts without {fresh.FullPath}", !Fix2Shown(main, fresh.FullPath));

            Task rescan;
            long at;
            using (var hold = new HeldDrive(held.FullPath))
            {
                rescan = Fix2RescanDrives(main);
                at = await LiveWait(() => Fix2Shown(main, fresh.FullPath), 5_000);
                Check($"the drive that came joins the canvas while {held.FullPath} is still to answer ({at} ms)", at >= 0 && !rescan.IsCompleted);
                hold.Release();
                await Task.WhenAny(rescan, Task.Delay(30_000));
            }

            Check($"and every drive is there once the slow one answers ({string.Join(", ", main.FirstPane.Tree.Root.AllChildren.Select(folder => folder.FullPath))})",
                rescan.IsCompleted && drives.All(drive => Fix2Shown(main, drive.FullPath)));
        }
        finally
        {
            Fix2StopRetry(main);
            shell.Dispose();
        }
    }

    /// <summary>
    /// A mapped drive whose server does not answer - a NAS rebooting, the
    /// network not up at logon.  A volume coming or going meanwhile keeps its
    /// cell and what was read in it; one left out of the listing is asked
    /// about again, and joins once it answers, with no volume message (J040).
    /// </summary>
    private static async Task Fix2MappedDriveChecksAsync(IReadOnlyList<ViewAllEntryDescriptor> drives)
    {
        Section("live watch, round 2: a mapped drive whose server does not answer (J040)");
        var mapped = drives[^1];
        var letter = char.ToUpperInvariant(mapped.FullPath[0]);
        var resolveBefore = VolumeKinds.ResolveLetter;
        var describeBefore = ViewAllFileSystemService.DescribeDrive;
        var existsBefore = FileSystemService.FolderExists;
        var interval = typeof(MainWindow).GetProperty("RemoteDriveRetryInterval", BindingFlags.Static | BindingFlags.NonPublic);
        var intervalBefore = interval?.GetValue(null);
        var answering = 0;
        bool IsMapped(string path) => ViewAllPath.Equals(ViewAllPath.Normalize(path), mapped.FullPath);
        interval?.SetValue(null, TimeSpan.FromMilliseconds(300));
        VolumeKinds.ResolveLetter = candidate => char.ToUpperInvariant(candidate) == letter ? @"\\fix2-nas\share" : null;
        ViewAllFileSystemService.DescribeDrive = drive => IsMapped(drive.RootDirectory.FullName) && Volatile.Read(ref answering) == 0 ? null : describeBefore(drive);
        FileSystemService.FolderExists = path => (!IsMapped(path) || Volatile.Read(ref answering) != 0) && existsBefore(path);
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks(Fix2Roots(drives));
            var cell = main.FirstPane.Tree.Root.AllChildren.FirstOrDefault(folder => ViewAllPath.Equals(folder.FullPath, mapped.FullPath));
            if (cell is null)
            {
                Check($"{mapped.FullPath} is on the canvas", false);
                return;
            }

            await main.FirstPane.Tree.LoadAsync(cell);
            await Fix2RescanDrives(main);
            Check($"a volume coming or going while {mapped.FullPath} does not answer leaves it its cell, with what was read in it",
                Fix2Shown(main, mapped.FullPath) && !NestedTree.IsDetached(cell) && cell.IsLoaded);

            main.UseNestedDrivesForChecks(Fix2Roots(drives.Where(drive => !ReferenceEquals(drive, mapped))));
            var early = await LiveWait(() => Fix2Shown(main, mapped.FullPath), 1_500);
            Check("left out, it does not join while it does not answer", early < 0);
            Volatile.Write(ref answering, 1);
            var at = await LiveWait(() => Fix2Shown(main, mapped.FullPath), 5_000);
            Check($"and joins once it answers, with no volume message ({at} ms)", at >= 0);
        }
        finally
        {
            Fix2StopRetry(main);
            shell.Dispose();
            interval?.SetValue(null, intervalBefore);
            VolumeKinds.ResolveLetter = resolveBefore;
            ViewAllFileSystemService.DescribeDrive = describeBefore;
            FileSystemService.FolderExists = existsBefore;
        }
    }
}
