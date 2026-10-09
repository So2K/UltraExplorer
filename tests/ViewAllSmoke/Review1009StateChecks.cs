using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Review of 2026-10-09, state findings: the shelf and the folder marks
/// never write a list they could not read back over the file, a cut is let
/// go of only while the clipboard still holds it, settings clicked during
/// load survive it, a file dialog never writes its layers as defaults, a
/// Back to a folder that is gone keeps the history, volumes are compared as
/// volumes, and Everything's replies are taken only from Everything.
/// </summary>
internal static partial class Program
{
    private static Task Review1009StateChecks()
    {
        RunOnSta("review 2026-10-09 state", Review1009StateOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task Review1009StateOnStaAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ue1009-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Review1009ShelfChecks(root);
            await Review1009MarksChecksAsync(root);
            await Review1009ClipboardChecksAsync(root);
            Review1009HistoryUnitChecks();
            await Review1009HistoryWindowChecksAsync(root);
            await Review1009WorkspaceChecksAsync();
            Review1009VolumeChecks();
            await Review1009EverythingChecksAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- M3: windows' shelves over one file -----------------------------------------

    private static void Review1009ShelfChecks(string root)
    {
        Section("review 1009 M3: two windows' shelves over one file lose nothing of each other's");
        var shelf = Path.Combine(root, "Shelf");
        // Not in the temporary folder, which the shelf copies rather than links.
        var items = Path.Combine(AppPaths.StateDirectory, "ue1009-shelf-items-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(items);
        string Item(string name)
        {
            var path = Path.Combine(items, name);
            File.WriteAllText(path, name);
            return path;
        }

        var (a, b, c, d) = (Item("a.txt"), Item("b.txt"), Item("c.txt"), Item("d.txt"));
        var first = new ShelfStore(shelf);
        var second = new ShelfStore(shelf);
        var heard = 0;
        second.Changed += () => heard++;
        first.AddAsync([a]).GetAwaiter().GetResult();
        second.AddAsync([b]).GetAwaiter().GetResult();
        Check("an item put on one window's shelf is on the other's at once", second.Entries.Any(entry => entry.Path == a) && heard > 0);
        Check("both windows' items are in the file", new ShelfStore(shelf).Entries.Select(entry => entry.Path).Order().SequenceEqual([a, b]));

        // Another copy of the app takes a off and puts c on, behind this
        // process's back: a save here keeps both of its changes.
        var listPath = Path.Combine(shelf, "shelf.json");
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        File.WriteAllText(listPath, JsonSerializer.Serialize(new
        {
            Width = 520.0,
            Entries = new[] { new ShelfEntry(b, DateTime.UtcNow, false), new ShelfEntry(c, DateTime.UtcNow, false) }
        }, options));
        first.SetPositions([(b, 40, 50)]);
        var after = new ShelfStore(shelf).Entries;
        Check("an item another copy took off is not brought back by a stale window's save", after.All(entry => entry.Path != a));
        Check("an item another copy put on is kept, and the stale window's change goes over it",
            after.Any(entry => entry.Path == c) && after.Single(entry => entry.Path == b) is { X: 40, Y: 50 });
        Check("the stale window's own cards follow the file", first.Entries.Select(entry => entry.Path).Order().SequenceEqual([b, c]));

        // The file held by a scanner while a window opens and saves.
        ShelfStore blind;
        using (new FileStream(listPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            blind = new ShelfStore(shelf);
            Check("a shelf opened while its file cannot be read starts empty", blind.Count == 0);
            blind.AddAsync([d]).GetAwaiter().GetResult();
            blind.SetWidth(640);
        }

        var kept = new ShelfStore(shelf);
        Check("a save that could not read the file does not wipe it", kept.Entries.Any(entry => entry.Path == b) && kept.Entries.Any(entry => entry.Path == c));
        // The window that could not read goes on: its next save writes what it held.
        blind.SetWidth(700);
        var healed = new ShelfStore(shelf);
        Check("a later save writes the held changes over what the file has",
            healed.Entries.Select(entry => entry.Path).Order().SequenceEqual([b, c, d]) && healed.Width == 700);
        TryDelete(items);
    }

    // ---- M8: folder marks over a file that cannot be read for a moment --------------

    private static async Task Review1009MarksChecksAsync(string root)
    {
        Section("review 1009 M8: a marks save that cannot read the file does not write its memory over it");
        var statePath = Path.Combine(root, "folder-marks.json");
        var window = new FolderMarkService(statePath);
        await window.LoadAsync();
        var dialog = new FolderMarkService(statePath);
        await dialog.LoadAsync();
        dialog.SetAccent(@"C:\Work\from-dialog", "#EF5A68");
        await dialog.SaveAsync();

        window.SetNote(@"C:\Work\from-window", "written while the file was held");
        using (new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            await window.SaveAsync();
        }

        var held = new FolderMarkService(statePath);
        await held.LoadAsync();
        Check("the other copy's colour is still in the file after the failed save", held.Get(@"C:\Work\from-dialog").AccentHex == "#EF5A68");
        await window.SaveAsync();
        var disk = new FolderMarkService(statePath);
        await disk.LoadAsync();
        Check("the next save writes the held note beside it",
            disk.Get(@"C:\Work\from-dialog").AccentHex == "#EF5A68" && disk.Get(@"C:\Work\from-window").Note == "written while the file was held");
    }

    // ---- M4: a cut let go of only while the clipboard holds it ----------------------

    private static async Task Review1009ClipboardChecksAsync(string root)
    {
        // Never the user's clipboard: the sequence and the clearing are this check's own.
        Section("review 1009 M4: a pasted cut clears only its own clipboard, asked off the window's thread");
        var sequenceBefore = MainViewModel.ClipboardSequence;
        var clearBefore = MainViewModel.ClearClipboard;
        var existsBefore = MainViewModel.ItemExists;
        var sequence = 41u;
        var cleared = 0;
        var askedOn = 0;
        var gone = new[] { Path.Combine(root, "moved-away.txt") };
        try
        {
            MainViewModel.ClipboardSequence = () => sequence;
            MainViewModel.ClearClipboard = () => cleared++;
            MainViewModel.ItemExists = path =>
            {
                askedOn = Environment.CurrentManagedThreadId;
                return File.Exists(path);
            };
            var window = Environment.CurrentManagedThreadId;
            Check("the same clipboard after the move is let go of", await MainViewModel.ReleaseCutAsync(gone, 41) && cleared == 1);
            Check("whether the items are gone is asked off the window's thread", askedOn != 0 && askedOn != window);
            sequence = 42;
            Check("text copied in another program during the move is left alone", !await MainViewModel.ReleaseCutAsync(gone, 41) && cleared == 1);
            var still = Path.Combine(root, "still-here.txt");
            File.WriteAllText(still, "x");
            Check("a cut whose items are still there is kept", !await MainViewModel.ReleaseCutAsync([still], 42) && cleared == 1);
        }
        finally
        {
            MainViewModel.ClipboardSequence = sequenceBefore;
            MainViewModel.ClearClipboard = clearBefore;
            MainViewModel.ItemExists = existsBefore;
        }

        Check("the real clipboard sequence is read", NativeShellService.ClipboardSequence() == NativeShellService.ClipboardSequence());
    }

    // ---- L10c: Back to a place that is gone -----------------------------------------

    private static void Review1009HistoryUnitChecks()
    {
        Section("review 1009 L10c: a history step taken back leaves the history whole");
        var history = new NavigationHistory();
        history.Record(@"C:\a");
        history.Record(@"C:\b");
        history.Record(@"C:\c");
        var from = history.Position;
        var path = history.Back();
        var version = history.Version;
        history.Undo(from, version);
        Check("a step undone is back where it was, nothing ahead of it lost",
            path == @"C:\b" && history.Current == @"C:\c" && history.Count == 3 && history.CanGoBack && !history.CanGoForward);

        from = history.Position;
        history.Back();
        version = history.Version;
        history.Back();
        history.Undo(from, version);
        Check("a step is not undone once another moved the history", history.Current == @"C:\a");
        history.Record(@"C:\d");
        from = history.Position;
        history.Back();
        version = history.Version;
        history.Record(@"C:\e");
        history.Undo(from, version);
        Check("nor once a new place was recorded", history.Current == @"C:\e");
    }

    private static async Task Review1009HistoryWindowChecksAsync(string root)
    {
        Section("review 1009 L10c: Back to a folder deleted since stays put and keeps Forward");
        var places = Path.Combine(root, "places");
        var (gone, here) = (Path.Combine(places, "gone"), Path.Combine(places, "here"));
        Directory.CreateDirectory(gone);
        Directory.CreateDirectory(here);

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(root, "history.view-all.json"));
        await model.InitializeAsync(places);
        await model.Tree.RevealPathAsync(gone);
        await model.Tree.RevealPathAsync(here);
        var history = model.History;
        var recorded = history.Count >= 2 && history.Current is { } current && ViewAllPath.Equals(current, here);
        Check($"both folders are steps of the history ({history.Count}, {history.Current})", recorded);
        if (!recorded) return;

        var count = history.Count;
        Directory.Delete(gone);

        // Deleted outside the app: the window hears of it and lets the folder go.
        var dropped = await Until(() => !model.Tree.TryGetNode(gone, out _), 10_000);
        Check("the window lets the deleted folder go", dropped);
        model.BackCommand.Execute(null);
        await Until(() => history.Current is { } now && ViewAllPath.Equals(now, here), 5_000);
        await Task.Delay(200);
        Check($"the history is back at the folder the window is in ({history.Current})",
            history.Current is { } now && ViewAllPath.Equals(now, here) && history.Count == count && !history.CanGoForward);
        Check("the window did not move to the deleted folder's parent",
            model.Tree.ActivePath is { } active && ViewAllPath.Equals(active, here));
    }

    // ---- L10a/L10b: settings during load, and a dialog's layers ---------------------

    private static async Task Review1009WorkspaceChecksAsync()
    {
        Section("review 1009 L10a/L10b: settings clicked during load survive it; a file dialog never writes its layers as defaults");
        var stateOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && ViewAllPath.Equals(stateOverride!, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory, everydayState)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";
        Check("workspace checks run on isolated state in test-window mode", isolated);
        if (!isolated) return;

        Directory.CreateDirectory(AppPaths.StateDirectory);
        var workspacePath = AppPaths.State("workspace.json");
        var original = File.Exists(workspacePath) ? File.ReadAllBytes(workspacePath) : null;
        var fixture = Path.Combine(AppPaths.StateDirectory, "review1009-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var store = new WorkspaceStore();
        var models = new List<MainViewModel>();
        try
        {
            await store.SaveAsync(new WorkspaceState
            {
                ShowFavoriteLinks = false, IsMinimapVisible = false, CanvasRenderer = "Cpu",
                NestedLeftDrag = WorkspaceState.LeftDragSetting(UltraExplorer.Controls.NestedLeftDrag.SelectArea)
            });
            var early = new MainViewModel
            {
                ShowFavoriteLinks = true,
                IsMinimapVisible = true,
                LeftDrag = UltraExplorer.Controls.NestedLeftDrag.Pan,
                Layers = CanvasLayer.All & ~CanvasLayer.Details
            };
            models.Add(early);
            await early.InitializeAsync(fixture);
            Check("settings clicked before the workspace was read are kept",
                early.ShowFavoriteLinks && early.IsMinimapVisible && early.LeftDrag == UltraExplorer.Controls.NestedLeftDrag.Pan
                && !early.IsLayerShown(CanvasLayer.Details));
            Check("the favourite-links click is written once loading ends", (await store.LoadAsync())?.ShowFavoriteLinks == true);
            await early.SaveNowAsync();
            var saved = await store.LoadAsync();
            Check("the canvas settings clicked during load are written by the next save",
                saved is { IsMinimapVisible: true } && WorkspaceState.ParseLeftDrag(saved.NestedLeftDrag) == UltraExplorer.Controls.NestedLeftDrag.Pan
                && saved.CanvasLayersOff?.Contains("Details") == true);

            // The workspace file gone - first run, or set aside as unreadable.
            File.Delete(workspacePath);
            var picker = new MainViewModel(Path.Combine(fixture, "picker.tree.json"), nestedPicker: true);
            models.Add(picker);
            picker.ConfigurePickerFiles(false);
            picker.Tree.SuppressWrites = true;
            await picker.InitializeAsync(fixture);
            Check("a folder-only file dialog hides its files", !picker.IsLayerShown(CanvasLayer.Files));
            await picker.SaveNowAsync();
            var afterPicker = await store.LoadAsync();
            Check($"its save over a missing workspace does not make hidden files the user's default ({string.Join(",", afterPicker?.CanvasLayersOff ?? [])})",
                afterPicker?.CanvasLayersOff?.Contains("Files") != true);
        }
        finally
        {
            foreach (var model in models) model.Dispose();
            if (original is not null) await File.WriteAllBytesAsync(workspacePath, original);
            else if (File.Exists(workspacePath)) File.Delete(workspacePath);
            TryDelete(fixture);
        }
    }

    // ---- L7: volumes, not drive letters ---------------------------------------------

    private static void Review1009VolumeChecks()
    {
        Section("review 1009 L7: same volume is asked of the volume, not its letter");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Path.GetPathRoot(windows)!;
        Check($"a local folder has a volume GUID path ({NativeShellService.VolumeOf(windows)})",
            NativeShellService.VolumeOf(windows)?.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) == true);
        Check("two folders of the system drive are one volume",
            NativeShellService.IsSameVolume(Path.Combine(windows, "notepad.exe"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var other = DriveInfo.GetDrives().FirstOrDefault(drive => drive.DriveType == DriveType.Fixed && drive.IsReady
            && !string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)
            && NativeShellService.VolumeOf(drive.Name) is { } volume && volume != NativeShellService.VolumeOf(system));
        if (other is not null)
        {
            Check($"another fixed volume ({other.Name}) is another volume", !NativeShellService.IsSameVolume(Path.Combine(windows, "notepad.exe"), other.Name));
        }

        Check("a share is left to its name, never asked of the network",
            NativeShellService.VolumeOf(@"\\ue-review-server\share\folder") is null
            && NativeShellService.IsSameVolume(@"\\ue-review-server\share\a.txt", @"\\ue-review-server\share\b")
            && !NativeShellService.IsSameVolume(@"\\ue-review-server\share\a.txt", @"\\ue-review-server\other\b"));
    }

    // ---- L9: replies only from Everything -------------------------------------------

    private static async Task Review1009EverythingChecksAsync()
    {
        Section("review 1009 L9: a reply is taken only from the Everything process asked, under an id no one else saw");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var client = EverythingClient.Shared;
        var reply = typeof(EverythingClient).GetMethod("EnsureWindow", flags)!.Invoke(client, null) is nint made ? made : 0;
        var pendingType = typeof(EverythingClient).GetNestedType("PendingReply", BindingFlags.NonPublic)!;
        var pending = (IDictionary)typeof(EverythingClient).GetField("_pending", flags)!.GetValue(client)!;
        Check("the reply window is made", reply != 0);
        if (reply == 0) return;

        TaskCompletionSource<byte[]> Wait(uint id, uint process)
        {
            var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = Activator.CreateInstance(pendingType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [process, answer], null);
            return answer;
        }

        bool Send(nint sender, uint id)
        {
            var data = Marshal.AllocHGlobal(4);
            try
            {
                var copy = new ReviewCopyData { Data = (nint)id, Size = 4, Pointer = data };
                return ReviewSendCopyData(reply, 0x004A, sender, ref copy, 0x0002, 2000, out var taken) != 0 && taken != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        const uint someoneElse = 4;
        var ours = (uint)Environment.ProcessId;
        try
        {
            var spoofed = Wait(0x1009_0001, someoneElse);
            var fromHere = Send(reply, 0x1009_0001);
            var fromNowhere = Send(0, 0x1009_0001);
            Check("a reply from another process than Everything's is refused", !fromHere && !fromNowhere && !spoofed.Task.IsCompleted);
            pending.Remove(0x1009_0001u);

            var genuine = Wait(0x1009_0002, ours);
            Check("the asked process's reply under its id is taken", Send(reply, 0x1009_0002) && genuine.Task.IsCompletedSuccessfully);
            pending.Remove(0x1009_0002u);

            var guessed = Wait(0x1009_0003, ours);
            for (var guess = 0u; guess < 16; guess++) Send(reply, 0x2000_0000 + guess);
            Check("a run of wrong ids ends the query rather than waiting for a hit",
                guessed.Task.IsFaulted && guessed.Task.Exception?.InnerException is TimeoutException);
            pending.Remove(0x1009_0003u);
        }
        finally
        {
            foreach (var id in new[] { 0x1009_0001u, 0x1009_0002u, 0x1009_0003u }) pending.Remove(id);
        }

        if (!EverythingClient.IsDatabaseLoaded)
        {
            Console.WriteLine("  (Everything is not running here: the live query is skipped)");
            return;
        }

        var page = await client.QueryAsync("windows", 5, CancellationToken.None);
        Check($"a real query still gets Everything's reply ({page?.Items.Count} of {page?.Total})", page is { Items.Count: > 0 });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReviewCopyData
    {
        public nint Data;
        public uint Size;
        public nint Pointer;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint ReviewSendCopyData(nint window, uint message, nint wParam, ref ReviewCopyData lParam, uint flags, uint timeout, out nint result);
}
