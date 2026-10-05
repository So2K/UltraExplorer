using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace FolderTagsSmoke;

internal static class Program
{
    private static int _checks;
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var exitCode = 1;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RunAsync();
                exitCode = _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL {ex}");
                _failures++;
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "UltraExplorerFolderTags", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var statePath = Path.Combine(scratch, "folder-marks.json");
            var redFolder = Path.Combine(scratch, "Red project");
            var yellowFolder = Path.Combine(scratch, "Yellow project");
            var externalFolder = Path.Combine(scratch, "External project");
            var slowFolder = Path.Combine(scratch, "Slow project");
            var afterDisposeFolder = Path.Combine(scratch, "After dispose");
            var colouredFile = Path.Combine(scratch, "coloured.txt");
            var noteOnlyFolder = Path.Combine(scratch, "Notes only");
            var duplicateA = Path.Combine(scratch, "Alpha", "Shared");
            var duplicateB = Path.Combine(scratch, "Beta", "Shared");
            var blocked1 = Path.Combine(scratch, "Blocked 1");
            var blocked2 = Path.Combine(scratch, "Blocked 2");
            var blocked3 = Path.Combine(scratch, "Blocked 3");
            var validBehindBlocked = Path.Combine(scratch, "Valid after blocked");
            var periodicFolder = Path.Combine(scratch, "Periodic import");
            var temporarilyUnavailable = Path.Combine(scratch, "Temporarily unavailable");
            var becomesFile = Path.Combine(scratch, "Unknown becomes file");
            var becomesFolder = Path.Combine(scratch, "Unknown becomes folder");
            var timedOutUnknown = Path.Combine(scratch, "Timed out unknown");
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                redFolder, yellowFolder, externalFolder, slowFolder, noteOnlyFolder, afterDisposeFolder,
                duplicateA, duplicateB, blocked1, blocked2, blocked3, validBehindBlocked, periodicFolder
            };
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { colouredFile };
            var delayed = new ConcurrentDictionary<string, TaskCompletionSource<FolderTagPathKind>>(StringComparer.OrdinalIgnoreCase);

            ValueTask<FolderTagPathKind> Classify(string path, CancellationToken cancellationToken)
            {
                if (delayed.TryGetValue(path, out var wait))
                {
                    return new ValueTask<FolderTagPathKind>(wait.Task.WaitAsync(cancellationToken));
                }

                return ValueTask.FromResult(folderPaths.Contains(path)
                    ? FolderTagPathKind.Folder
                    : filePaths.Contains(path) ? FolderTagPathKind.File : FolderTagPathKind.Unknown);
            }

            var marks = new FolderMarkService(statePath);
            marks.SetAccent(redFolder, "#EF5A68");
            marks.SetAccent(colouredFile, "#60CDFF");
            marks.SetNote(noteOnlyFolder, "keep the note, not a tag");

            using var projection = new FolderTagProjection(marks, Dispatcher.CurrentDispatcher, Classify);
            await projection.InitializeAsync();

            Check("only coloured folders become tags",
                projection.Items.Count == 1 && projection.Items[0].FullPath == redFolder);
            Check("a tag keeps its canonical navigation path",
                projection.Items[0].FullPath == redFolder);
            Check("a tag exposes its colour and full path",
                projection.Items[0].ColourName == "Red" && projection.Items[0].ToolTipText == redFolder);
            Check("folder kind is learned once and coloured files are remembered as files",
                marks.Get(redFolder).IsDirectory == true && marks.Get(colouredFile).IsDirectory == false);

            marks.SetAccent(yellowFolder, "#E3B341");
            marks.SetAccent(redFolder, "#4ED6A0");
            await EventuallyAsync(() => projection.Items.Count == 2
                && projection.Items[0].FullPath == yellowFolder
                && projection.Items[1].AccentHex == "#4ED6A0");
            Check("colour changes update once and keep palette order",
                projection.Items.Select(item => item.FullPath).SequenceEqual([yellowFolder, redFolder]));

            var collectionChanges = 0;
            ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, _) => collectionChanges++;
            await projection.RefreshAsync();
            Check("an unchanged activation refresh does not rebuild tag rows", collectionChanges == 0);

            projection.SetActivePath(redFolder);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check("the active folder is highlighted in Tags",
                projection.Items.Single(item => item.FullPath == redFolder).StateTag == "Active");

            marks.SetNote(redFolder, "important");
            marks.SetAccent(redFolder, null);
            await EventuallyAsync(() => projection.Items.All(item => item.FullPath != redFolder));
            Check("Unpin removes only the colour and preserves the note",
                marks.Get(redFolder) is { AccentHex.Length: 0, Note: "important" });

            var slowProbe = new TaskCompletionSource<FolderTagPathKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            delayed[slowFolder] = slowProbe;
            marks.SetAccent(slowFolder, "#EF5A68");
            await Task.Delay(20);
            marks.SetAccent(slowFolder, null);
            slowProbe.TrySetResult(FolderTagPathKind.Folder);
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check("a cleared pending tag is never resurrected",
                projection.Items.All(item => item.FullPath != slowFolder));

            var blockedProbes = new[] { blocked1, blocked2, blocked3 }
                .ToDictionary(
                    path => path,
                    _ => new TaskCompletionSource<FolderTagPathKind>(TaskCreationOptions.RunContinuationsAsynchronously),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var (path, wait) in blockedProbes)
            {
                delayed[path] = wait;
                marks.SetAccent(path, "#EF5A68");
            }

            marks.SetAccent(validBehindBlocked, "#4ED6A0");
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == validBehindBlocked), TimeSpan.FromSeconds(3));
            Check("three stalled probes cannot starve a later local tag",
                projection.Items.Any(item => item.FullPath == validBehindBlocked));
            foreach (var (path, wait) in blockedProbes)
            {
                marks.SetAccent(path, null);
                wait.TrySetResult(FolderTagPathKind.Folder);
            }

            marks.SetAccent(temporarilyUnavailable, "#E3B341");
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == temporarilyUnavailable));
            Check("an unavailable legacy path stays visible and its kind remains retryable",
                projection.Items.Any(item => item.FullPath == temporarilyUnavailable)
                && marks.Get(temporarilyUnavailable).IsDirectory is null);
            marks.SetAccent(temporarilyUnavailable, null);

            marks.SetAccent(becomesFile, "#E3B341");
            marks.SetAccent(becomesFolder, "#4ED6A0");
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == becomesFile)
                && projection.Items.Any(item => item.FullPath == becomesFolder));
            filePaths.Add(becomesFile);
            folderPaths.Add(becomesFolder);
            typeof(FolderTagProjection).GetField("_lastReconcileUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(projection, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            await projection.RefreshAsync();
            await EventuallyAsync(() => projection.Items.All(item => item.FullPath != becomesFile)
                && projection.Items.Any(item => item.FullPath == becomesFolder));
            Check("periodic retry resolves Unknown to File or Folder without a permanent false decision",
                marks.Get(becomesFile).IsDirectory == false
                && marks.Get(becomesFolder).IsDirectory == true
                && projection.Items.All(item => item.FullPath != becomesFile)
                && projection.Items.Any(item => item.FullPath == becomesFolder));

            var timedOutProbe = new TaskCompletionSource<FolderTagPathKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            delayed[timedOutUnknown] = timedOutProbe;
            marks.SetAccent(timedOutUnknown, "#60CDFF");
            await EventuallyAsync(
                () => projection.Items.Any(item => item.FullPath == timedOutUnknown),
                TimeSpan.FromSeconds(3));
            Check("a timed-out classifier leaves the coloured path visible and retryable",
                projection.Items.Any(item => item.FullPath == timedOutUnknown)
                && marks.Get(timedOutUnknown).IsDirectory is null);
            marks.SetAccent(timedOutUnknown, null);
            timedOutProbe.TrySetResult(FolderTagPathKind.Unknown);

            projection.SetActivePath(noteOnlyFolder);
            marks.SetAccent(noteOnlyFolder, "#F28A4B");
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == noteOnlyFolder));
            Check("a newly tagged active folder is highlighted immediately",
                projection.Items.Single(item => item.FullPath == noteOnlyFolder).IsActive);

            await marks.SaveAsync();
            var anotherProcess = new FolderMarkService(statePath);
            await anotherProcess.LoadAsync();
            anotherProcess.SetAccent(externalFolder, "#A979FF");
            await anotherProcess.SaveAsync();
            await projection.RefreshAsync();
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == externalFolder));
            Check("activation refresh imports another process's tag",
                projection.Items.Any(item => item.FullPath == externalFolder && item.ColourName == "Violet"));

            anotherProcess.SetAccent(externalFolder, null);
            await anotherProcess.SaveAsync();
            await projection.RefreshAsync();
            await EventuallyAsync(() => projection.Items.All(item => item.FullPath != externalFolder));
            Check("an external Unpin is removed on activation refresh",
                projection.Items.All(item => item.FullPath != externalFolder));

            anotherProcess.SetAccent(periodicFolder, "#4ED6A0");
            await anotherProcess.SaveAsync();
            var marksInfo = new FileInfo(statePath);
            var stampField = typeof(FolderTagProjection).GetField("_marksFileStamp", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var acknowledgedUnseenStamp = Activator.CreateInstance(
                stampField.FieldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [marksInfo.Length, marksInfo.LastWriteTimeUtc.Ticks],
                culture: null);
            stampField.SetValue(projection, acknowledgedUnseenStamp);
            typeof(FolderTagProjection).GetField("_lastReconcileUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(projection, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            await projection.RefreshAsync();
            await EventuallyAsync(() => projection.Items.Any(item => item.FullPath == periodicFolder));
            Check("periodic refresh loads even an externally changed file with an acknowledged stamp",
                projection.Items.Any(item => item.FullPath == periodicFolder));

            marks.SetAccent(duplicateA, "#60CDFF");
            marks.SetAccent(duplicateB, "#60CDFF");
            await EventuallyAsync(() => projection.Items.Count(item => item.Name == "Shared") == 2);
            Check("same-name folders show different parent hints and accessible paths",
                projection.Items.Where(item => item.Name == "Shared").Select(item => item.ParentHint).ToHashSet().SetEquals(["— Alpha", "— Beta"])
                && projection.Items.Where(item => item.Name == "Shared").All(item => item.AccessibleName.Contains(item.FullPath, StringComparison.Ordinal)));

            var beforeDispose = projection.Items.Count;
            projection.Dispose();
            marks.SetAccent(afterDisposeFolder, "#F28A4B");
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check("disposed projections stop receiving changes", projection.Items.Count == beforeDispose);

            var cacheState = Path.Combine(scratch, "cache-marks.json");
            var cacheMarks = new FolderMarkService(cacheState);
            using (var cacheProjection = new FolderTagProjection(cacheMarks, Dispatcher.CurrentDispatcher))
            {
                await cacheProjection.InitializeAsync();
                var localFolders = Enumerable.Range(0, 250)
                    .Select(index => Path.Combine(scratch, "cache", index.ToString("D3")))
                    .ToArray();
                foreach (var path in localFolders)
                {
                    Directory.CreateDirectory(path);
                    cacheMarks.SetAccent(path, "#60CDFF");
                }

                await EventuallyAsync(() => cacheProjection.Items.Count == localFolders.Length, TimeSpan.FromSeconds(5));
                foreach (var path in localFolders)
                {
                    cacheMarks.SetAccent(path, null);
                }

                await EventuallyAsync(() => cacheProjection.Items.Count == 0);
                var remoteMarks = Enumerable.Range(0, 600)
                    .Select(index => $@"\\offline-{index:D3}\share\folder")
                    .ToArray();
                foreach (var path in remoteMarks)
                {
                    cacheMarks.SetAccent(path, "#E3B341");
                }

                await EventuallyAsync(
                    () => cacheProjection.Items.Count(item => item.FullPath.StartsWith("\\\\offline-", StringComparison.Ordinal)) == remoteMarks.Length,
                    TimeSpan.FromSeconds(3));
                var probes = typeof(FolderTagProjection).GetField("DirectoryProbes", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null)!;
                var countProperty = probes.GetType().GetProperty("Count")!;
                var boundedCount = (int)countProperty.GetValue(probes)!;
                Check("legacy remote folder tags never start an uncancellable UNC probe",
                    boundedCount == 0 && cacheProjection.Items.Any(item => item.FullPath == remoteMarks[^1]));
                var colouredRemoteFile = @"\\offline-file\share\picture.png";
                cacheMarks.SetAccent(colouredRemoteFile, "#60CDFF", isDirectory: false);
                await Task.Delay(30);
                Check("a remote mark known to be a file is not shown as a folder tag",
                    cacheProjection.Items.All(item => item.FullPath != colouredRemoteFile));
                foreach (var path in remoteMarks)
                {
                    cacheMarks.SetAccent(path, null);
                }

                cacheMarks.SetAccent(colouredRemoteFile, null);

                await Task.Delay(100);
                var clearedCount = (int)countProperty.GetValue(probes)!;
                Check("cleared local and UNC tags release their cached probes", clearedCount == 0);
                var revisions = typeof(FolderTagProjection).GetField("_revisions", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(cacheProjection)!;
                var revisionCount = (int)revisions.GetType().GetProperty("Count")!.GetValue(revisions)!;
                Check("cleared tags do not leave an ever-growing revision history", revisionCount == 0);
            }

            await CombinedKindCompatibilityChecksAsync(scratch);

            var root = Directory.GetCurrentDirectory();
            var xaml = File.ReadAllText(Path.Combine(root, "src", "UltraExplorer", "MainWindow.xaml"));
            var tagsCode = File.ReadAllText(Path.Combine(root, "src", "UltraExplorer", "MainWindow.Tags.cs"));
            Check("Tags are placed below Network in the navigation pane",
                xaml.IndexOf("NetworkLocations", StringComparison.Ordinal) < xaml.IndexOf("FolderTagsSection", StringComparison.Ordinal));
            Check("the requested context actions are present",
                tagsCode.Contains("\"Unpin\"", StringComparison.Ordinal)
                && tagsCode.Contains("\"Change color\"", StringComparison.Ordinal));
            Check("tag navigation requires the exact folder and cannot select a surviving parent",
                xaml.Contains("Click=\"FolderTagItem_Click\"", StringComparison.Ordinal)
                && tagsCode.Contains("RevealAsync(tag.FullPath, exact: true)", StringComparison.Ordinal));
            Check("the tag menu is available from Shift+F10 and the Menu key",
                xaml.Contains("PreviewKeyDown=\"FolderTagItem_PreviewKeyDown\"", StringComparison.Ordinal)
                && tagsCode.Contains("Key.Apps", StringComparison.Ordinal)
                && tagsCode.Contains("Key.F10", StringComparison.Ordinal)
                && tagsCode.Contains("e.SystemKey", StringComparison.Ordinal));
            var keyRule = typeof(MainWindow).GetMethod("OpensFolderTagMenu", BindingFlags.NonPublic | BindingFlags.Static);
            Check("the keyboard rule handles WPF's SystemKey form of Shift+F10",
                keyRule is not null
                && (bool)keyRule.Invoke(null, [Key.System, Key.F10, ModifierKeys.Shift])!
                && (bool)keyRule.Invoke(null, [Key.Apps, Key.None, ModifierKeys.None])!
                && !(bool)keyRule.Invoke(null, [Key.F10, Key.None, ModifierKeys.None])!);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task CombinedKindCompatibilityChecksAsync(string scratch)
    {
        var combined = Path.Combine(scratch, "combined-kind");
        Directory.CreateDirectory(combined);

        var noteMoveState = Path.Combine(combined, "note-move.json");
        var noteOld = Path.Combine(combined, "Note old");
        var noteMoved = Path.Combine(combined, "Note moved");
        var noteMove = new FolderMarkService(noteMoveState);
        noteMove.SetNote(noteOld, "note without a colour");
        noteMove.SetItemKind(noteOld, isDirectory: true);
        noteMove.Move(noteOld, noteMoved);
        var noteMovedInMemory = noteMove.Get(noteMoved);
        await noteMove.SaveAsync();
        var noteMoveDisk = new FolderMarkService(noteMoveState);
        await noteMoveDisk.LoadAsync();
        Check("a note-only mark keeps folder kind through an atomic move and reload",
            noteMove.Get(noteOld).IsEmpty
            && noteMovedInMemory is { Note: "note without a colour", IsDirectory: true }
            && noteMoveDisk.Get(noteMoved) is { Note: "note without a colour", IsDirectory: true });

        var caseMoveState = Path.Combine(combined, "case-move.json");
        var oldRoot = Path.Combine(combined, "Case project");
        var newRoot = Path.Combine(combined, "case project");
        var oldChild = Path.Combine(oldRoot, "inside.txt");
        var newChild = Path.Combine(newRoot, "inside.txt");
        var caseMove = new FolderMarkService(caseMoveState);
        caseMove.SetAccent(oldRoot, "#EF5A68", isDirectory: true);
        caseMove.SetNote(oldChild, "inside");
        caseMove.SetItemKind(oldChild, isDirectory: false);
        caseMove.Move(oldRoot, newRoot);
        var movedKeys = caseMove.Snapshot().Select(pair => pair.Key).ToArray();
        await caseMove.SaveAsync();
        var caseMoveDisk = new FolderMarkService(caseMoveState);
        await caseMoveDisk.LoadAsync();
        Check("a case-only folder rename carries descendant kinds and their new spelling",
            movedKeys.Contains(newRoot, StringComparer.Ordinal)
            && movedKeys.Contains(newChild, StringComparer.Ordinal)
            && caseMoveDisk.Get(newRoot).IsDirectory == true
            && caseMoveDisk.Get(newChild) is { Note: "inside", IsDirectory: false });

        var seedState = Path.Combine(combined, "seed.json");
        var seededPath = Path.Combine(combined, "Seeded");
        var seedInitial = new FolderMarkService(seedState);
        seedInitial.SetAccent(seededPath, "#60CDFF", isDirectory: true);
        await seedInitial.SaveAsync();
        var seeder = new FolderMarkService(seedState);
        await seeder.LoadAsync();
        seeder.Seed(seededPath, "#E3B341", "from legacy workspace");
        var seedKeptKindInMemory = seeder.Get(seededPath).IsDirectory == true;
        var seedConcurrent = new FolderMarkService(seedState);
        await seedConcurrent.LoadAsync();
        seedConcurrent.SetItemKind(seededPath, isDirectory: false);
        await seedConcurrent.SaveAsync();
        await seeder.SaveAsync();
        var seedDisk = new FolderMarkService(seedState);
        await seedDisk.LoadAsync();
        Check("legacy Seed keeps in-memory kind but its Accent/Note mask preserves a newer disk kind",
            seedKeptKindInMemory
            && seedDisk.Get(seededPath) is
            {
                AccentHex: "#E3B341",
                Note: "from legacy workspace",
                IsDirectory: false
            });

        var pendingSeedState = Path.Combine(combined, "pending-seed.json");
        var pendingSeedPath = Path.Combine(combined, "Pending seed");
        var pendingSeed = new FolderMarkService(pendingSeedState);
        pendingSeed.SetNote(pendingSeedPath, "before seed");
        pendingSeed.SetItemKind(pendingSeedPath, isDirectory: true);
        pendingSeed.Seed(pendingSeedPath, "#F28A4B", "seeded before first save");
        await pendingSeed.SaveAsync();
        var pendingSeedDisk = new FolderMarkService(pendingSeedState);
        await pendingSeedDisk.LoadAsync();
        Check("Seed keeps an unsaved prior Kind change in its accumulated save mask",
            pendingSeedDisk.Get(pendingSeedPath) is
            {
                AccentHex: "#F28A4B",
                Note: "seeded before first save",
                IsDirectory: true
            });

        var saveOrders = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };
        var everySaveOrderKeptAllHalves = true;
        for (var orderIndex = 0; orderIndex < saveOrders.Length; orderIndex++)
        {
            var state = Path.Combine(combined, $"halves-{orderIndex}.json");
            var path = Path.Combine(combined, $"Three halves {orderIndex}");
            var initial = new FolderMarkService(state);
            initial.SetAccent(path, "#60CDFF", isDirectory: true);
            initial.SetNote(path, "before");
            await initial.SaveAsync();

            var accent = new FolderMarkService(state);
            var note = new FolderMarkService(state);
            var kind = new FolderMarkService(state);
            await accent.LoadAsync();
            await note.LoadAsync();
            await kind.LoadAsync();
            accent.SetAccent(path, "#A979FF");
            note.SetNote(path, "after");
            kind.SetItemKind(path, isDirectory: false);
            var writers = new[] { accent, note, kind };
            foreach (var writer in saveOrders[orderIndex])
            {
                await writers[writer].SaveAsync();
            }

            var disk = new FolderMarkService(state);
            await disk.LoadAsync();
            everySaveOrderKeptAllHalves &= disk.Get(path) is
            {
                AccentHex: "#A979FF",
                Note: "after",
                IsDirectory: false
            };
        }

        Check("interleaved Accent, Note and Kind saves retain all three halves in every order",
            everySaveOrderKeptAllHalves);

        var reloadState = Path.Combine(combined, "kind-reload.json");
        var reloadPath = Path.Combine(combined, "Reloaded tag");
        var reloadWriter = new FolderMarkService(reloadState);
        reloadWriter.SetAccent(reloadPath, "#4ED6A0");
        await reloadWriter.SaveAsync();
        var reloadReader = new FolderMarkService(reloadState);
        var classifyCalls = 0;
        using (var reloadProjection = new FolderTagProjection(
                   reloadReader,
                   Dispatcher.CurrentDispatcher,
                   (_, _) =>
                   {
                       classifyCalls++;
                       return ValueTask.FromResult(FolderTagPathKind.Unknown);
                   }))
        {
            await reloadProjection.InitializeAsync();
            await EventuallyAsync(() => reloadProjection.Items.Any(item => item.FullPath == reloadPath));
            reloadWriter.SetItemKind(reloadPath, isDirectory: false);
            await reloadWriter.SaveAsync();
            await reloadReader.LoadAsync();
            await EventuallyAsync(() => reloadProjection.Items.All(item => item.FullPath != reloadPath));
            var removedAsFile = reloadProjection.Items.All(item => item.FullPath != reloadPath);

            reloadWriter.SetItemKind(reloadPath, isDirectory: true);
            await reloadWriter.SaveAsync();
            await reloadReader.LoadAsync();
            await EventuallyAsync(() => reloadProjection.Items.Any(item => item.FullPath == reloadPath));
            Check("kind-only shared reload events remove a file and restore a folder without another probe",
                removedAsFile
                && reloadProjection.Items.Any(item => item.FullPath == reloadPath)
                && classifyCalls == 1);
        }

        var listenersState = Path.Combine(combined, "listeners.json");
        var listeners = new FolderMarkService(listenersState);
        var firstPath = Path.Combine(combined, "First listener tag");
        var survivorPath = Path.Combine(combined, "Survivor tag");
        var firstProjection = new FolderTagProjection(listeners, Dispatcher.CurrentDispatcher);
        using var survivingProjection = new FolderTagProjection(listeners, Dispatcher.CurrentDispatcher);
        await firstProjection.InitializeAsync();
        await survivingProjection.InitializeAsync();
        listeners.SetAccent(firstPath, "#60CDFF", isDirectory: true);
        await EventuallyAsync(() => firstProjection.Items.Count == 1 && survivingProjection.Items.Count == 1);
        var disposedCount = firstProjection.Items.Count;
        firstProjection.Dispose();
        listeners.SetAccent(survivorPath, "#EF5A68", isDirectory: true);
        await EventuallyAsync(() => survivingProjection.Items.Any(item => item.FullPath == survivorPath));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check("disposing one Tags subscriber leaves it still while the surviving subscriber keeps receiving changes",
            firstProjection.Items.Count == disposedCount
            && firstProjection.Items.All(item => item.FullPath != survivorPath)
            && survivingProjection.Items.Any(item => item.FullPath == survivorPath));
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
        }
    }

    private static void Check(string name, bool passed)
    {
        _checks++;
        if (!passed)
        {
            _failures++;
        }

        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    }
}
