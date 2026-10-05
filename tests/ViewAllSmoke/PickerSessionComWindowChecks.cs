using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;
using UltraExplorer.Picker.Integration;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The picker's window and the COM dialog in front of it, on the test
/// monitor and never activated: what a click on the tree names, what Escape
/// closes before it cancels, what OK does with a server that does not
/// answer, and how the COM object treats its caller's window, its events, a
/// Close made early and the window it showed.
///
/// <para>A window needs the application's theme, and a process can only
/// ever have the one application.  When an earlier group made it already
/// (the Settings checks do), on a thread that has ended since, these checks
/// run in a process of their own.</para>
/// </summary>
internal static partial class Program
{
    private const int EAbort = unchecked((int)0x80004004);

    private static Task PickerSessionComWindowChecks()
    {
        if (Application.Current is not null)
        {
            RunGroupInOwnProcess(nameof(PickerSessionComWindowChecks));
            return Task.CompletedTask;
        }

        RunOnSta("picker session and COM windows", PickerSessionComWindowsOnStaAsync);
        return Task.CompletedTask;
    }

    private static void RunGroupInOwnProcess(string group)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, $"--only {group}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var child = Process.Start(start)!;
        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line) Console.WriteLine($"  | {line}");
        };
        child.BeginOutputReadLine();
        var ended = child.WaitForExit(TimeSpan.FromMinutes(5));
        if (!ended) child.Kill(entireProcessTree: true);
        child.WaitForExit();
        Check($"{group} passed in a process of its own", ended && child.ExitCode == 0);
    }

    private static async Task PickerSessionComWindowsOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The tree under these windows is re-read whenever a folder above the
        // fixture changes, which other processes' work in the same temporary
        // folder does all the time.  What that throws on the interface thread
        // is written down and survived, as the installed app's crash reporter
        // does, rather than ending these checks half way.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerSessionCom", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        try
        {
            // Every window made from here on refuses activation: the pickers,
            // the COM dialog's windows and the caller's, and any message box a
            // failing check would leave up.
            using (ActivationGuard.GuardWindowsCreated())
            {
                await TreePickerWindowChecksAsync(root);
                await TreePickerFilterEscapeChecksAsync(root);
                await ProxyPickerProbeChecksAsync(root);
                await ComDialogWindowChecksAsync(root);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the tree-layout picker (--pick, COM) --------------------------------------------

    private static async Task TreePickerWindowChecksAsync(string root)
    {
        Section("tree picker: what a click names, and what Escape closes first");
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        var alpha = Path.Combine(root, "alpha");
        var projects = Path.Combine(root, "Projects.lnk");
        var linked = TryMakeShortcut(projects, alpha);

        // NoValidate: a shortcut taken for a file is answered, never refused
        // with a message box that would wait for a click.
        var request = new FileDialogRequest
        {
            InitialFolder = root, Options = FileDialogOptions.NoValidate | FileDialogOptions.PathMustExist
        };
        request.Filters.Add(new FileDialogFilterSpec("All Files", "*.*"));
        request.Filters.Add(new FileDialogFilterSpec("Text", "*.txt"));
        var session = new FileDialogSession(request);
        MainWindow? picker = null;
        try
        {
            picker = new MainWindow(session) { Width = 1100, Height = 760 };
            picker.Show();
            var shell = (MainViewModel)picker.DataContext;
            Check("the tree picker opened on the test monitor",
                TestScreen.Target() is null || TestScreen.OnSecondary(new WindowInteropHelper(picker).Handle));
            // The folder is read on the way to it and read again when it is
            // opened; the window is ready when it has put the name box first.
            var loaded = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(picker), picker.PickerNameBox)
                && shell.Tree.TryGetNode(a, out _) && shell.Tree.TryGetNode(b, out _), 15_000);
            Check("the tree picker read the folder it was asked for", loaded);
            if (!loaded) return;

            // What Nodify does on a click: the clicked node goes into its
            // selection, and on a second click the new node goes in before
            // the old one comes out.  A folder above re-read meanwhile makes
            // new nodes for everything below it and drops the old ones from
            // the selection, so clicks that met that are made again.
            string first = string.Empty, second = string.Empty;
            IReadOnlyList<string> pending = [];
            FileDialogAction? answer = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                ViewAllNodeViewModel? nodeA = null, nodeB = null;
                if (!await Until(() => shell.Tree.TryGetNode(a, out nodeA!) && shell.Tree.TryGetNode(b, out nodeB!), 5_000)
                    || nodeA is null || nodeB is null)
                {
                    break;
                }

                shell.Tree.Selection.Apply(new SelectionEdit { Clear = true, Source = SelectionSource.Navigation });
                await PickerIdle();
                shell.Tree.SelectedNodes.Add(nodeA);
                await PickerIdle();
                first = session.FileNameText;
                shell.Tree.SelectedNodes.Add(nodeB);
                shell.Tree.SelectedNodes.Remove(nodeA);
                await PickerIdle();
                second = session.FileNameText;
                pending = session.PendingSelection;
                answer = session.Prepare(picker.PickerSelection);
                if (shell.Tree.TryGetNode(a, out var afterA) && ReferenceEquals(afterA, nodeA)
                    && shell.Tree.TryGetNode(b, out var afterB) && ReferenceEquals(afterB, nodeB))
                {
                    break;
                }
            }

            Check($"a click on a file in the tree names it in the dialog ({first})", first == "a.txt");
            Check($"a click on another file names that one, not the one clicked before ({second})",
                second == "b.txt" && pending.SequenceEqual([b]));
            Check("and OK answers with the file now highlighted",
                answer is { Kind: FileDialogActionKind.Accept } accept && accept.Paths.SequenceEqual([b]));

            // Escape closes what is open before it cancels the caller's dialog.
            picker.FolderListFilterBox.Text = "zz";
            PressPickerKey(picker.FolderListFilterBox, Key.Escape);
            Check("Escape in the list's filter clears the filter and leaves the dialog open",
                picker.FolderListFilterBox.Text.Length == 0 && !picker.PickerResult.IsCompleted);

            if (await PressWhileOpen(picker.PickerTypeBox, Key.Escape,
                () => picker.PickerTypeBox.IsDropDownOpen = true, () => picker.PickerTypeBox.IsDropDownOpen))
            {
                Check("Escape in the open file-type list closes the list and leaves the dialog open",
                    !picker.PickerTypeBox.IsDropDownOpen && !picker.PickerResult.IsCompleted);
            }

            session.RememberName("earlier.txt");
            if (await PressWhileOpen(picker.PickerNameBox, Key.Escape,
                () => picker.PickerNameBox.IsDropDownOpen = true, () => picker.PickerNameBox.IsDropDownOpen))
            {
                Check("Escape in the name box's open list of names closes the list and leaves the dialog open",
                    !picker.PickerNameBox.IsDropDownOpen && !picker.PickerResult.IsCompleted);
            }

            // The crumbs are made again whenever the address changes, so the
            // one opened is whichever is first at the time.
            void OpenCrumb()
            {
                if (shell.Address.Breadcrumbs.FirstOrDefault() is { } crumb) crumb.IsMenuOpen = true;
            }

            bool CrumbOpen() => shell.Address.Breadcrumbs.Any(crumb => crumb.IsMenuOpen);
            if (await PressWhileOpen(picker, Key.Escape, OpenCrumb, CrumbOpen))
            {
                Check("Escape with a crumb's list of folders open closes the list and leaves the dialog open",
                    !CrumbOpen() && !picker.PickerResult.IsCompleted);
            }

            if (await PressWhileOpen(picker.PickerNameBox, Key.Escape, OpenCrumb, CrumbOpen))
            {
                Check("and so does Escape in the name box", !CrumbOpen() && !picker.PickerResult.IsCompleted);
            }

            // Read at once: a list lets go as soon as another window takes the mouse.
            PressPickerKey(picker.PickerNameBox, Key.F4);
            Check("F4 in the name box opens its list of names, not the address bar's",
                picker.PickerNameBox.IsDropDownOpen && !shell.Address.IsEditing && !picker.PickerResult.IsCompleted);
            picker.PickerNameBox.IsDropDownOpen = false;
            await PickerIdle();

            // A shortcut to a folder opens the folder when it is opened. It is
            // brought onto the canvas by name: a folder above it re-read meanwhile
            // can leave the folder holding only what was selected in it.
            var link = linked ? await shell.Tree.RevealPathAsync(projects, focus: false, select: false) : null;
            if (link is not null)
            {
                var answered = new List<string>();
                session.AcceptGuard = paths => { answered.AddRange(paths); return false; };
                shell.Tree.ActivationOverride?.Invoke(link);
                var opened = await Until(() => ViewAllPath.Equals(session.CurrentFolder, alpha), 5_000);
                await PickerIdle();
                Check("double-clicking a shortcut to a folder opens the folder instead of answering with it",
                    opened && answered.Count == 0 && !picker.PickerResult.IsCompleted);
                session.AcceptGuard = null;
            }
            else
            {
                Check($"the shortcut for the check is on the canvas (made: {linked})", false);
            }

            PressPickerKey(picker, Key.Escape);
            Check("Escape with nothing open still cancels the dialog",
                picker.PickerResult.IsCompleted && !picker.PickerResult.Result.Accepted);
        }
        finally
        {
            picker?.CloseFromCaller();
        }
    }

    /// <summary>
    /// The list's filter keeps the keyboard once Escape has cleared it, so
    /// the next Escape there has to reach the dialog.  On a picker of its
    /// own, since that Escape cancels it.
    /// </summary>
    private static async Task TreePickerFilterEscapeChecksAsync(string root)
    {
        Section("tree picker: Escape in the list's filter, twice");
        var request = new FileDialogRequest { InitialFolder = root };
        request.Filters.Add(new FileDialogFilterSpec("All Files", "*.*"));
        var session = new FileDialogSession(request);
        MainWindow? picker = null;
        try
        {
            picker = new MainWindow(session) { Width = 1100, Height = 760 };
            picker.Show();
            var loaded = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(picker), picker.PickerNameBox), 15_000);
            Check("the second tree picker is ready for keys", loaded);
            if (!loaded) return;

            picker.FolderListFilterBox.Text = "zz";
            PressPickerKey(picker.FolderListFilterBox, Key.Escape);
            Check("the first Escape in the list's filter clears it and leaves the dialog open",
                picker.FolderListFilterBox.Text.Length == 0 && !picker.PickerResult.IsCompleted);
            PressPickerKey(picker.FolderListFilterBox, Key.Escape);
            Check("the second, with nothing left to clear, cancels the dialog",
                picker.PickerResult.IsCompleted && !picker.PickerResult.Result.Accepted);
        }
        finally
        {
            picker?.CloseFromCaller();
        }
    }

    /// <summary>A key as the keyboard delivers it: down the tree first, then back up, with what was handled carried over.</summary>
    private static void PressPickerKey(UIElement target, Key key)
    {
        // A window the key has closed already takes no more keys.
        if (PresentationSource.FromVisual(target) is not { } source) return;
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(press);
        press.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(press);
    }

    /// <summary>
    /// Opens a list or a popup and presses <paramref name="key"/> while it is
    /// open.  Either lets go as soon as another window takes the mouse, which
    /// the user's own clicks elsewhere do, so it is opened again a few times;
    /// false, and a line saying so, when it never stayed open to be pressed on.
    /// </summary>
    private static async Task<bool> PressWhileOpen(UIElement target, Key key, Action open, Func<bool> isOpen)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            open();
            await PickerIdle();
            if (isOpen())
            {
                PressPickerKey(target, key);
                return true;
            }
        }

        Console.WriteLine($"  (skipped {key} on {target.GetType().Name}: it would not stay open)");
        return false;
    }

    private static async Task PickerIdle() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    // ---- a replaced dialog and a server that does not answer -------------------------

    /// <summary>
    /// A name on a server that does not answer: Windows takes some 20 s to
    /// say so, and a replaced dialog's interface thread has to keep its
    /// heartbeat meanwhile.  Its OK lets the application's own dialog, which
    /// checks every answer again, decide.  The servers are in the ranges set
    /// aside for documentation, which nothing answers, and new on every run,
    /// since Windows remembers one that did not answer.
    /// </summary>
    private static async Task ProxyPickerProbeChecksAsync(string root)
    {
        Section("replaced dialog: OK with a name on a server that does not answer");
        var typed = $@"\\192.0.2.{Random.Shared.Next(1, 255)}\share\notes.txt";
        var proxy = new FileDialogRequest
        {
            IsNativeProxy = true, Mode = FileDialogMode.Save, InitialFolder = root,
            Options = FileDialogOptions.NoValidate | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
        };
        proxy.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
        var (held, result) = await AcceptOnProxyAsync(proxy, picker => picker.PickerNameBox.Text = typed,
            picker => picker.PickerAcceptButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Check($"OK keeps the interface thread free while the name is looked at (it was held {held.TotalMilliseconds:F0} ms)",
            held < TimeSpan.FromMilliseconds(500));
        Check("and the name goes to the application's dialog within a few seconds",
            result is { Accepted: true } typedResult && typedResult.Paths.SequenceEqual([typed]));
        if (held >= TimeSpan.FromMilliseconds(500))
        {
            // The next check would end in a message box nobody is there to answer.
            return;
        }

        // A file opened from the canvas is checked before it goes back. Only
        // where such a server is slow to fail can it be told apart.
        var probe = $@"\\198.51.100.{Random.Shared.Next(1, 255)}\share";
        var slow = Task.Run(() => Directory.Exists(probe));
        if (await Task.WhenAny(slow, Task.Delay(1500)) == slow)
        {
            Console.WriteLine("  (skipped: a server that does not exist fails at once here)");
            return;
        }

        var opened = $@"\\203.0.113.{Random.Shared.Next(1, 255)}\share\photo.png";
        var open = new FileDialogRequest
        {
            IsNativeProxy = true, Mode = FileDialogMode.Open, InitialFolder = root,
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
        };
        open.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
        (held, result) = await AcceptOnProxyAsync(open, _ => { }, picker => picker.OpenPickerNestedFile(opened));
        Check($"opening a file there keeps the interface thread free too (it was held {held.TotalMilliseconds:F0} ms)",
            held < TimeSpan.FromMilliseconds(500));
        Check("and hands it to the application's dialog to check",
            result is { Accepted: true } openedResult && openedResult.Paths.SequenceEqual([opened]));
    }

    private static async Task<(TimeSpan Held, FileDialogResult? Result)> AcceptOnProxyAsync(
        FileDialogRequest request, Action<MainWindow> prepare, Action<MainWindow> accept)
    {
        var picker = new MainWindow(new FileDialogSession(request)) { Width = 1100, Height = 760 };
        try
        {
            picker.PrepareAsCloakedPicker(_ => { });
            picker.Show();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(8));
            prepare(picker);
            var clock = Stopwatch.StartNew();
            accept(picker);
            var held = clock.Elapsed;
            var answered = await Task.WhenAny(picker.PickerResult, Task.Delay(TimeSpan.FromSeconds(6))) == picker.PickerResult;
            return (held, answered ? picker.PickerResult.Result : null);
        }
        finally
        {
            picker.CloseFromCaller();
        }
    }

    // ---- the COM dialog ---------------------------------------------------------------------

    private static async Task ComDialogWindowChecksAsync(string root)
    {
        Section("COM dialog: the caller's window, its events, an early Close and the window it showed");
        var folder = ShellNative.ItemFor(root);
        if (folder is null)
        {
            Check("the fixture folder has a shell item", false);
            return;
        }

        await ComEarlyCloseChecksAsync(folder);
        await ComOwnerChecksAsync(folder);
        await ComEventChecksAsync(folder, root);
    }

    private static readonly FieldInfo ComWindowField =
        typeof(FileDialogComBase).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ComSessionField =
        typeof(FileDialogComBase).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ComRunningField =
        typeof(FileDialogComBase).GetField("_dialogRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Show, from a thread of its own, as a caller's call arrives.</summary>
    private static Task<int> ShowComDialog(FileDialogComBase dialog, nint owner, Action<int>? onThread = null) =>
        Task.Factory.StartNew(() =>
        {
            onThread?.Invoke(Environment.CurrentManagedThreadId);
            return dialog.Show(owner);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task<MainWindow?> ComWindowAsync(FileDialogComBase dialog)
    {
        MainWindow? window = null;
        await Until(() => (window = ComWindowField.GetValue(dialog) as MainWindow) is { IsLoaded: true }, 10_000);
        if (window is not null && TestScreen.Target() is not null && !TestScreen.OnSecondary(new WindowInteropHelper(window).Handle))
        {
            Check("the COM dialog's window opened on the test monitor", false);
        }

        return window;
    }

    private static async Task<bool> EndComShowAsync(FileDialogComBase dialog, Task<int> shown)
    {
        if (await Task.WhenAny(shown, Task.Delay(TimeSpan.FromSeconds(8))) == shown) return true;
        (ComWindowField.GetValue(dialog) as MainWindow)?.CloseFromCaller();
        await Task.WhenAny(shown, Task.Delay(TimeSpan.FromSeconds(8)));
        return false;
    }

    /// <summary>
    /// A caller may close its dialog before the window is on screen: from
    /// another thread, or from an event the window raised while it was being
    /// built.  That Close must not be lost.
    /// </summary>
    private static async Task ComEarlyCloseChecksAsync(IShellItem folder)
    {
        var dialog = new UltraFileOpenDialog();
        dialog.SetFolder(folder);
        var shown = ShowComDialog(dialog, 0);

        // Show has handed the window to this thread, which has not built it yet.
        var waiting = Stopwatch.StartNew();
        while (!(bool)ComRunningField.GetValue(dialog)! && waiting.ElapsedMilliseconds < 5_000) Thread.Sleep(1);
        dialog.Close(EAbort);
        var returned = await EndComShowAsync(dialog, shown);
        Check("a Close made while the window is still being built closes it, and Show returns what Close was given",
            returned && shown.Result == EAbort);
    }

    private static async Task ComOwnerChecksAsync(IShellItem folder)
    {
        if (TestScreen.Target() is not { } screen)
        {
            Console.WriteLine("  (skipped: the owner checks need a second monitor)");
            return;
        }

        // The caller's window: never shown, on the test monitor, so the
        // dialog centred on it opens there too.
        using var owner = new HwndSource(new HwndSourceParameters("picker owner fixture")
        {
            WindowStyle = unchecked((int)0x80000000),
            PositionX = screen.Work.Left + 40,
            PositionY = screen.Work.Top + 40,
            Width = 900,
            Height = 700
        });
        var dialog = new UltraFileOpenDialog();
        dialog.SetFolder(folder);
        var shown = ShowComDialog(dialog, owner.Handle);
        var window = await ComWindowAsync(dialog);
        Check("the COM dialog came up for the owner", window is not null);
        if (window is null)
        {
            await EndComShowAsync(dialog, shown);
            return;
        }

        Check("the caller's window is left enabled while the dialog is up, so nothing has to come back if this process dies",
            ShellNative.IsWindowEnabled(owner.Handle));
        bool? enabledWhenGone = null;
        window.Closed += (_, _) => enabledWhenGone = ShellNative.IsWindowEnabled(owner.Handle);
        window.CloseFromCaller();
        var returned = await EndComShowAsync(dialog, shown);
        Check("and it is enabled as the dialog goes, so Windows gives the activation back to it",
            returned && enabledWhenGone == true && ShellNative.IsWindowEnabled(owner.Handle));
    }

    private static async Task ComEventChecksAsync(IShellItem folder, string root)
    {
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        var sink = new PickerEventSink { SelectionDelay = TimeSpan.FromMilliseconds(250) };
        var dialog = new UltraFileOpenDialog();
        dialog.SetFolder(folder);
        dialog.Advise(sink, out var cookie);
        var showThread = 0;
        var shown = ShowComDialog(dialog, 0, id => showThread = id);
        var window = await ComWindowAsync(dialog);
        Check("the COM dialog came up for its events", window is not null);
        if (window is null || ComSessionField.GetValue(dialog) is not FileDialogSession session)
        {
            await EndComShowAsync(dialog, shown);
            return;
        }

        // The caller's event takes 250 ms each time.  The interface thread
        // does other work of its own meanwhile (the tree re-reads a folder
        // when one above it changes), so the quickest of three is what counts.
        await PickerIdle();
        var one = TimeSpan.MaxValue;
        var clock = new Stopwatch();
        for (var index = 0; index < 3; index++)
        {
            await Until(() => sink.Idle, 5_000);
            clock.Restart();
            session.ReportSelection([new SelectionItem(index % 2 == 0 ? a : b, false, 1)]);
            one = clock.Elapsed < one ? clock.Elapsed : one;
        }

        Check($"a selection change does not wait for the caller's event to run (it waited {one.TotalMilliseconds:F0} ms)",
            one < TimeSpan.FromMilliseconds(150));

        await Until(() => sink.Idle, 5_000);
        var before = sink.SelectionChanges;
        clock.Restart();
        for (var index = 0; index < 40; index++)
        {
            session.ReportSelection([new SelectionItem(index % 2 == 0 ? b : a, false, 1)]);
        }

        var storm = clock.Elapsed;
        Check($"forty changes in a row do not wait for forty of the caller's events ({storm.TotalMilliseconds:F0} ms)",
            storm < TimeSpan.FromSeconds(3));
        var heard = await Until(() => sink.SelectionChanges > before && sink.Idle, 10_000);
        await Task.Delay(400);
        var calls = sink.SelectionChanges - before;
        Check($"the caller hears of them, a few times for many ({calls})", heard && calls is > 0 and < 20);
        Check("on the thread its Show call came in on", sink.Threads(sink.SelectionThreads).All(id => id == showThread));

        sink.FileOk = Hresult.False;
        var refused = session.AcceptGuard?.Invoke([a]);
        sink.FileOk = Hresult.Ok;
        var accepted = session.AcceptGuard?.Invoke([a]);
        Check("OnFileOk is still waited for: S_FALSE keeps the dialog open, S_OK lets it close",
            refused == false && accepted == true && sink.Threads(sink.FileOkThreads).All(id => id == showThread));

        window.CloseFromCaller();
        var returned = await EndComShowAsync(dialog, shown);
        dialog.Unadvise(cookie);
        var filterChanged = typeof(FileDialogSession)
            .GetField(nameof(FileDialogSession.FilterChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session) as Delegate;
        Check("once the dialog is closed its session no longer holds on to the window",
            returned && (filterChanged?.GetInvocationList().All(handler => handler.Target is not MainWindow) ?? true));
        Check("and the caller can still read back the file type it ended on",
            dialog.GetFileTypeIndex(out var type) == Hresult.Ok && type == 1);
    }

    /// <summary>A caller's event sink, as a program writes one; it counts what it hears and where.</summary>
    private sealed class PickerEventSink : IFileDialogEvents
    {
        private int _running;

        public TimeSpan SelectionDelay { get; init; }

        public int FileOk { get; set; } = Hresult.Ok;

        public int SelectionChanges;

        public List<int> SelectionThreads { get; } = [];

        public List<int> FileOkThreads { get; } = [];

        /// <summary>A copy of one of the lists, taken while the caller's thread cannot add to it.</summary>
        public int[] Threads(List<int> list)
        {
            lock (list) return [.. list];
        }

        public bool Idle => Volatile.Read(ref _running) == 0;

        public int OnFileOk(IFileDialog dialog)
        {
            lock (FileOkThreads) FileOkThreads.Add(Environment.CurrentManagedThreadId);
            return FileOk;
        }

        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => Hresult.Ok;

        public int OnFolderChange(IFileDialog dialog) => Hresult.Ok;

        public int OnSelectionChange(IFileDialog dialog)
        {
            Interlocked.Increment(ref _running);
            try
            {
                lock (SelectionThreads) SelectionThreads.Add(Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref SelectionChanges);
                Thread.Sleep(SelectionDelay);
                return Hresult.Ok;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public int OnShareViolation(IFileDialog dialog, IShellItem item, out ShareViolationResponse response)
        {
            response = ShareViolationResponse.Default;
            return Hresult.Ok;
        }

        public int OnTypeChange(IFileDialog dialog) => Hresult.Ok;

        public int OnOverwrite(IFileDialog dialog, IShellItem item, out OverwriteResponse response)
        {
            response = OverwriteResponse.Default;
            return Hresult.Ok;
        }
    }
}
