using System.Diagnostics;
using System.IO;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The marks file, kept by more than one copy of the app at a time - the
/// window, a replaced dialog, the prepared picker, a second instance: a save
/// writes what this copy changed over the file as it is now, never the whole
/// set it read at its start, so no copy erases another's colours and notes;
/// a copy read again has the file's marks and keeps its own unsaved ones,
/// and the ones a save of its own is still writing;
/// and colouring thousands of files at once costs each file once, not once
/// per mark there is.
/// </summary>
internal static partial class Program
{
    private static async Task MarksPersistenceChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerMarksPersistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await MarksMergeChecksAsync(root);
            await MarksReloadChecksAsync(root);
            MarksBulkChecks();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<FolderMarkService> MarksFromDiskAsync(string statePath)
    {
        var marks = new FolderMarkService(statePath);
        await marks.LoadAsync();
        return marks;
    }

    private static async Task MarksMergeChecksAsync(string root)
    {
        Section("folder marks: each copy saves what it changed, over the file as it is now");
        var statePath = Path.Combine(root, "folder-marks.json");

        // The window and a replaced dialog, both open on the same file.
        var window = await MarksFromDiskAsync(statePath);
        var dialog = await MarksFromDiskAsync(statePath);
        dialog.SetAccent(@"C:\Work\from-dialog", "#EF5A68");
        await dialog.SaveAsync();
        window.SetNote(@"C:\Work\from-window", "written in the window");
        await window.SaveAsync();
        var disk = await MarksFromDiskAsync(statePath);
        Check("a colour set in a dialog survives the window's next save", disk.Get(@"C:\Work\from-dialog").AccentHex == "#EF5A68");
        Check("and the window's own note is saved beside it", disk.Get(@"C:\Work\from-window").Note == "written in the window");

        // A copy loaded long ago - the prepared picker - and a note made in
        // the window since.
        var stale = await MarksFromDiskAsync(statePath);
        window.SetNote(@"C:\Work\later", "made after the picker loaded");
        await window.SaveAsync();
        var before = File.ReadAllBytes(statePath);
        await stale.SaveAsync();
        Check("a copy that changed nothing writes nothing", File.ReadAllBytes(statePath).AsSpan().SequenceEqual(before));
        stale.SetAccent(@"C:\Work\from-picker", "#60CDFF");
        await stale.SaveAsync();
        disk = await MarksFromDiskAsync(statePath);
        Check("a stale copy's save keeps the notes made since it loaded",
            disk.Get(@"C:\Work\later").Note == "made after the picker loaded"
            && disk.Get(@"C:\Work\from-picker").AccentHex == "#60CDFF"
            && disk.Get(@"C:\Work\from-dialog").AccentHex == "#EF5A68");

        // The two halves of one mark, each changed in another copy.
        var shared = @"C:\Work\shared";
        window.SetAccent(shared, "#E3B341");
        await window.SaveAsync();
        var other = await MarksFromDiskAsync(statePath);
        other.SetNote(shared, "noted in the other copy");
        await other.SaveAsync();
        window.SetAccent(shared, "#9B6BFF");
        await window.SaveAsync();
        disk = await MarksFromDiskAsync(statePath);
        Check("a colour changed in one copy keeps the note written in another",
            disk.Get(shared) is { AccentHex: "#9B6BFF", Note: "noted in the other copy" });

        // A mark cleared in one copy, still held by another that saves.
        window.SetAccent(@"C:\Work\cleared", "#EF5A68");
        await window.SaveAsync();
        var holder = await MarksFromDiskAsync(statePath);
        var cleaner = await MarksFromDiskAsync(statePath);
        cleaner.SetAccent(@"C:\Work\cleared", null);
        await cleaner.SaveAsync();
        holder.SetNote(@"C:\Work\unrelated", "something else");
        await holder.SaveAsync();
        disk = await MarksFromDiskAsync(statePath);
        Check("a mark cleared in one copy is not put back by another's save",
            disk.Get(@"C:\Work\cleared").IsEmpty && disk.Get(@"C:\Work\unrelated").Note == "something else");

        // Several copies saving at once, as processes do at logoff.
        var copies = new FolderMarkService[4];
        for (var index = 0; index < copies.Length; index++)
        {
            copies[index] = await MarksFromDiskAsync(statePath);
        }

        await Task.WhenAll(copies.Select((copy, index) => Task.Run(async () =>
        {
            for (var round = 0; round < 10; round++)
            {
                copy.SetNote($@"C:\Work\copy-{index}\note-{round}", "kept");
                await copy.SaveAsync();
            }
        })));
        disk = await MarksFromDiskAsync(statePath);
        var kept = Enumerable.Range(0, copies.Length)
            .Sum(index => Enumerable.Range(0, 10).Count(round => disk.Get($@"C:\Work\copy-{index}\note-{round}").Note == "kept"));
        Check($"saves made at once by several copies keep every copy's notes ({kept} of {copies.Length * 10})", kept == copies.Length * 10);
        Check("and leave no temporary file behind", Directory.GetFiles(root, "*.tmp").Length == 0);

        // A mark seeded from an old workspace is a change of this copy's.
        var seededPath = Path.Combine(root, "seeded.json");
        var seeded = new FolderMarkService(seededPath);
        seeded.Seed(@"C:\Legacy", "#EF5A68", "from the old workspace");
        await seeded.SaveAsync();
        Check("a mark seeded from an old workspace is saved", (await MarksFromDiskAsync(seededPath)).Get(@"C:\Legacy").Note == "from the old workspace");

        // A file damaged since it was read is set aside, not written over.
        File.WriteAllText(statePath, "{ this is not json");
        window.SetNote(@"C:\Work\after-damage", "written after the damage");
        await window.SaveAsync();
        var setAside = Directory.GetFiles(root, "folder-marks.json.corrupt-*");
        disk = await MarksFromDiskAsync(statePath);
        Check("a marks file damaged since the load is set aside before a save replaces it",
            setAside.Length == 1 && disk.Get(@"C:\Work\after-damage").Note == "written after the damage"
            && disk.Get(@"C:\Work\from-window").Note == "written in the window");

        // A save that cannot be written is tried again by the next.
        var blocked = await MarksFromDiskAsync(statePath);
        blocked.SetNote(@"C:\Work\retried", "written on the second try");
        using (new FileStream(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await blocked.SaveAsync();
        }

        await blocked.SaveAsync();
        Check("a change whose save failed goes with the next one", (await MarksFromDiskAsync(statePath)).Get(@"C:\Work\retried").Note == "written on the second try");
    }

    private static async Task MarksReloadChecksAsync(string root)
    {
        Section("folder marks: read again, a copy has the file's marks and keeps its own unsaved ones");
        var statePath = Path.Combine(root, "reloaded-marks.json");
        var window = new FolderMarkService(statePath);
        window.SetAccent(@"C:\Work\cleared-later", "#EF5A68");
        await window.SaveAsync();

        var picker = await MarksFromDiskAsync(statePath);
        window.SetAccent(@"C:\Work\cleared-later", null);
        window.SetNote(@"C:\Work\added-later", "added in the window");
        await window.SaveAsync();
        picker.SetNote(@"C:\Work\unsaved", "not saved yet");
        await picker.LoadAsync();
        Check("read again, a copy has the marks added since", picker.Get(@"C:\Work\added-later").Note == "added in the window");
        Check("and no longer the ones cleared since", picker.Get(@"C:\Work\cleared-later").IsEmpty);
        Check("and keeps its own change not saved yet", picker.Get(@"C:\Work\unsaved").Note == "not saved yet");
        Check("and knows which folders hold marks now",
            picker.MarkedFolders.Contains(@"C:\Work") && picker.MarkedFolders.Count == 1);
        await picker.SaveAsync();
        var disk = await MarksFromDiskAsync(statePath);
        Check("and saves that change afterwards", disk.Get(@"C:\Work\unsaved").Note == "not saved yet"
            && disk.Get(@"C:\Work\added-later").Note == "added in the window");

        // Read again while a save is still waiting for the file's turn - a
        // dialog's closing save and the next bind: the save has taken its
        // changes, and the file does not hold them yet.
        var turnName = (string)typeof(FolderMarkService)
            .GetField("_turnName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(picker)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            // Another copy, in the middle of its own save.
            using var turn = new Mutex(false, turnName);
            turn.WaitOne();
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            turn.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        held.Wait(TimeSpan.FromSeconds(10));
        picker.SetAccent(@"C:\Work\coloured-mid-save", "#E3B341");
        var save = picker.SaveAsync();
        var reload = picker.LoadAsync();
        await Task.WhenAny(reload, Task.Delay(500));
        var reloadWaited = !reload.IsCompleted;
        release.Set();
        await Task.WhenAll(save, reload);
        holder.Join(TimeSpan.FromSeconds(10));
        Check("read again during a save, a copy waits for the save to finish", reloadWaited);
        Check("and keeps the colour that save was writing", picker.Get(@"C:\Work\coloured-mid-save").AccentHex == "#E3B341");
        await picker.SaveAsync();
        Check("and the file keeps it too, after the next save",
            (await MarksFromDiskAsync(statePath)).Get(@"C:\Work\coloured-mid-save").AccentHex == "#E3B341");
    }

    private static void MarksBulkChecks()
    {
        Section("folder marks: colouring a large selection costs each file once");
        var marks = new FolderMarkService(Path.Combine(Path.GetTempPath(), "never-written-" + Guid.NewGuid().ToString("N") + ".json"));
        var announced = 0;
        marks.MarkChanged += (_, _) => announced++;
        var files = Enumerable.Range(0, 10_000).Select(index => $@"C:\Big\file-{index:D5}.bin").ToArray();

        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            marks.SetAccent(file, "#EF5A68");
        }

        watch.Stop();
        Report("colouring 10,000 selected files", watch.ElapsedMilliseconds, 500);
        Check("each file is announced, and their folder is known to hold marks",
            announced == files.Length && marks.MarkedFolders.Count == 1 && marks.MarkedFolders.Contains(@"C:\Big"));

        watch.Restart();
        foreach (var file in files)
        {
            marks.SetAccent(file, null);
        }

        watch.Stop();
        Report("clearing their colour again", watch.ElapsedMilliseconds, 500);
        Check("cleared, no folder holds marks", marks.MarkedFolders.Count == 0 && marks.Snapshot().Count == 0);
    }
}
