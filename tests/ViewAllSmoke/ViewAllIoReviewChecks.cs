using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The tree's reading and saving, from the second full review: a folder whose
/// name ends in a dot or a space (J051), a folder with more entries than one
/// read takes (J050), and the workspace save after the camera comes to rest
/// (J017).
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllIoReviewChecks()
    {
        await FolderNamedWithADotIsReadAsItselfAsync();
        await FolderPastTheCapKeepsItsFoldersAsync();
        await WorkspaceSaveStaysOffTheUiThreadAsync();
    }

    // ---- J051: a folder whose name ends in a dot or a space ----------------

    /// <summary>
    /// "backup." beside "backup", and "lonely." alone, as WSL, git or a share
    /// can leave them.  Read by the name the graph keeps, the dotted folder
    /// was read as its neighbour - backup's files listed under backup.'s
    /// title, so Delete and Rename on them acted on backup's - and the lone
    /// one could not be read at all.
    /// </summary>
    private static async Task FolderNamedWithADotIsReadAsItselfAsync()
    {
        Section("view-all io: a folder whose name ends in a dot or a space is read as itself (J051)");
        const string ExtendedLength = @"\\?\";
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerDotRead", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup");
        var dotted = backup + ".";
        var lonely = Path.Combine(root, "lonely.");
        var spaced = Path.Combine(dotted, "inner ");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "in-backup.txt"), "plain");

        // Made through the extended-length form, which keeps the names' ends.
        Directory.CreateDirectory(ExtendedLength + spaced);
        Directory.CreateDirectory(ExtendedLength + lonely);
        File.WriteAllText(ExtendedLength + Path.Combine(dotted, "in-dotted.txt"), "dotted");
        File.WriteAllText(ExtendedLength + Path.Combine(lonely, "alone.txt"), "alone");
        File.WriteAllText(ExtendedLength + Path.Combine(spaced, "deep.txt"), "deep");
        try
        {
            var files = new ViewAllFileSystemService();
            var options = new ViewAllGraphOptions();
            async Task<string> ReadAsync(string folder)
            {
                try
                {
                    var snapshot = await files.GetChildrenAsync(folder, options);
                    return string.Join(" | ", snapshot.Entries.Select(entry => $"{entry.FullPath} ({entry.DisplayName})"));
                }
                catch (DirectoryNotFoundException ex)
                {
                    return ex.Message;
                }
            }

            var dottedRead = await ReadAsync(dotted);
            Check($"'backup.' lists its own entries, not backup's ({dottedRead})",
                dottedRead == $"{spaced} (inner ) | {Path.Combine(dotted, "in-dotted.txt")} (in-dotted.txt)");
            var lonelyRead = await ReadAsync(lonely);
            Check($"'lonely.', with no neighbour, can be read ({lonelyRead})", lonelyRead == $"{Path.Combine(lonely, "alone.txt")} (alone.txt)");
            var spacedRead = await ReadAsync(spaced);
            Check($"so can a folder ending in a space inside it ({spacedRead})", spacedRead == $"{Path.Combine(spaced, "deep.txt")} (deep.txt)");
            var plainRead = await ReadAsync(backup);
            var rootRead = await ReadAsync(root);
            Check($"and every other folder reads as ever ({plainRead}; {rootRead})",
                plainRead == $"{Path.Combine(backup, "in-backup.txt")} (in-backup.txt)"
                && rootRead == $"{backup} (backup) | {dotted} (backup.) | {lonely} (lonely.)");

            // The tree: opening backup. shows backup.'s own, and backup keeps its own.
            using var graph = new ViewAllGraphService();
            var top = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(top);
            if (graph.TryGetNode(dotted, out var dottedNode) && graph.TryGetNode(backup, out var backupNode))
            {
                await graph.ExpandAsync(dottedNode);
                await graph.ExpandAsync(backupNode);
                var underDotted = string.Join(" | ", dottedNode.Children.Select(child => child.FullPath));
                var underPlain = string.Join(" | ", backupNode.Children.Select(child => child.FullPath));
                Check($"on the tree, 'backup.' holds its own and 'backup' its own ({underDotted}; {underPlain})",
                    underDotted == $"{spaced} | {Path.Combine(dotted, "in-dotted.txt")}"
                    && underPlain == Path.Combine(backup, "in-backup.txt"));
            }
            else
            {
                Check("on the tree, 'backup.' and 'backup' are both there", false);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(ExtendedLength + root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder.
            }
        }
    }

    // ---- J050: a folder with more entries than one read takes ---------------

    /// <summary>
    /// A camera folder: forty pictures, and the folders Archive, Screens and
    /// Videos, read with a cap of thirty-two.  The file system hands out
    /// Archive, then the pictures, then Screens and Videos; cut where the
    /// thirty-second entry fell, the list, a dialog and the tree showed
    /// Archive alone, in the order the folders lead in, and nothing offered
    /// to load the other two.
    /// </summary>
    private static async Task FolderPastTheCapKeepsItsFoldersAsync()
    {
        Section("view-all io: a folder past the cap still shows every sub-folder (J050)");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerCapFolders", Guid.NewGuid().ToString("N"));
        string[] folders = ["Archive", "Screens", "Videos"];
        try
        {
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
            }

            for (var index = 1; index <= 40; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"IMG_{index:D4}.jpg"), []);
            }

            var files = new ViewAllFileSystemService();
            var options = new ViewAllGraphOptions(MaximumChildrenPerFolder: 32);
            string Describe(IEnumerable<ViewAllEntryDescriptor> entries, bool isTruncated)
            {
                var list = entries.ToList();
                var shownFolders = list.Where(entry => entry.Kind == ViewAllEntryKind.Folder).Select(entry => entry.DisplayName);
                return $"{list.Count} entries, folders [{string.Join(", ", shownFolders)}]{(isTruncated ? ", cut short" : string.Empty)}";
            }

            // Every folder, then as many pictures as are left room for, in
            // name order, and the read still says it was cut short.
            bool Kept(IReadOnlyList<ViewAllEntryDescriptor> entries, bool isTruncated, bool filesByName = true)
            {
                var pictures = entries.Skip(folders.Length).Select(entry => entry.DisplayName).ToList();
                return isTruncated
                    && entries.Count == 32
                    && entries.Take(folders.Length).Select(entry => entry.DisplayName).SequenceEqual(folders)
                    && entries.Skip(folders.Length).All(entry => entry.Kind == ViewAllEntryKind.File)
                    && (!filesByName || pictures.SequenceEqual(pictures.Order(StringComparer.CurrentCultureIgnoreCase)));
            }

            // The list and a dialog, in the default order.
            var listed = await files.GetChildrenAsync(root, options, default, ItemSort.Default, keepFirstShown: true);
            Check($"the list in names from A shows every folder ({Describe(listed.Entries, listed.IsTruncated)})",
                Kept(listed.Entries, listed.IsTruncated));

            // The tree's own read, which keeps the first ones read.
            var read = await files.GetChildrenAsync(root, options);
            Check($"so does the tree's read ({Describe(read.Entries, read.IsTruncated)})", Kept(read.Entries, read.IsTruncated));

            // Newest first already read them all; it still does.
            var newest = await files.GetChildrenAsync(root, options, default, new ItemSort(SortColumn.Modified, true), keepFirstShown: true);
            Check($"and the list newest first, as before ({Describe(newest.Entries, newest.IsTruncated)})",
                Kept(newest.Entries, newest.IsTruncated, filesByName: false));

            // On the tree itself, and Load more brings in the rest once.
            using var graph = new ViewAllGraphService(options);
            var top = (await graph.AddRootAsync(root))!;
            var expansion = await graph.ExpandAsync(top);
            var children = top.Children.Select(child => child.Entry).ToList();
            Check($"the tree opens the folder with every sub-folder ({Describe(children, expansion.IsTruncated)})",
                Kept(children, expansion.IsTruncated));
            var more = await graph.LoadMoreAsync(top, additionalChildren: 64);
            Check($"and Load more brings in the rest, each once ({top.Children.Count} children)",
                !more.IsTruncated && top.Children.Count == 43
                && top.Children.Select(child => child.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 43);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- J017: the workspace save after the camera comes to rest -------------

    /// <summary>
    /// The save that follows every rest of the camera, started on the UI
    /// thread as the window's timer starts it.  It opened, flushed and moved
    /// its file there - milliseconds on an idle disk, hundreds when anything
    /// else has the file: the search indexer or a virus scanner holding it
    /// keeps the move waiting until it lets go, and every window was frozen
    /// for that.  Here the test holds the file as they do (an oplock that
    /// caches its handle) and lets go 300 ms after the move asks for it,
    /// and what is measured is the time a thread standing in for the UI
    /// thread spends on the save.
    /// </summary>
    private static async Task WorkspaceSaveStaysOffTheUiThreadAsync()
    {
        Section("view-all io: the workspace is saved off the UI thread (J017)");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerWorkspaceSave", Guid.NewGuid().ToString("N"));
        try
        {
            // A canvas used for a long while: sixty thousand nodes.
            var state = new ViewAllWorkspaceState
            {
                ViewportX = 12,
                ViewportY = 34,
                ViewportZoom = 0.5,
                ActivePath = @"C:\canvas\folder-000001",
                Nodes = Enumerable.Range(0, 60_000)
                    .Select(index => new ViewAllNodeState($@"C:\canvas\folder-{index:D6}", index * 2.5, index * 0.5, index % 3 == 0, index % 2 == 0))
                    .ToList()
            };
            var statePath = Path.Combine(folder, "view-all.workspace.json");
            var store = new ViewAllWorkspaceStore(statePath);

            // Once first, so what is measured is the save and not its first
            // run, and so there is a file for something else to hold.
            await store.SaveAsync(state);

            using var ui = new TimedUiThread();
            var watch = Stopwatch.StartNew();
            await ui.RunAsync(() => store.SaveAsync(state));
            watch.Stop();
            var idle = ui.BusyMilliseconds;

            using var holder = HeldFile.Hold(statePath, TimeSpan.FromMilliseconds(300));
            using var held = new TimedUiThread();
            var heldWatch = Stopwatch.StartNew();
            await held.RunAsync(() => store.SaveAsync(state));
            heldWatch.Stop();
            var busy = held.BusyMilliseconds;
            Check($"the file was held, and let go once the save asked for it ({holder.Describe()})", holder.WasAskedFor);
            Check($"while it waits for the file the UI thread is free ({busy:0.0} ms of a {heldWatch.Elapsed.TotalMilliseconds:0} ms save on the UI thread; idle disk {idle:0.0} ms of {watch.Elapsed.TotalMilliseconds:0} ms)",
                holder.WasAskedFor && busy < 100);

            var loaded = await store.LoadAsync();
            Check("and the workspace is written whole all the same",
                loaded is { ViewportZoom: 0.5, ActivePath: @"C:\canvas\folder-000001" }
                && loaded.Nodes.Count == 60_000
                && loaded.Nodes[^1] == state.Nodes[^1]);
            Check("with no temporary file left beside it",
                Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(["view-all.workspace.json"]));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>
    /// A thread standing in for the UI thread: it runs what is posted to it
    /// one at a time, as a dispatcher does, and adds up how long it was busy.
    /// </summary>
    private sealed class TimedUiThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private long _busyTicks;

        public TimedUiThread()
        {
            new Thread(Pump) { IsBackground = true, Name = "timed UI thread" }.Start();
        }

        public double BusyMilliseconds => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _busyTicks)).TotalMilliseconds;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Starts <paramref name="work"/> on this thread, and completes with it.</summary>
        public Task RunAsync(Func<Task> work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                var start = Stopwatch.GetTimestamp();
                callback(state);
                Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - start);
            }
        }
    }

    /// <summary>
    /// A file held as the search indexer or a virus scanner holds one: open,
    /// with an oplock that caches its handle.  Whatever then wants to replace
    /// the file waits while the holder is told and lets go, which this one
    /// does <c>delay</c> after it is told.
    /// </summary>
    private sealed class HeldFile : IDisposable
    {
        private const uint FsctlRequestOplock = 0x00090240;
        private const int ErrorIoPending = 997;
        private readonly SafeFileHandle _handle;
        private readonly ManualResetEvent _broken = new(false);
        private readonly IntPtr _buffers;
        private readonly Task _letGo;
        private readonly string _failure;

        private HeldFile(string path, TimeSpan delay)
        {
            _buffers = Marshal.AllocHGlobal(256);
            for (var offset = 0; offset < 256; offset++)
            {
                Marshal.WriteByte(_buffers, offset, 0);
            }

            // REQUEST_OPLOCK_INPUT_BUFFER: version 1, 12 bytes, read and handle caching, a request.
            Marshal.WriteInt16(_buffers, 0, 1);
            Marshal.WriteInt16(_buffers, 2, 12);
            Marshal.WriteInt32(_buffers, 4, 1 | 2);
            Marshal.WriteInt32(_buffers, 8, 1);

            // The OVERLAPPED, whose event is set when the oplock is broken.
            var overlapped = _buffers + 64;
            Marshal.WriteIntPtr(overlapped, IntPtr.Size == 8 ? 24 : 16, _broken.SafeWaitHandle.DangerousGetHandle());

            _handle = CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                _failure = $"open failed {Marshal.GetLastWin32Error()}";
            }
            else if (DeviceIoControl(_handle, FsctlRequestOplock, _buffers, 12, _buffers + 32, 24, IntPtr.Zero, overlapped)
                || Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                _failure = "no oplock granted";
            }
            else
            {
                _failure = string.Empty;
            }

            var held = Stopwatch.StartNew();
            _letGo = Task.Run(() =>
            {
                if (_failure.Length == 0 && _broken.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    WasAskedFor = true;
                    AskedAfter = held.Elapsed;
                    Thread.Sleep(delay);
                }

                _handle.Dispose();
            });
        }

        public bool WasAskedFor { get; private set; }

        public TimeSpan AskedAfter { get; private set; }

        public static HeldFile Hold(string path, TimeSpan delay) => new(path, delay);

        public string Describe() => _failure.Length > 0 ? _failure : WasAskedFor ? $"asked for {AskedAfter.TotalMilliseconds:0} ms in" : "never asked for";

        public void Dispose()
        {
            _letGo.Wait(TimeSpan.FromSeconds(15));
            _handle.Dispose();

            // The request ends when its handle closes; its buffers go after it.
            _broken.WaitOne(TimeSpan.FromSeconds(1));
            Marshal.FreeHGlobal(_buffers);
            _broken.Dispose();
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, IntPtr returned, IntPtr overlapped);
    }
}
