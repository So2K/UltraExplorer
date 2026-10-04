using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the picker window of a replaced dialog,
/// and what was done about it, a section each: thousands of files chosen
/// in a multi-select dialog (J031); a path typed into a Select Folder and
/// the view then moved (J146); a prepared picker said to be drawn while its
/// new folder was still unread (J024); and a prepared picker put over a
/// dialog on a monitor shorter than its minimum (J157).
///
/// <para>The windows need the app, of which a process can only ever have the
/// one, on the thread that made it: these checks always run in a process of
/// their own. Their windows open on a monitor that is not the primary one
/// and are never activated; the dialog a picker is put over here is a
/// stand-in of this process's own, cloaked, there too. Without a second
/// monitor the windows are not opened.</para>
/// </summary>
internal static partial class Program
{
    private static Task PickerDialogsWindowReviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PickerDialogsWindowReviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(PickerDialogsWindowReviewChecks));
            return Task.CompletedTask;
        }

        RunOnSta("picker dialogs window review", PickerDialogsWindowReviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PickerDialogsWindowReviewOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the picker window review checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        if (TestScreen.Target() is null)
        {
            Console.WriteLine("  (no secondary monitor: the picker windows are not opened)");
            return;
        }

        // What the windows' own reading throws on the interface thread,
        // outside these checks, is written down and survived.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerWindowReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await PickerFooterChoiceChecksAsync(root);
            await PickerTypedFolderChecksAsync(root);
            await PickerDrawnFolderChecksAsync(root);
            await PickerMinimumSizeChecksAsync(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>A picker made as a dialog worker prepares one - shown, cloaked, never activated - and its folder drawn.</summary>
    private static async Task<MainWindow> ShowReviewPickerAsync(FileDialogSession session)
    {
        var picker = new MainWindow(session) { Width = 1280, Height = 860, WindowState = WindowState.Normal };
        picker.PrepareAsCloakedPicker(_ => { });
        using (ActivationGuard.GuardWindowsCreated())
        {
            picker.Show();
        }

        await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(8));
        return picker;
    }

    private static FileDialogRequest ReviewRequest(string folder, FileDialogMode mode, FileDialogOptions options = FileDialogOptions.None)
    {
        var request = new FileDialogRequest { IsNativeProxy = true, Mode = mode, InitialFolder = folder, Options = options };
        request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
        return request;
    }

    // ---- J031: thousands of files chosen in a multi-select dialog -----------------------

    /// <summary>
    /// Ctrl+A over thousands of photos in an upload dialog, then one more
    /// Ctrl+click: the footer says how many are chosen and lists the first of
    /// them, not every one - each change used to lay out every chosen path in
    /// a wrapping text, several hundred milliseconds at five thousand.
    /// </summary>
    private static async Task PickerFooterChoiceChecksAsync(string root)
    {
        Section("picker review: thousands of files chosen in a replaced multi-select dialog (J031)");
        var folder = Path.Combine(root, "photos");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "IMG_00000.jpg"), "photo");
        var session = new FileDialogSession(ReviewRequest(folder, FileDialogMode.Open, FileDialogOptions.AllowMultiSelect));
        var picker = await ShowReviewPickerAsync(session);
        try
        {
            await picker.RebindAsync(session = new FileDialogSession(ReviewRequest(folder, FileDialogMode.Open, FileDialogOptions.AllowMultiSelect)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            Check("the dialog lets several files be chosen", session.AllowsMultipleSelection);
            foreach (var count in new[] { 10, 1000, 5000 })
            {
                var chosen = Enumerable.Range(0, count)
                    .Select(index => new SelectionItem(Path.Combine(folder, $"IMG_{index:D5} holiday in the mountains.jpg"), false, 1)).ToArray();
                picker.UpdateLayout();
                var clock = Stopwatch.StartNew();
                session.ReportSelection(chosen);
                picker.UpdateLayout();
                var all = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                session.ReportSelection(chosen[..^1]);
                picker.UpdateLayout();
                var one = clock.Elapsed.TotalMilliseconds;
                session.ReportSelection(chosen);
                picker.UpdateLayout();
                var lines = picker.PickerPendingLocation.Text.Split(Environment.NewLine);
                Console.WriteLine($"  {count} chosen: {all:F1} ms to show, {one:F1} ms for one more Ctrl+click; the footer lists {lines.Length} line(s)");
                Check($"with {count} chosen the footer counts them ({picker.PickerPendingName.Text})",
                    picker.PickerPendingName.Text == $"Selected files ({count})");
                Check($"and lists no more than the first few of them ({lines.Length} lines)", lines.Length <= 11);
                Check("the first of them as they are", lines[0] == chosen[0].Path && (count <= 10 || lines[9] == chosen[9].Path));
                Check(count <= 10 ? "all of them, when they are few" : $"and says how many more there are ({lines[^1]})",
                    count <= 10 ? lines.SequenceEqual(chosen.Select(item => item.Path)) : lines[^1] == $"and {count - 10:N0} more");
                Check("the tip over them still names every one",
                    picker.PickerPendingChoice.ToolTip is string tip && tip.Split(Environment.NewLine).Length == count);
            }
        }
        finally
        {
            picker.CloseFromCaller();
        }
    }

    // ---- J146: a path typed into a Select Folder, then the view moved -------------------

    /// <summary>
    /// A replaced Select Folder opened at Documents: the user types a folder
    /// of their own into the name box, then pans or zooms into Invoices to
    /// look. The typed folder is still the answer; the view moved by hand
    /// is where a name typed without a folder would go. Moving the view
    /// with nothing typed answers the folder in view, as before.
    /// </summary>
    private static async Task PickerTypedFolderChecksAsync(string root)
    {
        Section("picker review: a path typed into a replaced Select Folder, then the view moved (J146)");
        var documents = Path.Combine(root, "Documents");
        var invoices = Path.Combine(documents, "Invoices");
        var backups = Path.Combine(root, "Backups");
        foreach (var name in new[] { "Invoices", "Letters", "Photos", "Taxes", "Recipes", "Travel" })
        {
            Directory.CreateDirectory(Path.Combine(documents, name));
        }

        Directory.CreateDirectory(backups);
        var session = new FileDialogSession(ReviewRequest(documents, FileDialogMode.PickFolder));
        var picker = await ShowReviewPickerAsync(session);
        try
        {
            await picker.RebindAsync(session = new FileDialogSession(ReviewRequest(documents, FileDialogMode.PickFolder)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            picker.PickerNameBox.Text = backups;
            Check("a folder of the user's own is typed into the name box", session.FileNameText == backups);
            var framed = await ZoomIntoByHandAsync(picker, invoices, keys: false);
            var followed = await LiveWait(() => ViewAllPath.Equals(session.CurrentFolder, invoices), 3_000) >= 0;
            Check("the user then zooms into Invoices by hand, and the dialog is there", framed && followed);
            Check($"what was typed is still in the name box ({session.FileNameText})", session.FileNameText == backups);
            var chosen = session.Prepare(picker.PickerSelection);
            Check($"and Select Folder answers the typed folder ({string.Join(" | ", chosen.Paths)})",
                chosen.Kind == FileDialogActionKind.Accept && chosen.Paths.Count == 1 && ViewAllPath.Equals(chosen.Paths[0], backups));

            // Nothing typed: the folder in view is the answer, as before.
            await picker.RebindAsync(session = new FileDialogSession(ReviewRequest(documents, FileDialogMode.PickFolder)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            framed = await ZoomIntoByHandAsync(picker, invoices, keys: true);
            followed = await LiveWait(() => ViewAllPath.Equals(session.CurrentFolder, invoices), 3_000) >= 0;
            chosen = session.Prepare(picker.PickerSelection);
            Check($"with nothing typed, zoomed into Invoices, Select Folder answers Invoices ({string.Join(" | ", chosen.Paths.Select(Path.GetFileName))})",
                framed && followed && chosen.Kind == FileDialogActionKind.Accept && chosen.Paths.Count == 1 && ViewAllPath.Equals(chosen.Paths[0], invoices));
        }
        finally
        {
            picker.CloseFromCaller();
        }
    }

    // ---- J024: a prepared picker bound to another dialog's folder -----------------------

    /// <summary>
    /// A prepared picker bound to the next dialog, and asked at once, as the
    /// replacement asks, when that dialog's folder is drawn: only once that
    /// folder has been read - never because the folder the previous dialog
    /// showed was drawn long ago. A folder of thousands of files each time,
    /// one the picker has never read.
    /// </summary>
    private static async Task PickerDrawnFolderChecksAsync(string root)
    {
        Section("picker review: a prepared picker bound to the next dialog is drawn with that dialog's folder (J024)");
        var first = Path.Combine(root, "first");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "note.txt"), "note");
        const int Rounds = 6;
        var folders = Enumerable.Range(0, Rounds).Select(round => Path.Combine(root, $"bulk-{round}")).ToArray();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
            for (var index = 0; index < 4000; index++)
            {
                File.Create(Path.Combine(folder, $"file-{index:D4}.txt")).Dispose();
            }
        }

        // The user's saved workspace, which binding reads for the sidebar's
        // places before it shows the new dialog: with one there, as in real
        // use, that read takes a turn of its own.
        var workspace = AppPaths.State("workspace.json");
        var savedWorkspace = File.Exists(workspace) ? File.ReadAllText(workspace) : null;
        File.WriteAllText(workspace, "{}");
        var picker = await ShowReviewPickerAsync(new FileDialogSession(ReviewRequest(first, FileDialogMode.Open)));
        try
        {
            await picker.RebindAsync(new FileDialogSession(ReviewRequest(first, FileDialogMode.Open)));
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            var early = 0;
            // What the drawn-wait asked at once after a bind is told about:
            // the folder it waits to see read, the new dialog's or the old one.
            var waitsOnOld = 0;
            var times = new List<string>();
            foreach (var folder in folders)
            {
                var clock = Stopwatch.StartNew();
                var bind = picker.RebindAsync(new FileDialogSession(ReviewRequest(folder, FileDialogMode.Open)));
                if (!string.Equals(typeof(MainWindow).GetField("_pickerStartFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker) as string, folder, StringComparison.Ordinal)) waitsOnOld++;
                var drawn = await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(8));
                var read = picker.ActivePane.Tree.Find(folder) is { IsLoaded: true };
                times.Add($"{clock.ElapsedMilliseconds} ms{(read ? "" : " UNREAD")}");
                if (drawn && !read)
                {
                    early++;
                }

                await bind;
                await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(8));
            }

            Console.WriteLine($"  drawn after: {string.Join(", ", times)}");
            Check($"asked at once after a bind, the picker waits for the new dialog's folder, not the last one's ({waitsOnOld} of {Rounds} waited on the last)",
                waitsOnOld == 0);
            Check($"the picker is drawn only once the new dialog's folder is read ({early} of {Rounds} said drawn before it was)", early == 0);
        }
        finally
        {
            picker.CloseFromCaller();
            if (savedWorkspace is null) TryDeleteFile(workspace);
            else File.WriteAllText(workspace, savedWorkspace);
        }
    }

    // ---- J157: a prepared picker over a dialog on a shorter monitor ---------------------

    /// <summary>
    /// A prepared picker fitted, as it was made, to a taller monitor, then put
    /// over a dialog on one that cannot hold its minimum height - a 1080p
    /// panel at 175 %, where 620 pixels of the window's own are 1085 of the
    /// screen's. Simulated on the secondary monitor with a minimum taller than
    /// its work area: the picker must still lie inside it, its OK and Cancel
    /// above the taskbar.
    /// </summary>
    private static async Task PickerMinimumSizeChecksAsync(string root)
    {
        Section("picker review: a prepared picker over a dialog on a monitor shorter than its minimum (J157)");
        var target = TestScreen.Target()!.Value;
        var folder = Path.Combine(root, "minimum");
        Directory.CreateDirectory(folder);
        using var standIn = new CloakedStandIn(target.Work, "UltraExplorer picker review: a dialog on a short monitor");
        Check("a stand-in dialog waits, cloaked, on the secondary monitor", standIn.Handle != 0);
        if (standIn.Handle == 0) return;
        var picker = await ShowReviewPickerAsync(new FileDialogSession(ReviewRequest(folder, FileDialogMode.Open)));
        try
        {
            var handle = new WindowInteropHelper(picker).Handle;
            var scale = DialogNative.MonitorOf(standIn.Handle)?.Scale ?? 1;
            picker.MinHeight = Math.Ceiling(target.Work.Height / scale) + 120;
            picker.MinWidth = Math.Min(picker.MinWidth, Math.Floor(target.Work.Width / scale) - 40);
            var proxy = new NativeDialogProxy(Application.Current, standIn.Handle);
            typeof(NativeDialogProxy).GetMethod("PlaceOverOriginal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(proxy, [handle]);
            await Task.Delay(100);
            var placed = TestScreen.WindowRect(handle);
            Console.WriteLine($"  work area {target.Work}; the picker {placed}; its minimum height {picker.MinHeight:0}");
            Check($"the picker lies inside the work area of the dialog's monitor ({placed} in {target.Work})",
                placed.Top >= target.Work.Top && placed.Bottom <= target.Work.Bottom
                && placed.Left >= target.Work.Left && placed.Right <= target.Work.Right);
        }
        finally
        {
            picker.CloseFromCaller();
        }
    }
}
