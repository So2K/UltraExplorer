using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the 2026-10-02 review found in the main window itself, and what was
/// done about it.
/// A window maximized on a monitor larger than the primary one grew past its edges (I024).
/// A new window larger than the screen opened with its caption above the top of it (I027).
/// A rename the folder list was waiting to start renamed whatever was selected by then (I074).
/// The crumbs of a path longer than the bar never showed the folder the window was in (I081).
/// A press in a crumb's list of folders turned the bar into the line to type in (I159).
/// A window closed while it was starting saved the default sidebar width over the user's (I142).
///
/// <para>The windows need the app, of which a process can only ever have the
/// one, on the thread that made it: these checks always run in a process of
/// their own, so that the Settings checks after them still make theirs.  The
/// windows that are shown open on a monitor that is not the primary one,
/// cloaked and never active; without a second monitor they are not opened.</para>
/// </summary>
internal static partial class Program
{
    private static Task MainWindowReviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(MainWindowReviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(MainWindowReviewChecks));
            return Task.CompletedTask;
        }

        MainWindowMaximizeRuleChecks();
        RunOnSta("main window review", MainWindowReviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task MainWindowReviewOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the main window review checks require isolated state and test-window mode", isolated);
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

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerMainWindowReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Every window made from here on refuses activation.
            using (ActivationGuard.GuardWindowsCreated())
            {
                await MainWindowMaximizedChecksAsync();
                await MainWindowPlacementChecksAsync();
                await MainWindowListRenameChecksAsync(root);
                await MainWindowCrumbStripChecksAsync(root);
                await MainWindowCrumbMenuPressChecksAsync(root);
                MainWindowSidebarWidthChecks();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- shown windows, on the second monitor ------------------------------------------

    /// <summary>The first monitor that is not the primary one - where a test copy's window opens - or null.</summary>
    private static MainWindow.MonitorInfo? ReviewSecondMonitor()
    {
        MainWindow.MonitorInfo? found = null;
        ReviewEnumMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MainWindow.MonitorInfo { Size = Marshal.SizeOf<MainWindow.MonitorInfo>() };
            if (found is null && ReviewMonitorInfo(monitor, ref info) && (info.Flags & 1) == 0)
            {
                found = info;
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>
    /// A window of the app as a test copy opens it - on the monitor of
    /// <see cref="ReviewSecondMonitor"/>, never active - and cloaked, so none
    /// of it is ever on screen.  <paramref name="place"/> is given it once it
    /// exists, before it is first shown, after the test copy's own placement.
    /// </summary>
    private static MainWindow ShowReviewWindow(Action<MainWindow, nint>? place = null)
    {
        var window = new MainWindow { WindowState = WindowState.Normal };
        window.PrepareAsNativeProxy(handle =>
        {
            DialogNative.CloakOwn(handle, true);
            place?.Invoke(window, handle);
        });
        window.Show();
        return window;
    }

    private static NativeRect ReviewRect(MainWindow.RectL rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    // ---- I024: maximized on a monitor larger than the primary ---------------------------

    /// <summary>
    /// Windows takes a maximized size as meant for the primary monitor, and
    /// on another one grows a size at least as large as the primary monitor
    /// by the difference between the two (The Old New Thing, 2015-05-01,
    /// "How does the window manager adjust ptMaxSize and ptMaxPosition for
    /// multiple monitors?").  The window is then no larger than its tracking
    /// size.  Laid out by that rule, a window maximized on a 4K monitor beside
    /// a 1080p primary covers that monitor's work area exactly - not the work
    /// area plus the difference, with its caption buttons and status bar past
    /// the edges.  Where Windows takes the size as given, nothing else of
    /// what the window asks for changes.
    /// </summary>
    private static void MainWindowMaximizeRuleChecks()
    {
        Section("main window: maximized on a monitor larger than the primary (I024)");

        static MainWindow.RectL Rect(int left, int top, int right, int bottom) =>
            new() { Left = left, Top = top, Right = right, Bottom = bottom };

        static MainWindow.MonitorInfo Monitor(MainWindow.RectL monitor, MainWindow.RectL work, bool primary) =>
            new() { Size = Marshal.SizeOf<MainWindow.MonitorInfo>(), Monitor = monitor, Work = work, Flags = primary ? 1 : 0 };

        // Windows' default tracking size: the whole desktop and a frame.
        var defaultTrack = new MainWindow.PointL { X = 5784, Y = 2184 };

        (MainWindow.RectL Rect, MainWindow.MinMaxInfo Asked) Maximize(MainWindow.MonitorInfo target, MainWindow.MonitorInfo primary)
        {
            var asked = new MainWindow.MinMaxInfo { MaxTrackSize = defaultTrack };
            MainWindow.FitMaximizedBounds(ref asked, target, primary.Work);
            var width = asked.MaxSize.X;
            var height = asked.MaxSize.Y;
            var primaryWidth = primary.Monitor.Right - primary.Monitor.Left;
            var primaryHeight = primary.Monitor.Bottom - primary.Monitor.Top;
            if ((target.Flags & 1) == 0 && width >= primaryWidth && height >= primaryHeight)
            {
                width += target.Monitor.Right - target.Monitor.Left - primaryWidth;
                height += target.Monitor.Bottom - target.Monitor.Top - primaryHeight;
            }

            width = Math.Min(width, asked.MaxTrackSize.X);
            height = Math.Min(height, asked.MaxTrackSize.Y);
            var left = target.Monitor.Left + asked.MaxPosition.X;
            var top = target.Monitor.Top + asked.MaxPosition.Y;
            return (Rect(left, top, left + width, top + height), asked);
        }

        var fullHd = Monitor(Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1040), primary: true);
        var fourK = Monitor(Rect(1920, 0, 5760, 2160), Rect(1920, 0, 5760, 2112), primary: false);
        var (onFourK, _) = Maximize(fourK, fullHd);
        Check($"maximized on a 4K monitor beside a 1080p primary, the window covers that monitor's work area, not more ({onFourK.Left},{onFourK.Top} {onFourK.Right - onFourK.Left}x{onFourK.Bottom - onFourK.Top})",
            ReviewRect(onFourK) == ReviewRect(fourK.Work));
        var (onFullHd, _) = Maximize(fullHd, fullHd);
        Check("and on the 1080p primary, that one's", ReviewRect(onFullHd) == ReviewRect(fullHd.Work));

        // A secondary smaller than the primary, as on the machine these
        // checks were written on: Windows takes the size as given, and the
        // window asks for nothing more than it did.
        var large = Monitor(Rect(0, 0, 2560, 1440), Rect(0, 0, 2504, 1440), primary: true);
        var small = Monitor(Rect(-1920, 694, 0, 1774), Rect(-1920, 694, 0, 1734), primary: false);
        var (onSmall, smallAsked) = Maximize(small, large);
        Check("on a secondary smaller than the primary, the window covers its work area",
            ReviewRect(onSmall) == ReviewRect(small.Work));
        Check("and is left free to be resized as before",
            smallAsked.MaxTrackSize.X == defaultTrack.X && smallAsked.MaxTrackSize.Y == defaultTrack.Y);
        var (_, largeAsked) = Maximize(large, large);
        Check("as it is on the primary itself",
            largeAsked.MaxTrackSize.X == defaultTrack.X && largeAsked.MaxTrackSize.Y == defaultTrack.Y);
    }

    /// <summary>
    /// The rule above, as this Windows applies it: a window whose maximized
    /// size is larger than its monitor's work area - as Windows makes it on a
    /// monitor larger than the primary - is held to its tracking size.  And
    /// the app's window, maximized on the second monitor, covers its work
    /// area exactly.
    /// </summary>
    private static async Task MainWindowMaximizedChecksAsync()
    {
        Section("main window: maximized, on this Windows (I024)");
        if (ReviewSecondMonitor() is not { } second)
        {
            Check("no second monitor: no window is opened on the main one", true);
            return;
        }

        var oversized = ReviewMaximizeProbe(second, capTrack: false);
        var capped = ReviewMaximizeProbe(second, capTrack: true);
        Check($"a maximized size larger than the work area spills past it ({oversized})",
            oversized is { } spilled && !ReviewRect(second.Work).Contains(spilled));
        Check($"and the tracking size holds the window to the work area ({capped})", capped == ReviewRect(second.Work));

        var window = ShowReviewWindow();
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            window.WindowState = WindowState.Maximized;
            await SettingsSettle();
            var monitor = DialogNative.MonitorOf(handle);
            var bounds = DialogNative.WindowBounds(handle);
            Check($"the app's window, maximized on the second monitor, covers its work area exactly ({bounds} on {monitor?.Work})",
                monitor is { Primary: false } && bounds == monitor.Value.Work);
            window.WindowState = WindowState.Normal;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A bare window on <paramref name="monitor"/>, maximized while cloaked,
    /// that asks for a maximized size larger than the work area - and, with
    /// <paramref name="capTrack"/>, for a tracking size no larger than it.
    /// Where it ended up, or null.
    /// </summary>
    private static NativeRect? ReviewMaximizeProbe(MainWindow.MonitorInfo monitor, bool capTrack)
    {
        var work = monitor.Work;
        var parameters = new HwndSourceParameters("UltraExplorer maximize probe")
        {
            // WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_SYSMENU, as the app's window has.
            WindowStyle = 0x00040000 | 0x00010000 | 0x00020000 | 0x00080000,
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never active, never in the taskbar.
            ExtendedWindowStyle = 0x08000000 | 0x00000080,
            PositionX = work.Left + 40,
            PositionY = work.Top + 40,
            Width = 400,
            Height = 300
        };
        using var source = new HwndSource(parameters);
        source.AddHook((nint _, int message, nint _, nint lParam, ref bool _) =>
        {
            if (message == 0x0024)
            {
                var bounds = Marshal.PtrToStructure<MainWindow.MinMaxInfo>(lParam);
                bounds.MaxPosition = new MainWindow.PointL { X = work.Left - monitor.Monitor.Left, Y = work.Top - monitor.Monitor.Top };
                bounds.MaxSize = new MainWindow.PointL { X = work.Right - work.Left + 240, Y = work.Bottom - work.Top + 160 };
                if (capTrack)
                {
                    bounds.MaxTrackSize = new MainWindow.PointL { X = work.Right - work.Left, Y = work.Bottom - work.Top };
                }

                Marshal.StructureToPtr(bounds, lParam, false);
            }

            return 0;
        });
        DialogNative.CloakOwn(source.Handle, true);
        ReviewShowWindow(source.Handle, 3); // SW_SHOWMAXIMIZED
        var where = DialogNative.WindowBounds(source.Handle);
        ReviewShowWindow(source.Handle, 0); // SW_HIDE
        return where;
    }

    // ---- I027: a new window larger than the screen ---------------------------------------

    /// <summary>
    /// A new window is 1520 by 900 and centred on the work area, which on a
    /// 1080p screen at 125 % put its caption 46 pixels above the top of the
    /// screen; and its minimum height, 620, is taller than a small screen at
    /// 150 %.  Placed as either leaves it, before it is first shown, the
    /// window opens wholly on its monitor's work area all the same.
    /// </summary>
    private static async Task MainWindowPlacementChecksAsync()
    {
        Section("main window: a new window larger than the screen (I027)");
        if (ReviewSecondMonitor() is null)
        {
            Check("no second monitor: no window is opened on the main one", true);
            return;
        }

        // As WPF centres a window taller than the work area: as much above it as below.
        var centred = ShowReviewWindow((_, handle) =>
        {
            if (DialogNative.MonitorOf(handle) is { } monitor)
            {
                var work = monitor.Work;
                var width = Math.Min(work.Width - 16, (int)Math.Round(1520 * monitor.Scale));
                var height = work.Height + 92;
                var left = work.Left + (work.Width - width) / 2;
                var top = work.Top + (work.Height - height) / 2;
                DialogNative.Place(handle, new NativeRect(left, top, left + width, top + height));
            }
        });
        try
        {
            await SettingsSettle();
            var handle = new WindowInteropHelper(centred).Handle;
            var monitor = DialogNative.MonitorOf(handle);
            var bounds = DialogNative.WindowBounds(handle);
            Check($"a window centred taller than the work area opens wholly on it, its caption below the top ({bounds} in {monitor?.Work})",
                monitor is { Primary: false } && bounds is { } inside && monitor.Value.Work.Contains(inside));
            Check("still centred across it",
                monitor is { } across && bounds is { } placed
                && Math.Abs(placed.Left - across.Work.Left - (across.Work.Right - placed.Right)) <= 1);
        }
        finally
        {
            centred.Close();
        }

        // A minimum height taller than the work area holds the window to it.
        var scale = 1.0;
        var tall = ShowReviewWindow((shown, handle) =>
        {
            if (DialogNative.MonitorOf(handle) is { } monitor)
            {
                scale = monitor.Scale;
                shown.MinHeight = monitor.Work.Height / monitor.Scale + 120;
            }
        });
        try
        {
            await SettingsSettle();
            var handle = new WindowInteropHelper(tall).Handle;
            var monitor = DialogNative.MonitorOf(handle);
            var bounds = DialogNative.WindowBounds(handle);
            Check($"a window whose minimum is taller than the work area opens wholly on it ({bounds} in {monitor?.Work})",
                monitor is { Primary: false } && bounds is { } inside && monitor.Value.Work.Contains(inside));
            Check($"and its minimum height is what the work area holds ({tall.MinHeight:0.#} DIP)",
                monitor is { } held && tall.MinHeight * scale <= held.Work.Height);
        }
        finally
        {
            tall.Close();
        }
    }

    // ---- windows never shown -----------------------------------------------------------

    /// <summary>A window as the Settings checks have it: never shown, its page laid out at a size.</summary>
    private static MainWindow LaidOutReviewWindow(out MainViewModel shell)
    {
        var main = new MainWindow();
        shell = (MainViewModel)main.DataContext;
        LayOutWindow(main, 1400, 900);
        return main;
    }

    /// <summary>
    /// The left button going down, or up, on <paramref name="element"/> as the
    /// mouse sends it: the preview tunnels down to it, and each element on the
    /// way raises its own PreviewMouseLeftButtonDown or -Up from it.
    /// </summary>
    private static void ReviewPress(UIElement element, bool down = true, int clicks = 1)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = down ? UIElement.PreviewMouseDownEvent : UIElement.PreviewMouseUpEvent
        };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, clicks);
        element.RaiseEvent(press);
    }

    // ---- I074: a rename waiting in the folder list ---------------------------------------

    /// <summary>
    /// A second click on the selected row of the folder list renames it once
    /// the double-click time has passed.  A click on another row meanwhile, a
    /// key, the selection moving on or the window losing the foreground calls
    /// that off - it renamed whichever row was selected by then, and what was
    /// typed next went into the dialog.
    /// </summary>
    private static async Task MainWindowListRenameChecksAsync(string root)
    {
        Section("main window: a rename waiting in the folder list (I074)");
        var folder = Path.Combine(root, "rename-list");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        var main = LaidOutReviewWindow(out var shell);
        try
        {
            // The rename's question is answered here: no dialog is ever shown.
            var asked = new List<string>();
            typeof(MainViewModel).GetField(nameof(MainViewModel.PromptRequested), BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(shell, new Func<string, string, string, bool, string?>((title, _, value, _) =>
                {
                    asked.Add($"{title}: {value}");
                    return null;
                }));
            // On the nested canvas, as the window opens by default, the tree
            // canvas is not shown: a change on disk above these rows - the
            // temporary folder changes all the time - is left for when it is,
            // rather than moving the selection on meanwhile.
            shell.Tree.IsCanvasShown = false;
            shell.Tree.FolderList.IsVisible = true;
            await shell.Tree.FolderList.NavigateAsync(folder);
            LayOutWindow(main, 1400, 900);
            var list = main.FolderListItems;
            ListBoxItem? Row(string name) =>
                list.Items.OfType<FolderListItem>().FirstOrDefault(item => item.DisplayName == name) is { } item
                    ? list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem
                    : null;
            var (alpha, beta, gamma) = (Row("alpha.txt"), Row("beta.txt"), Row("gamma.txt"));
            Check("the folder list shows the folder's rows", alpha is not null && beta is not null && gamma is not null);
            if (alpha is null || beta is null || gamma is null)
            {
                return;
            }

            var wait = TimeSpan.FromMilliseconds(NativeShellService.DoubleClickMilliseconds + 700);
            var clickWasOnSelection = typeof(MainWindow).GetField("_folderListClickWasOnSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // A press selects the row, as the list box does on the way down.
            void Select(ListBoxItem row)
            {
                ReviewPress(row);
                list.SelectedItem = row.DataContext;
            }

            // A second click on the selected row: the list has the keyboard by
            // then, which a window never shown cannot give it.
            void ClickSelected(ListBoxItem row)
            {
                ReviewPress(row);
                clickWasOnSelection.SetValue(main, true);
                ReviewPress(row, down: false);
            }

            Select(alpha);
            ClickSelected(alpha);
            await Task.Delay(wait);
            Check($"a second click on the selected row renames it once the double-click time has passed ({string.Join(", ", asked)})",
                asked.SequenceEqual(["Rename: alpha.txt"]));

            asked.Clear();
            ClickSelected(alpha);
            Select(beta);
            await Task.Delay(wait);
            Check($"a click on another row meanwhile calls it off, and renames nothing ({string.Join(", ", asked)})", asked.Count == 0);

            asked.Clear();
            Select(alpha);
            ClickSelected(alpha);
            list.SelectedItem = gamma.DataContext;
            await Task.Delay(wait);
            Check($"the selection moving on to another row meanwhile calls it off ({string.Join(", ", asked)})", asked.Count == 0);

            asked.Clear();
            Select(alpha);
            ClickSelected(alpha);
            PressEscape(main);
            await Task.Delay(wait);
            Check($"a key meanwhile calls it off ({string.Join(", ", asked)})", asked.Count == 0);

            asked.Clear();
            ClickSelected(alpha);
            typeof(MainWindow).GetMethod("Window_Deactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, EventArgs.Empty]);
            await Task.Delay(wait);
            Check($"and so does the window losing the foreground ({string.Join(", ", asked)})", asked.Count == 0);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- I081: a path longer than the address bar ----------------------------------------

    /// <summary>
    /// The crumbs of a path longer than the bar has room for show their end -
    /// the folder the window is in - and keep it in view when the window is
    /// made narrower.  The strip has no scroll bar and no wheel, and stayed
    /// at its start.  A path that fits shows from its start, as before.
    /// </summary>
    private static async Task MainWindowCrumbStripChecksAsync(string root)
    {
        Section("main window: a path longer than the address bar (I081)");
        var deep = root;
        for (var level = 1; level <= 14; level++)
        {
            deep = Path.Combine(deep, $"folder-{level:D2}");
        }

        Directory.CreateDirectory(deep);
        var main = LaidOutReviewWindow(out var shell);
        try
        {
            var strip = SearchVisualChildren(main.AddressArea).OfType<ScrollViewer>().FirstOrDefault();
            Check("the address bar has its strip of crumbs", strip is not null);
            if (strip is null)
            {
                return;
            }

            async Task LayOut(double width)
            {
                LayOutWindow(main, width, 900);
                await SettingsSettle();
                LayOutWindow(main, width, 900);
            }

            shell.Address.SetPath(deep);
            await LayOut(1400);
            Check($"the crumbs of a long path overflow the bar ({strip.ScrollableWidth:0} pixels)", strip.ScrollableWidth > 0);
            Check($"and the strip shows their end, the folder the window is in (at {strip.HorizontalOffset:0} of {strip.ScrollableWidth:0})",
                strip.ScrollableWidth > 0 && Math.Abs(strip.HorizontalOffset - strip.ScrollableWidth) < 1);

            await LayOut(1100);
            Check($"made narrower, the window still shows it (at {strip.HorizontalOffset:0} of {strip.ScrollableWidth:0})",
                strip.ScrollableWidth > 0 && Math.Abs(strip.HorizontalOffset - strip.ScrollableWidth) < 1);

            shell.Address.SetPath(Path.GetPathRoot(root));
            await LayOut(1400);
            Check("a path that fits shows from its start", strip.ScrollableWidth == 0 && strip.HorizontalOffset == 0);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- I159: a press in a crumb's list of folders --------------------------------------

    /// <summary>
    /// A press in the list of folders behind a crumb's chevron is a press on
    /// that list, not on the bar: it came to the bar too, through the list's
    /// popup, and turned the crumbs into the line to type in under the open
    /// list.  A press on the bar's empty strip still does.
    /// </summary>
    private static async Task MainWindowCrumbMenuPressChecksAsync(string root)
    {
        Section("main window: a press in a crumb's list of folders (I159)");
        var folder = Path.Combine(root, "crumb-menu", "inner");
        Directory.CreateDirectory(folder);
        var main = LaidOutReviewWindow(out var shell);
        try
        {
            shell.Address.SetPath(folder);
            LayOutWindow(main, 1400, 900);
            var menu = SearchVisualChildren(main.AddressArea).OfType<Popup>().LastOrDefault()?.Child;
            var strip = SearchVisualChildren(main.AddressArea).OfType<ScrollViewer>().FirstOrDefault();
            Check("the last crumb has its list of folders", menu is not null && strip is not null);
            if (menu is null || strip is null)
            {
                return;
            }

            // A press in the open list, as its popup hands it on to the bar.
            // (A list never opened has no popup to hand it on.)
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = menu
            };
            typeof(MainWindow).GetMethod("AddressArea_PreviewMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [main.AddressArea, press]);
            await SettingsSettle();
            Check("a press in a crumb's list of folders leaves the bar as crumbs", !shell.Address.IsEditing);

            ReviewPress(strip);
            await SettingsSettle();
            Check("a press on the bar's empty strip still turns it into the line to type in", shell.Address.IsEditing);
            shell.Address.EndEdit();
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- I142: closed before it has finished starting ------------------------------------

    /// <summary>
    /// A window closed - or a session ended - while it is still starting saves
    /// the sidebar width it read, not the 240 its sidebar has until the start
    /// puts that width on.  Once it has, the width the sidebar has is saved,
    /// as before.
    /// </summary>
    private static void MainWindowSidebarWidthChecks()
    {
        Section("main window: closed before it has finished starting (I142)");
        var main = LaidOutReviewWindow(out var shell);
        try
        {
            var capture = typeof(MainWindow).GetMethod("CaptureStateForSave", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // What the workspace said, read; the start has not got to the sidebar yet.
            shell.SidebarWidth = 300;
            capture.Invoke(main, null);
            Check($"closed before its start put the saved sidebar width on, the window keeps that width ({shell.SidebarWidth:0})",
                shell.SidebarWidth == 300);

            // The start puts it on, and the sidebar is then dragged to 280.
            typeof(MainWindow).GetField("_sidebarWidthRestored", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(main, true);
            main.SidebarColumn.Width = new GridLength(280);
            LayOutWindow(main, 1400, 900);
            capture.Invoke(main, null);
            Check($"once it has, the width the sidebar has is the one kept ({shell.SidebarWidth:0})", shell.SidebarWidth == 280);
        }
        finally
        {
            shell.Dispose();
        }
    }

    private delegate bool ReviewMonitorProc(nint monitor, nint deviceContext, nint rect, nint data);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    private static extern bool ReviewEnumMonitors(nint deviceContext, nint clip, ReviewMonitorProc callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool ReviewMonitorInfo(nint monitor, ref MainWindow.MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ReviewShowWindow(nint window, int command);
}
