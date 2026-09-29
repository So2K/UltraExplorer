using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer;

/// <summary>
/// A development aid, and only for a test copy of the window
/// (<c>ULTRAEXPLORER_TEST_WINDOW=1</c>, which opens on a monitor nobody is
/// using and never takes the keyboard): <c>--menu-demo &lt;folder&gt;
/// --menu-demo-path &lt;folder&gt;</c> right-clicks, with the canvas's own
/// synthetic mouse, every sub-folder's title and every file of the folder
/// named once, then one of each and the folder's open space again and again,
/// and writes <c>menu-times.tsv</c> into the first folder: how long each menu
/// took to come up - from the button going down, and from it coming up to
/// the moment the menu is handed to Windows - how long it took to build, and
/// whether that was done while the button was down.  The first menu of each
/// kind is pictured, and a folder's with its New open too.  Every menu is
/// shut again by the demo itself, and none is shown unless the point it would
/// open at is on a monitor other than the primary one.
///
/// <c>--menu-demo-press &lt;ms&gt;</c> is how long the button is held (90 ms, a
/// quick click, unless said; the last rounds let go at once);
/// <c>--menu-demo-wait &lt;ms&gt;</c> how long to wait after start-up (8 s),
/// <c>--menu-demo-settle &lt;ms&gt;</c> after flying to the folder (1.5 s), and
/// <c>--menu-demo-pauses 300,6000</c> right-clicks the first folder again
/// after each pause, for what a pause costs.
/// </summary>
public partial class MainWindow
{
    private MenuDemoRound? _menuDemoRound;

    /// <summary>One right-click of the demo, and when each part of it happened.</summary>
    private sealed class MenuDemoRound
    {
        public long Down { get; set; }

        public long Shown { get; set; }

        public string? Picture { get; init; }

        /// <summary>Whether New is opened too, from the keyboard, and pictured with the menu.</summary>
        public bool OpenNew { get; init; }

        public string Report { get; set; } = string.Empty;
    }

    private async Task RunMenuDemoAsync()
    {
        if (!IsTestWindow || SwitchValue("--menu-demo") is not { Length: > 0 } output || SwitchValue("--menu-demo-path") is not { Length: > 0 } path)
        {
            return;
        }

        Directory.CreateDirectory(output);
        var lines = new List<string> { "kind\tround\tpress ms\tdown to menu ms\trelease to menu ms\tbuild ms\tbuilt on\tnote" };

        // Never on the monitor the user is working on: a window found there
        // is put away before anything else happens.
        if (!IsOffPrimaryMonitor())
        {
            Hide();
            lines.Add($"stopped: the window opened on the primary monitor at {Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0} {WindowState}");
            File.WriteAllLines(Path.Combine(output, "menu-times.tsv"), lines);
            return;
        }

        var press = int.TryParse(SwitchValue("--menu-demo-press"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked) ? asked : 90;
        var wait = int.TryParse(SwitchValue("--menu-demo-wait"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var waitAsked) ? waitAsked : 8000;

        ShellContextMenu.Tracking = OnMenuDemoTracking;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuDemoWpfOpened));
        try
        {
            await Task.Delay(wait);
            await ShellMenuWarmUp.Completed;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"# warm-up: {ShellMenuWarmUp.MenusBuilt} menus built in {ShellMenuWarmUp.Elapsed.TotalMilliseconds:F0} ms; dark menus: {DarkMenus.IsOn}"));
            if (!IsNested || await FirstPane.Tree.RevealAsync(path) is not { } folder)
            {
                lines.Add("the folder could not be shown on the nested canvas");
                return;
            }

            Nested.FlyTo(folder, 0.92, animated: false);
            await Task.Delay(int.TryParse(SwitchValue("--menu-demo-settle"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var settle) ? settle : 1500);
            var targets = FindMenuDemoTargets(folder);
            lines.Add($"# targets: {targets.Count(target => target.Kind == "folder item")} folders, {targets.Count(target => target.Kind == "file item")} files, {targets.Count(target => target.Kind == "folder background")} open space");
            var pointer = Nested.Pointer;

            // One right-click, the button held for a while or let go at once.
            async Task<bool> RoundAsync(string kind, string round, Point point, int held, string? picture)
            {
                var screen = Nested.PointToScreen(point);
                if (!IsOffPrimaryMonitor() || !IsOffPrimaryMonitor(screen))
                {
                    lines.Add($"stopped before {kind} {round}: the menu would open on the primary monitor");
                    return false;
                }

                var demo = new MenuDemoRound { Picture = picture, OpenNew = picture is not null && kind != "file item" };
                _menuDemoRound = demo;
                demo.Down = Stopwatch.GetTimestamp();
                pointer.Down(MouseButton.Right, point);
                if (held > 0)
                {
                    await Task.Delay(held);
                }

                pointer.Up(MouseButton.Right, point);
                for (var tick = 0; tick < 300 && demo.Shown == 0; tick++)
                {
                    await Task.Delay(10);
                }

                await Task.Delay(picture is null ? 350 : 1800);
                _menuDemoRound = null;

                // The release is when the finger lifts - the press's length after
                // the press - however long the window then takes to hear of it.
                var fromDown = Stopwatch.GetElapsedTime(demo.Down, demo.Shown).TotalMilliseconds;
                var (build, prepared, steps) = LastShownMenu;
                var builtOn = demo.Report == "the app's own menu" ? "-" : prepared ? "press" : "release";
                lines.Add(demo.Shown == 0
                    ? $"{kind}	{round}	{held}	-	-	-	-	no menu came up"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{kind}	{round}	{held}	{fromDown:F1}	{Math.Max(0, fromDown - held):F1}	{build.TotalMilliseconds:F1}	{builtOn}	{steps}; {demo.Report}"));
                return true;
            }

            // Every item once - most right-clicks are on something not clicked
            // before - then the first of each kind again and again.
            var pictured = new HashSet<string>();
            foreach (var (kind, name, point) in targets)
            {
                var picture = pictured.Add(kind) ? Path.Combine(output, $"{kind.Replace(' ', '-')}.png") : null;
                if (!await RoundAsync(kind, $"first {name}", point, press, picture))
                {
                    return;
                }
            }

            // For finding out what a pause before a right-click costs: the first
            // folder again after each pause asked for.
            if (SwitchValue("--menu-demo-pauses") is { Length: > 0 } pauses && targets.FirstOrDefault(target => target.Kind == "folder item") is { Kind: not null } paused)
            {
                foreach (var pause in pauses.Split(',').Select(text => int.Parse(text, CultureInfo.InvariantCulture)))
                {
                    await Task.Delay(pause);
                    if (!await RoundAsync("folder item", $"after {pause} ms", paused.Point, press, null))
                    {
                        return;
                    }
                }
            }

            foreach (var kind in new[] { "folder item", "file item", "folder background" })
            {
                if (targets.FirstOrDefault(target => target.Kind == kind) is not { Kind: not null } target)
                {
                    continue;
                }

                for (var round = 1; round < 9; round++)
                {
                    // The last rounds let go at once: no press to hide the work in.
                    if (!await RoundAsync(kind, $"again {round}", target.Point, round >= 6 ? 0 : press, null))
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            ShellContextMenu.Tracking = null;
            _menuDemoRound = null;
            lines.Add($"# menus built on the press and used: {PreparedMenusUsed}; built on the release: {MenusBuiltOnShow}");
            lines.Add($"window {Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0}, off the primary monitor: {IsOffPrimaryMonitor()}");
            File.WriteAllLines(Path.Combine(output, "menu-times.tsv"), lines);
        }
    }

    /// <summary>
    /// A point on each sub-folder's title and on each file of the folder, and
    /// one in the folder's own open space, as the canvas hits them - the
    /// folders and files taken in turn.
    /// </summary>
    private List<(string Kind, string Name, Point Point)> FindMenuDemoTargets(NestedFolder folder)
    {
        var folders = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        Point? background = null;
        var view = new Rect(0, 0, Nested.ActualWidth, Nested.ActualHeight);
        for (var y = 40.0; y < view.Height - 40; y += 5)
        {
            for (var x = 40.0; x < view.Width - 40; x += 5)
            {
                if (Nested.HitTest(new Point(x, y)) is not { } hit)
                {
                    continue;
                }

                if (hit.IsFile && hit.Folder == folder)
                {
                    files.TryAdd(hit.Path, new Point(hit.Bounds.X + hit.Bounds.Width / 2, hit.Bounds.Y + hit.Bounds.Height / 2));
                }
                else if (!hit.IsFile && hit.IsOnHeader && hit.Folder.Parent == folder)
                {
                    folders.TryAdd(hit.Path, new Point(hit.Bounds.X + hit.Bounds.Width / 2, hit.Bounds.Y + hit.Bounds.Width * NestedLayout.HeaderHeight / 2));
                }
                else if (!hit.IsFile && !hit.IsOnHeader && background is null && hit.Folder == folder && x > view.Width * 0.6 && y > view.Height * 0.6)
                {
                    background = new Point(x, y);
                }
            }
        }

        var targets = new List<(string, string, Point)>();
        using var folderList = folders.GetEnumerator();
        using var fileList = files.GetEnumerator();
        bool moreFolders, moreFiles;
        do
        {
            if (moreFolders = folderList.MoveNext())
            {
                targets.Add(("folder item", Path.GetFileName(folderList.Current.Key), folderList.Current.Value));
            }

            if (moreFiles = fileList.MoveNext())
            {
                targets.Add(("file item", Path.GetFileName(fileList.Current.Key), fileList.Current.Value));
            }
        }
        while (moreFolders || moreFiles);

        if (background is { } open)
        {
            targets.Add(("folder background", folder.Name, open));
        }

        return targets;
    }

    /// <summary>A native menu is about to go up: when, and a picture of it before it is shut again.</summary>
    private void OnMenuDemoTracking(IntPtr menu)
    {
        if (_menuDemoRound is not { } demo)
        {
            return;
        }

        demo.Shown = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(demo.Picture is null ? 120 : 600)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (demo.Picture is { } picture)
            {
                demo.Report = CaptureMenuDemoWindow(picture);
            }

            if (!demo.OpenNew || demo.Picture is null || MenuDemoWindows() is not [var root, ..] || NewItemIndex(menu) is not { } index)
            {
                EndMenu();
                return;
            }

            // Down to New and Right into it, as the keyboard does: the menu
            // fills New from what the Shell's New handler answers.
            for (var step = 0; step <= index; step++)
            {
                PostMessage(root, WmKeyDown, new IntPtr(VkDown), IntPtr.Zero);
            }

            PostMessage(root, WmKeyDown, new IntPtr(VkRight), IntPtr.Zero);
            var opened = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            opened.Tick += (_, _) =>
            {
                opened.Stop();
                demo.Report += "; with New open: " + CaptureMenuDemoWindow(Path.ChangeExtension(demo.Picture, null) + "-new.png");
                EndMenu();
            };
            opened.Start();
        };
        timer.Start();
    }

    /// <summary>How many items the keyboard passes over before New at the top of a menu, or null when it has none.</summary>
    private static int? NewItemIndex(IntPtr menu)
    {
        var passed = 0;
        var count = GetMenuItemCount(menu);
        for (var position = 0; position < count; position++)
        {
            if ((GetMenuState(menu, (uint)position, MenuByPosition) & MenuSeparator) != 0)
            {
                continue;
            }

            var text = new System.Text.StringBuilder(128);
            GetMenuStringW(menu, (uint)position, text, text.Capacity, MenuByPosition);
            if (GetSubMenu(menu, position) != IntPtr.Zero && text.ToString().Replace("&", string.Empty, StringComparison.Ordinal) == "New")
            {
                return passed;
            }

            passed++;
        }

        return null;
    }

    /// <summary>The menu windows of this thread on screen, the first one opened first.</summary>
    private static List<IntPtr> MenuDemoWindows()
    {
        var thread = GetCurrentThreadId();
        var menus = new List<IntPtr>();
        EnumWindows((window, data) =>
        {
            var name = new System.Text.StringBuilder(16);
            if (GetWindowThreadProcessId(window, out _) == thread && IsWindowVisible(window)
                && GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32768")
            {
                menus.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        // Windows lists the newest window first.
        menus.Reverse();
        return menus;
    }

    /// <summary>The app's own menu opened instead of the Shell's: when, and shut again.</summary>
    private void OnMenuDemoWpfOpened(object sender, RoutedEventArgs e)
    {
        if (_menuDemoRound is not { } demo || sender is not ContextMenu menu)
        {
            return;
        }

        demo.Shown = Stopwatch.GetTimestamp();
        demo.Report = "the app's own menu";
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(200);
            menu.IsOpen = false;
        });
    }

    /// <summary>
    /// The menu windows of this thread, copied off the screen into a PNG, and
    /// what they look like in numbers: how dark the menu is on average and how
    /// much of it is light - the text and the icons.
    /// </summary>
    private static string CaptureMenuDemoWindow(string path)
    {
        // All of them together: a submenu open beside its menu is one picture.
        var windows = new List<RectL>();
        var rect = new RectL { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
        foreach (var window in MenuDemoWindows())
        {
            if (GetWindowRect(window, out var one))
            {
                if (!IsOffPrimaryMonitor(new Point(one.Left, one.Top)) || !IsOffPrimaryMonitor(new Point(one.Right - 1, one.Bottom - 1)))
                {
                    return "a menu window reached the primary monitor: not pictured";
                }

                windows.Add(one);
                rect = new RectL
                {
                    Left = Math.Min(rect.Left, one.Left),
                    Top = Math.Min(rect.Top, one.Top),
                    Right = Math.Max(rect.Right, one.Right),
                    Bottom = Math.Max(rect.Bottom, one.Bottom)
                };
            }
        }

        if (windows.Count == 0)
        {
            return "no menu window found";
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            // Only the menus: whatever else is on that screen stays out of the picture.
            PatBlt(memory, 0, 0, width, height, 0x00000042);
            foreach (var one in windows)
            {
                BitBlt(memory, one.Left - rect.Left, one.Top - rect.Top, one.Right - one.Left, one.Bottom - one.Top, screen, one.Left, one.Top, 0x00CC0020);
            }

            SelectObject(memory, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            double sum = 0;
            var light = 0;
            foreach (var one in windows)
            {
                for (var y = one.Top - rect.Top; y < one.Bottom - rect.Top; y++)
                {
                    for (var x = one.Left - rect.Left; x < one.Right - rect.Left; x++)
                    {
                        var index = (y * width + x) * 4;
                        var luma = 0.0722 * pixels[index] + 0.7152 * pixels[index + 1] + 0.2126 * pixels[index + 2];
                        sum += luma;
                        if (luma > 170)
                        {
                            light++;
                        }
                    }
                }
            }

            // The numbers are the menus' own, not the black around them.
            var count = windows.Sum(one => (one.Right - one.Left) * (one.Bottom - one.Top));
            return string.Create(CultureInfo.InvariantCulture, $"menu {width}x{height} at {rect.Left},{rect.Top}; mean luma {sum / count:F0}; light pixels {100.0 * light / count:F1}%");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Whether a point on the screen is on a monitor other than the primary one.</summary>
    private static bool IsOffPrimaryMonitor(Point screen)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var point = new CursorPoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        return GetMonitorInfo(MonitorFromPoint(point, MonitorDefaultToNearest), ref info) && (info.Flags & MonitorInfoPrimary) == 0;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    private const int WmKeyDown = 0x0100;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const uint MenuByPosition = 0x0400;
    private const uint MenuSeparator = 0x0800;

    [DllImport("user32.dll")]
    private static extern bool EndMenu();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint item, System.Text.StringBuilder text, int capacity, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectL rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr deviceContext, int x, int y, int width, int height, uint operation);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
