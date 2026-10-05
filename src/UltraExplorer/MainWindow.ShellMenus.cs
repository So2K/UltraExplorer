using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The right-click menus.  On an item, and on the open space of a folder,
/// the menu is Windows' own - every verb and every installed extension, New
/// with its templates, Send to and Open with - drawn dark like the rest of
/// the window (<see cref="DarkMenus"/>), with the app's own items below the
/// Shell's.  Where the Shell has no menu - items from several folders, the
/// canvas outside every folder, a Shell that fails - the same entries make
/// the app's own menu instead.
///
/// <para><b>Speed.</b> Building a Shell menu is the slow part - every
/// installed extension is asked what it adds - so it is built while the
/// button is still down, from what is under the pointer, and shown the
/// moment the button comes up; the work hides inside the click.  The Shell's
/// handlers are loaded once at start-up by <see cref="ShellMenuWarmUp"/>, so
/// even the first menu of a session is built warm.  The build stays on the
/// window's thread, where the menu is shown: the handlers belong to the
/// apartment that made them, and the messages that fill and draw their
/// submenus arrive on the thread that shows it.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>How long a menu built for a press is kept for the release that may never come (a right-drag pans instead).</summary>
    private static readonly TimeSpan PreparedMenuLifetime = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The most items a press builds a menu for.  The Shell's menu grows
    /// with the selection - measured, 200 ms for 1,000 files and 650 ms for
    /// 3,000 - and a press that turns into a right-drag only pans, so a
    /// bigger selection's menu is built on the release, as a click.
    /// </summary>
    private const int PreparedMenuMostItems = 100;

    /// <summary>
    /// The most items the Shell's menu is built for at all.  It grows with
    /// the selection - 1.6 s for 5,000 files, measured, and on for as long as
    /// the selection goes - on the thread every window shares; a bigger
    /// selection gets the app's own menu.
    /// </summary>
    private const int ShellMenuMostItems = 2000;

    /// <summary>
    /// How long a press must stay still before its menu is built: a press
    /// that moves sooner is the start of a right-drag, which only pans, and
    /// building the menu it never shows held the pan's start.
    /// </summary>
    private static readonly TimeSpan PreparedMenuStillTime = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long an item on a share is waited for before its menu is built:
    /// the Shell asks the server while it parses the item, and a server that
    /// has gone away held every window for the network's timeout - over a
    /// minute, measured.  An item that does not answer in time gets the app's
    /// own menu.
    /// </summary>
    private static readonly TimeSpan ShareAnswerTime = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The question last put to each share, by its \\server\share: one still
    /// unanswered is not asked again, nor waited for, until it is answered.
    /// </summary>
    private static readonly ShellSharePreflight ShareQuestions = new(
        static share => Task.Run(() => Directory.Exists(share)),
        maximumPending: 2, maximumKeys: 64);

    private long _shellMenuRequestGeneration;

    // Menu events can be reentered while a preflight pumps. A second window
    // must not nest another wait and stretch the first one's deadline.
    [ThreadStatic]
    private static bool _waitingForMenuShare;

    private ShellContextMenu? _preparedMenu;
    private string? _preparedKey;
    private DispatcherOperation? _preparing;
    private DispatcherTimer? _preparedStill;
    private DispatcherTimer? _preparedExpiry;

    /// <summary>For tests: the menus built for a press, and the ones built only when shown.</summary>
    internal int PreparedMenusUsed { get; private set; }

    internal int MenusBuiltOnShow { get; private set; }

    /// <summary>For the test copy's measurements: how long the menu last shown took to build, and whether it was built on the press.</summary>
    internal (TimeSpan Build, bool Prepared, string Steps) LastShownMenu { get; private set; }

    // ---- built on the press, shown on the release ------------------------------------

    /// <summary>What a menu is for, as one string: the kind, Shift, and the paths.</summary>
    private static string MenuKey(bool background, IReadOnlyList<string> paths, bool extended) =>
        (background ? "B|" : "I|") + (extended ? "X|" : "-|") + string.Join('\n', paths).ToUpperInvariant();

    /// <summary>
    /// The right button went down on something whose menu the Shell has:
    /// that menu is built now, just after the press has been handled and
    /// drawn and before the release can be, so the release finds it ready.
    /// </summary>
    internal void PrepareShellMenu(bool background, IReadOnlyList<string> paths)
    {
        if (!ShellMenuCountAllowed(paths.Count, prepared: true) || _closeRequested || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // Not for a share, a drive mapped to one or a WSL distribution: the
        // Shell asks the server while it parses the items, which for one that
        // has gone away holds the window for the network's timeout - forty
        // seconds, measured - and a press that only meant to pan the canvas
        // would wait it out.  Their menu is built on the release, as a click.
        if (paths.Any(VolumeKinds.IsNetwork))
        {
            return;
        }

        var extended = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = MenuKey(background, paths, extended);
        if (key == _preparedKey && (_preparedMenu is not null || _preparing is not null || _preparedStill is not null))
        {
            return;
        }

        DropPreparedMenu();
        _preparedKey = key;
        string[] copy = [.. paths];
        var current = CaptureShellMenuRequest();
        // A right press can become a pan. Give it a short still interval
        // before doing the Shell work; release/pan/close cancels this timer.
        _preparedStill = CreateShellMenuStillTimer(Dispatcher, () =>
        {
            _preparedStill = null;
            if (_preparedKey != key || !current())
            {
                if (_preparedKey == key) _preparedKey = null;
                return;
            }

            try
            {
                var built = BuildShellMenu(background, copy, extended, current);
                if (!current())
                {
                    built?.Dispose();
                    return;
                }
                _preparedMenu = built;
            }
            catch (Exception)
            {
                // Nothing would catch it here: the app has no handler for what
                // escapes a dispatcher callback, so it would end the app.  The
                // release builds the menu again, and says what went wrong.
                if (current()) _preparedMenu = null;
            }

            if (!current()) return;
            if (_preparedMenu is null)
            {
                _preparedKey = null;
                return;
            }

            _preparedExpiry ??= new DispatcherTimer(PreparedMenuLifetime, DispatcherPriority.Background, (_, _) => DropPreparedMenu(), Dispatcher);
            _preparedExpiry.Stop();
            _preparedExpiry.Start();
        });
        _preparedStill.Start();
    }

    /// <summary>The menu built for the press when it is for this, else one built now; null when the Shell has none.</summary>
    private ShellContextMenu? TakeShellMenu(bool background, IReadOnlyList<string> paths, out bool abandoned)
    {
        abandoned = false;
        if (_closeRequested || Dispatcher.HasShutdownStarted)
        {
            abandoned = true;
            return null;
        }
        var extended = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = MenuKey(background, paths, extended);
        if (_preparedMenu is { } prepared && _preparedKey == key && prepared.IsOnThisThread)
        {
            _preparedMenu = null;
            _preparedKey = null;
            _preparedExpiry?.Stop();
            PreparedMenusUsed++;
            LastShownMenu = (prepared.BuildTime, true, prepared.BuildSteps);
            return prepared;
        }

        DropPreparedMenu();
        var current = CaptureShellMenuRequest();
        MenusBuiltOnShow++;
        var built = BuildShellMenu(background, paths, extended, current);
        if (!current())
        {
            built?.Dispose();
            abandoned = true;
            return null;
        }
        LastShownMenu = (built?.BuildTime ?? TimeSpan.Zero, false, built?.BuildSteps ?? string.Empty);
        return built;
    }

    /// <summary>Lets go of a menu built for a press whose release asked for another, or for none.</summary>
    private void DropPreparedMenu()
    {
        _shellMenuRequestGeneration++;
        _preparing?.Abort();
        _preparing = null;
        _preparedStill?.Stop();
        _preparedStill = null;
        _preparedKey = null;
        _preparedExpiry?.Stop();
        _preparedMenu?.Dispose();
        _preparedMenu = null;
    }

    private Func<bool> CaptureShellMenuRequest()
    {
        var generation = _shellMenuRequestGeneration;
        var selection = _viewModel.Tree.Selection.Version;
        var pane = ActivePane;
        return () => !_closeRequested && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished
            && generation == _shellMenuRequestGeneration
            && selection == _viewModel.Tree.Selection.Version && ReferenceEquals(pane, ActivePane);
    }

    private ShellContextMenu? BuildShellMenu(bool background, IReadOnlyList<string> paths, bool extended, Func<bool>? current = null)
    {
        Dispatcher.VerifyAccess();
        if (!ShellMenuCountAllowed(paths.Count, prepared: false)) return null;
        current ??= CaptureShellMenuRequest();
        if (!current()) return null;
        var owner = new WindowInteropHelper(this).Handle;
        if (owner == IntPtr.Zero)
        {
            return null;
        }

        var shares = paths.Select(VolumeKinds.ShareKey).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Items from different shares cannot have one native parent menu.
        if (shares.Length > 1) return null;
        if (shares is [var share] && !WaitForShellShare(Dispatcher, ShareQuestions.Ask(share), ShareAnswerTime, current))
            return null;
        if (!current()) return null;

        try
        {
            using var span = PerfLog.Measure(background ? "menu.build.background" : "menu.build.items");
            return background
                ? ShellContextMenu.ForFolderBackground(paths[0], owner, extended)
                : ShellContextMenu.ForItems(paths, owner, extended, offerNew: true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool ShellMenuCountAllowed(int count, bool prepared) =>
        count > 0 && count <= (prepared ? PreparedMenuMostItems : ShellMenuMostItems);

    internal static TimeSpan ShellSharePreflightBudget => ShareAnswerTime;

    /// <summary>A one-shot press timer. Stop cancels it before any COM work starts.</summary>
    internal static DispatcherTimer CreateShellMenuStillTimer(Dispatcher dispatcher, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PreparedMenuStillTime };
        timer.Tick += (_, _) => { timer.Stop(); callback(); };
        return timer;
    }

    /// <summary>
    /// Only a newly started preflight may wait. UI messages keep pumping on
    /// the original apartment; a second click on a still pending share falls
    /// back immediately. This bounds preflight, not subsequent Shell COM or
    /// installed extensions, which cannot be safely aborted on this apartment.
    /// </summary>
    internal static bool WaitForShellShare(Dispatcher dispatcher, ShellShareQuestion question, TimeSpan budget, Func<bool> current)
    {
        dispatcher.VerifyAccess();
        if (!current()) return false;
        if (question.Answer.IsCompleted) return question.Answer.IsCompletedSuccessfully && question.Answer.Result;
        if (!question.NewlyStarted || budget <= TimeSpan.Zero || _waitingForMenuShare) return false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (question.Answer.IsCompleted || !current() || elapsed.Elapsed >= budget || dispatcher.HasShutdownStarted)
                frame.Continue = false;
        };
        timer.Start();
        _waitingForMenuShare = true;
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); _waitingForMenuShare = false; }
        return current() && question.Answer.IsCompletedSuccessfully && question.Answer.Result;
    }

    internal readonly record struct ShellShareQuestion(Task<bool> Answer, bool NewlyStarted);

    /// <summary>
    /// Process-wide bounded, single-flight reachability. Pending entries are
    /// never evicted; even an uncancellable SMB wait consumes at most one of
    /// the two physical probes. Saturated capacity uses the app menu instead
    /// of queueing threads. Tests supply owned TaskCompletionSource probes.
    /// </summary>
    internal sealed class ShellSharePreflight
    {
        private sealed class Entry(Task<bool> answer)
        {
            internal readonly Task<bool> Answer = answer;
            internal long Expires = long.MaxValue;
        }

        private readonly Lock _gate = new();
        private readonly Dictionary<string, Entry> _questions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, Task<bool>> _probe;
        private readonly Func<long> _now;
        private readonly Action<Exception> _report;
        private readonly int _maximumPending;
        private readonly int _maximumKeys;
        private static readonly Task<bool> Unavailable = Task.FromResult(false);

        internal ShellSharePreflight(Func<string, Task<bool>> probe, int maximumPending = 2, int maximumKeys = 64, Func<long>? now = null, Action<Exception>? report = null)
        {
            if (maximumPending <= 0 || maximumKeys < maximumPending) throw new ArgumentOutOfRangeException(nameof(maximumPending));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _maximumPending = maximumPending;
            _maximumKeys = maximumKeys;
            _now = now ?? (() => Environment.TickCount64);
            _report = report ?? (error => Infrastructure.CrashReporter.Log("checking a share before its context menu", error));
        }

        internal int PendingCount { get { lock (_gate) return _questions.Values.Count(entry => !entry.Answer.IsCompleted); } }
        internal int KeyCount { get { lock (_gate) return _questions.Count; } }

        internal ShellShareQuestion Ask(string share)
        {
            lock (_gate)
            {
                if (_questions.TryGetValue(share, out var known))
                {
                    if (!known.Answer.IsCompleted || known.Expires > _now()) return new(known.Answer, false);
                    _questions.Remove(share);
                }
                if (_questions.Values.Count(entry => !entry.Answer.IsCompleted) >= _maximumPending)
                    return new(Unavailable, false);
                if (_questions.Count >= _maximumKeys)
                {
                    var oldest = _questions.Where(pair => pair.Value.Answer.IsCompleted).MinBy(pair => pair.Value.Expires);
                    if (oldest.Value is null) return new(Unavailable, false);
                    _questions.Remove(oldest.Key);
                }
                Task<bool> answer;
                try { answer = _probe(share) ?? Task.FromException<bool>(new InvalidOperationException("A share preflight returned no task.")); }
                catch (Exception error) { answer = Task.FromException<bool>(error); }
                var entry = new Entry(answer);
                _questions.Add(share, entry);
                _ = answer.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                        _report(completed.Exception!);
                    lock (_gate) entry.Expires = _now() + 10_000;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return new(answer, true);
            }
        }
    }

    /// <summary>
    /// Shows a built Shell menu at a point of an element, with the app's
    /// entries below the Shell's, and does what was picked: the app's entry,
    /// the app's own rename for the Shell's Rename (which needs a window of
    /// Explorer's to work in), or the Shell's item - and, for one of New's,
    /// what it made becomes the selection and is renamed, as in Explorer.
    /// The menu is let go of once the window is idle again, however this
    /// ends - the entries are made inside, so one that fails cannot leave it.
    /// </summary>
    private void ShowShellMenu(ShellContextMenu menu, FrameworkElement origin, Point point, Func<IReadOnlyList<ShellMenuEntry>> entries)
    {
        try
        {
            if (PresentationSource.FromVisual(this) is not HwndSource source)
            {
                return;
            }

            var screen = origin.PointToScreen(point);
            var (x, y) = ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            menu.AppendEntries(entries());
            var choice = menu.Show(source, x, y);
            RunShellChoice(menu, choice, x, y);
        }
        finally
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, menu.Dispose);
        }
    }

    /// <summary>Does what was picked on a Shell menu (see <see cref="ShowShellMenu"/>).</summary>
    internal void RunShellChoice(ShellContextMenu menu, ShellMenuChoice choice, int x, int y)
    {
        switch (choice.Pick)
        {
            case ShellMenuPick.App:
                choice.Entry?.Execute();
                return;
            case ShellMenuPick.Shell when !menu.IsBackground && string.Equals(choice.Verb, "rename", StringComparison.OrdinalIgnoreCase):
                SelectMenuItem(menu);
                _viewModel.RenameCommand.Execute(null);
                return;
            // Open on a folder goes into it here, as a double-click does.  The
            // Shell's verb has no window of Explorer's here to open it in, and
            // hands the folder to whatever opens folders - another window of
            // the app's, or Explorer's.  Several folders are each the Shell's.
            case ShellMenuPick.Shell when !menu.IsBackground && menu.Paths is [var opened] && IsFolderOpenVerb(choice.Verb) && IsMenuFolder(opened):
                _ = _viewModel.Tree.RevealPathAsync(opened);
                return;
            // New ▸ Shortcut starts Windows' own wizard, which names what it makes.
            case ShellMenuPick.Shell when choice.IsNew && menu.NewTarget is { } folder && !string.Equals(choice.Verb, "NewLink", StringComparison.OrdinalIgnoreCase):
                var before = FolderEntries(folder);
                menu.Invoke(choice, x, y);
                _ = FollowNewItemAsync(folder, before);
                return;
            case ShellMenuPick.Shell:
                menu.Invoke(choice, x, y);
                return;
        }
    }

    /// <summary>The Shell's verbs that open a folder where it is shown: Open, and Explore with the folder tree beside it.</summary>
    private static bool IsFolderOpenVerb(string verb) =>
        string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase) || string.Equals(verb, "explore", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an item a menu is for is a folder: as the selection knows it, else as the disk says.</summary>
    private bool IsMenuFolder(string path) =>
        _viewModel.Tree.Selection.TryGetItem(path, out var item) ? item.IsDirectory : Directory.Exists(path);

    /// <summary>
    /// Makes the one item a menu is for the selection, unless it is all that
    /// is selected already: the app's rename acts on the selection, and the
    /// menu can be for something else - a search result, while the canvas
    /// still has the result revealed before it - which picking Rename on
    /// its menu would leave alone and rename the other.
    /// </summary>
    private void SelectMenuItem(ShellContextMenu menu)
    {
        var selection = _viewModel.Tree.Selection;
        if (menu.Paths is not [var path] || (selection.Count == 1 && selection.Contains(path)))
        {
            return;
        }

        var isDirectory = Directory.Exists(path);
        long size = 0;
        if (!isDirectory)
        {
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: the rename says so.
            }
        }

        selection.ReplaceSingle(path, isDirectory, size, SelectionSource.Command);
    }

    /// <summary>What a folder holds, by full path; empty when it cannot be read.</summary>
    internal static HashSet<string> FolderEntries(string folder)
    {
        try
        {
            return new HashSet<string>(Directory.EnumerateFileSystemEntries(folder), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The item a New made in <paramref name="folder"/>: the newest of what is there now and was not before, or null.</summary>
    internal static string? FindNewEntry(string folder, IReadOnlySet<string> before)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(path => !before.Contains(path))
                .OrderByDescending(path => new FileInfo(path).CreationTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Explorer puts a new folder or file straight into renaming; so does the
    /// app: what the New made is read into the canvas, becomes the selection,
    /// and the app's rename opens on it - or <paramref name="rename"/>, for tests.
    /// </summary>
    internal async Task<string?> FollowNewItemAsync(string folder, IReadOnlySet<string> before, Action? rename = null)
    {
        if (FindNewEntry(folder, before) is not { } made)
        {
            return null;
        }

        await _viewModel.Tree.RefreshPathAsync(folder);
        _viewModel.Tree.Selection.ReplaceSingle(made, Directory.Exists(made), 0, SelectionSource.Command);
        (rename ?? (() => _viewModel.RenameCommand.Execute(null)))();
        return made;
    }

    // ---- the menus themselves --------------------------------------------------------

    private void ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point)
        => ShowContextMenu(paths, origin, point, includeCanvasCommands: true);

    /// <summary>
    /// The menu for items: the Shell's, with the canvas's entries below it
    /// where the items are on a canvas; the app's own where the Shell has
    /// none.  True when it was the Shell's.
    /// </summary>
    private bool ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point, bool includeCanvasCommands, bool fallBack = true)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        // The canvas's entries are left out when the thing clicked has no node
        // on a canvas: hiding or colouring what is not there would act on the
        // wrong item.
        if (TryShowShellMenu(background: false, paths, origin, point, () => includeCanvasCommands ? ItemMenuEntries(paths) : []))
        {
            return true;
        }

        // Items spread across different folders have no single Shell menu.
        if (fallBack)
        {
            ShowSelectionMenu(origin);
        }

        return false;
    }

    /// <summary>
    /// The Shell's menu for items or for a folder's open space, with the
    /// app's entries: true when it was shown, false when the Shell has none
    /// and the caller's own menu is wanted instead.  A failure once it is up
    /// is said, not answered with a second menu. A superseded/closed request
    /// is consumed without showing either an old Shell menu or a fallback.
    /// </summary>
    private bool TryShowShellMenu(bool background, IReadOnlyList<string> paths, FrameworkElement origin, Point point, Func<IReadOnlyList<ShellMenuEntry>> entries)
    {
        ShellContextMenu? menu;
        try
        {
            menu = TakeShellMenu(background, paths, out var abandoned);
            if (abandoned) return true;
        }
        catch (Exception ex)
        {
            _viewModel.Toast.ShowError($"Windows context menu failed: {ex.Message}");
            return false;
        }

        if (menu is null)
        {
            return false;
        }

        try
        {
            ShowShellMenu(menu, origin, point, entries);
        }
        catch (Exception ex)
        {
            _viewModel.Toast.ShowError($"Windows context menu failed: {ex.Message}");
        }

        return true;
    }

    /// <summary>
    /// The menu for the open space of <paramref name="folder"/>: the Shell's -
    /// Paste, the extensions, New, Properties - then the folder's own settings
    /// and the canvas's commands.  True when it was the Shell's.
    /// </summary>
    private bool ShowFolderAreaShellMenu(string folder, FrameworkElement origin, Point point) =>
        TryShowShellMenu(background: true, [folder], origin, point, () => FolderAreaEntries(folder, forShell: true));

    private void ShowCanvasMenu(Point point, Point graphPoint)
    {
        // Every folder owns a rectangle on the canvas, so a right-click inside one
        // is a right-click "in" that folder - which is where a new file belongs.
        // Blocks nest, so this is the innermost one the click landed in.
        var area = _viewModel.Tree.FolderAt(graphPoint);
        if (area is { IsDirectory: true })
        {
            _viewModel.Tree.SelectOnly(area);
            if (ShowFolderAreaShellMenu(area.FullPath, Editor, point))
            {
                return;
            }
        }

        ShowFolderAreaMenu(Editor, area);
    }

    /// <summary>
    /// The app's own menu for the open space of a folder: what can be made or
    /// put in it, the folder's own settings, and the canvas's own commands.
    /// Used where the Shell has no menu - outside every folder, or when it fails.
    /// </summary>
    private void ShowFolderAreaMenu(FrameworkElement placementTarget, ViewAllNodeViewModel? area)
    {
        // The folder the menu names is the folder its commands act on: New
        // folder, New text file and Paste go to the selection, so the folder
        // clicked in becomes the selection - as a click on empty space in an
        // Explorer window does.
        string? folder = null;
        if (area is { IsDirectory: true })
        {
            _viewModel.Tree.SelectOnly(area);
            folder = area.FullPath;
        }

        BuildFolderAreaMenu(placementTarget, folder).IsOpen = true;
    }

    /// <summary>
    /// <see cref="ShowFolderAreaMenu"/>'s menu for the open space of
    /// <paramref name="folder"/> - or, for none, of the canvas outside every
    /// folder - not yet open.
    /// </summary>
    internal ContextMenu BuildFolderAreaMenu(FrameworkElement placementTarget, string? folder)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        AddEntries(menu, FolderAreaEntries(folder, forShell: false));
        return menu;
    }

    /// <summary>
    /// What the menu of a folder's open space holds.  For the Shell's menu
    /// (<paramref name="forShell"/>) New, Paste and Properties are left to
    /// the Shell, which has its own; the app's menu has them itself.
    /// </summary>
    internal IReadOnlyList<ShellMenuEntry> FolderAreaEntries(string? folder, bool forShell)
    {
        var where = folder is not null
            ? FolderDisplayName(folder)
            : _viewModel.Tree.ActiveNode is { IsDirectory: true } active ? active.DisplayName : null;

        var entries = new List<ShellMenuEntry>();
        if (!forShell)
        {
            entries.Add(new ShellMenuEntry(where is null ? "New folder" : $"New folder in {where}") { Command = _viewModel.NewFolderCommand, Glyph = "\uE8F4", Shortcut = "Ctrl+Shift+N" });
            entries.Add(new ShellMenuEntry(where is null ? "New text file" : $"New text file in {where}") { Command = _viewModel.NewTextFileCommand, Glyph = "\uE8A5" });
            entries.Add(new ShellMenuEntry(where is null ? "Paste" : $"Paste into {where}") { Command = _viewModel.PasteCommand, Glyph = "\uE77F", Shortcut = "Ctrl+V" });
            entries.Add(ShellMenuEntry.Separator);
        }

        if (folder is not null)
        {
            entries.AddRange(FolderSettingsEntries(folder, where!, forShell));
            entries.Add(ShellMenuEntry.Separator);
        }

        entries.Add(new ShellMenuEntry("Fit all") { Command = _viewModel.FitAllCommand, Glyph = "\uE9A6", Shortcut = "Shift+1" });
        if (!IsNested)
        {
            entries.Add(new ShellMenuEntry("Reset zoom") { Command = _viewModel.ResetZoomCommand, Glyph = "\uE71E", Shortcut = "Ctrl+0" });
            entries.Add(new ShellMenuEntry("Collapse every branch") { Command = _viewModel.CollapseAllCommand, Glyph = "\uE72B" });
            entries.Add(new ShellMenuEntry("Tidy the layout") { Command = _viewModel.RelayoutCommand, Glyph = "\uE8AB" });
        }

        // Outside every folder, Sort by orders all of them.
        if (folder is null)
        {
            entries.Add(SortEntry(null));
        }

        if (HiddenFoldersEntry() is { } hidden)
        {
            entries.Add(hidden);
        }

        entries.Add(new ShellMenuEntry("Folder list") { Command = _viewModel.ToggleFolderListCommand, Glyph = "\uE8FD" });
        if (!IsNested)
        {
            entries.Add(new ShellMenuEntry("Minimap") { Command = _viewModel.ToggleMinimapCommand, Glyph = "\uE81E" });
        }

        entries.Add(LayersEntry());
        entries.AddRange(LayoutEntries());
        return entries;
    }

    /// <summary>
    /// A folder's own settings, in the menu of its open space: how its
    /// contents are sorted, its colour, its note, its pin on Home, and the
    /// folder shown in File Explorer, in Windows' properties sheet (in the
    /// app's menu) and, on the nested canvas, in the other pane.  Each acts
    /// on this folder by its path, whatever else is selected.
    /// </summary>
    private List<ShellMenuEntry> FolderSettingsEntries(string folder, string name, bool forShell)
    {
        var entries = new List<ShellMenuEntry> { SortEntry(folder, name), ColourEntry([folder]), NoteEntry(folder, name) };
        if (!IsPickerMode)
        {
            entries.Add(PinEntry(folder));
        }

        entries.Add(new ShellMenuEntry("Show in File Explorer", () => _viewModel.ShowInExplorer(folder)) { Glyph = "\uEC50" });
        if (!forShell)
        {
            // Alt+Enter shows the selection's properties: the same sheet only
            // while the folder is all that is selected, as the right-click that
            // opened this menu leaves it.
            var selected = _viewModel.Tree.SelectedOrActivePaths;
            var gesture = selected.Count == 1 && ViewAllPath.Equals(selected[0], folder) ? "Alt+Enter" : string.Empty;
            entries.Add(new ShellMenuEntry("Properties", () => _viewModel.ShowPropertiesOf(folder)) { Glyph = "\uE946", Shortcut = gesture });
        }

        // The folder in the other pane of a split view, which this splits if need be.
        if (OpenInOtherPaneEntryFor(folder) is { } other)
        {
            entries.Add(other);
        }

        return entries;
    }

    /// <summary>
    /// What the app adds to the Shell's menu for items on a canvas: on the
    /// nested canvas, the split view's - the folder in focus opened in the
    /// other pane, the items copied or moved to it - then their colour, the
    /// note and the pin of the one in focus, and hiding the folders among
    /// them or putting them back into the layout.  All of it by path, so the
    /// menu never waits for the canvas's tree to know the items.
    /// </summary>
    internal IReadOnlyList<ShellMenuEntry> ItemMenuEntries(IReadOnlyList<string> paths)
    {
        var selection = _viewModel.Tree.Selection;
        var focus = selection.Focus is { } focused && paths.Contains(focused, StringComparer.OrdinalIgnoreCase) ? focused : paths[0];
        var isFolder = selection.TryGetItem(focus, out var item) ? item.IsDirectory : Directory.Exists(focus);
        var entries = OtherPaneEntries(paths, isFolder ? focus : null);
        if (entries.Count > 0)
        {
            entries.Add(ShellMenuEntry.Separator);
        }

        entries.Add(ColourEntry(paths));
        entries.Add(NoteEntry(focus, FolderDisplayName(focus)));
        if (!IsPickerMode && isFolder)
        {
            // Pinning takes every folder selected, as the command does; unpinning the one in focus.
            entries.Add(_viewModel.IsPinned(focus)
                ? new ShellMenuEntry("Unpin from Home", () => _viewModel.UnpinPath(focus)) { Glyph = "\uE77A" }
                : new ShellMenuEntry("Pin to Home") { Command = _viewModel.AddToFavoritesCommand, Glyph = "\uE718" });
        }

        if (selection.FolderCount > 0)
        {
            entries.Add(new ShellMenuEntry("Hide from canvas") { Command = _viewModel.HideSelectedCommand, Glyph = "\uED1A", Shortcut = "Ctrl+H" });
        }

        // Only worth offering for something that is actually out of the layout.
        if (_viewModel.Tree.HasHandPlacedSelection)
        {
            entries.Add(new ShellMenuEntry("Return to layout") { Command = _viewModel.ReturnToLayoutCommand, Glyph = "\uE8AB" });
        }

        return entries;
    }

    private ShellMenuEntry NoteEntry(string path, string name)
    {
        var hasNote = !string.IsNullOrWhiteSpace(_viewModel.Marks.Get(path).Note);
        return new ShellMenuEntry(hasNote ? "Edit note\u2026" : "Add note\u2026", () => _viewModel.EditNoteOf(path, name)) { Glyph = "\uE70B" };
    }

    private ShellMenuEntry PinEntry(string folder) =>
        _viewModel.IsPinned(folder)
            ? new ShellMenuEntry("Unpin from Home", () => _viewModel.UnpinPath(folder)) { Glyph = "\uE77A" }
            : new ShellMenuEntry("Pin to Home", () => _viewModel.PinPath(folder)) { Glyph = "\uE718" };

    /// <summary>A folder's name as its own menu says it: as the tree shows it when it is the folder the tree has, else its own name.</summary>
    private string FolderDisplayName(string folder) =>
        _viewModel.Tree.ActiveNode is { } node && ViewAllPath.Equals(node.FullPath, folder) && node.DisplayName.Length > 0
            ? node.DisplayName
            : FolderName(folder);

    /// <summary>
    /// Explorer's "Sort by" for <paramref name="folder"/>, named after it -
    /// every folder when there is none, or when folders are all sorted the
    /// same: the four columns, then which way round, each marked when it is
    /// the current choice.  Picking another column starts it its own way -
    /// names and types from A, dates and sizes from the newest and largest -
    /// as a click on its header would.  While each folder has its own order,
    /// the folder's order can be let go of for the default, or made every
    /// folder's.
    /// </summary>
    private ShellMenuEntry SortEntry(string? folder, string? name = null)
    {
        var orders = _viewModel.Orders;
        var perFolder = orders.Scope == SortScope.PerFolder;
        if (!perFolder)
        {
            folder = null;
        }

        var sort = orders.SortOf(folder);
        var children = new List<ShellMenuEntry>();
        if (perFolder && folder is null)
        {
            children.Add(new ShellMenuEntry("Every folder without its own order") { IsEnabled = false });
            children.Add(ShellMenuEntry.Separator);
        }

        foreach (var column in Enum.GetValues<SortColumn>())
        {
            var chosen = column;
            children.Add(new ShellMenuEntry(ItemSort.Describe(column), () =>
            {
                var now = orders.SortOf(folder);
                if (now.Column != chosen)
                {
                    orders.Choose(folder, now.Click(chosen));
                }
            }) { Checked = sort.Column == column, IsRadio = true });
        }

        children.Add(ShellMenuEntry.Separator);
        foreach (var descending in new[] { false, true })
        {
            var way = descending;
            children.Add(new ShellMenuEntry(descending ? "Descending" : "Ascending", () => orders.Choose(folder, orders.SortOf(folder) with { Descending = way }))
            {
                Checked = sort.Descending == descending,
                IsRadio = true
            });
        }

        if (perFolder)
        {
            children.Add(ShellMenuEntry.Separator);
            children.Add(new ShellMenuEntry("Reset to the default order", () =>
            {
                if (folder is not null)
                {
                    orders.ResetFolder(folder);
                }
            })
            {
                IsEnabled = folder is not null && orders.HasOwnOrder(folder),
                ToolTip = $"Back to {ItemSort.Describe(orders.Default.Column)}, {ItemSort.DescribeDirection(orders.Default.Column, orders.Default.Descending)}, as every folder without its own order"
            });
            children.Add(new ShellMenuEntry("Use this order for all folders", () => orders.UseEverywhere(orders.SortOf(folder)))
            {
                IsEnabled = orders.Count > 0 || sort != orders.Default,
                ToolTip = "Every folder in this order, and every folder's own order let go of"
            });
        }

        return new ShellMenuEntry(folder is null ? "Sort by" : $"Sort {name ?? FolderName(folder)} by", Children: children);
    }

    /// <summary>
    /// The colour palette as a submenu: for the selection when there are no
    /// <paramref name="paths"/>, or for those paths whatever is selected -
    /// one path's present colour said beside it.
    /// </summary>
    private ShellMenuEntry ColourEntry(IReadOnlyList<string>? paths = null)
    {
        var current = paths is [var one] ? _viewModel.Marks.Get(one).AccentHex : null;
        var children = new List<ShellMenuEntry>(CanvasColours.Length);
        foreach (var colour in CanvasColours)
        {
            var hex = colour.Hex;
            Action run = paths is null
                ? () => _viewModel.SetAccentCommand.Execute(hex)
                : () => _viewModel.Tree.ApplyAccent(paths, string.IsNullOrEmpty(hex) ? null : hex);
            children.Add(new ShellMenuEntry(colour.Name, run)
            {
                Swatch = hex,
                Shortcut = current is not null && string.Equals(current, hex, StringComparison.OrdinalIgnoreCase) ? "Current" : string.Empty
            });
        }

        return new ShellMenuEntry("Colour", Children: children);
    }

    /// <summary>
    /// The way back for anything hidden.  Without a list of what is hidden, a
    /// folder put away months ago is unfindable - the classic failure of this
    /// kind of command.  Null while nothing is hidden.
    /// </summary>
    private ShellMenuEntry? HiddenFoldersEntry()
    {
        var hidden = _viewModel.HiddenPaths;
        if (hidden.Count == 0)
        {
            return null;
        }

        var children = new List<ShellMenuEntry>(hidden.Count + 2);
        foreach (var path in hidden)
        {
            var restored = path;
            children.Add(new ShellMenuEntry(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
                () => _viewModel.Tree.ShowHidden(restored)) { ToolTip = path });
        }

        children.Add(ShellMenuEntry.Separator);
        children.Add(new ShellMenuEntry("Show all hidden", () => _viewModel.Tree.ShowAllHidden()));
        return new ShellMenuEntry($"Hidden folders ({hidden.Count})", Children: children);
    }

    /// <summary>
    /// The canvas's layers as switches, as the layers button has them - each
    /// one flipped by picking it - with hidden items among them, the tree
    /// canvas's minimap, and the way back to every layer.
    /// </summary>
    private ShellMenuEntry LayersEntry()
    {
        var children = new List<ShellMenuEntry>();
        foreach (var layer in CanvasLayers.Each)
        {
            if (layer == CanvasLayer.Marks)
            {
                children.Add(new ShellMenuEntry("Hidden items")
                {
                    Command = _viewModel.ToggleHiddenItemsCommand,
                    Checked = _viewModel.Tree.ShowHiddenItems,
                    ToolTip = "The files and folders Windows marks as hidden"
                });
            }

            var chosen = layer;
            children.Add(new ShellMenuEntry(CanvasLayers.Describe(layer), () => _viewModel.SetLayer(chosen, !_viewModel.IsLayerShown(chosen)))
            {
                Checked = _viewModel.IsLayerShown(layer),
                ToolTip = CanvasLayers.Explain(layer)
            });
        }

        children.Add(ShellMenuEntry.Separator);
        children.Add(new ShellMenuEntry("Show all layers", () => _viewModel.Layers = CanvasLayer.All)
        {
            IsEnabled = _viewModel.Layers != CanvasLayer.All,
            ToolTip = "Every layer back on; hidden items stay as they are"
        });
        return new ShellMenuEntry("Layers", Children: children);
    }

    /// <summary>The two pictures of the drives, as a pair of choices; nothing in a file dialog.</summary>
    private IReadOnlyList<ShellMenuEntry> LayoutEntries() => IsPickerMode
        ? []
        :
        [
            ShellMenuEntry.Separator,
            new ShellMenuEntry("Nested canvas", () => _viewModel.Layout = CanvasLayout.Nested)
            {
                Checked = IsNested,
                IsRadio = true,
                ToolTip = "Every folder inside its parent, the whole disk on one screen"
            },
            new ShellMenuEntry("Tree canvas", () => _viewModel.Layout = CanvasLayout.Tree)
            {
                Checked = !IsNested,
                IsRadio = true,
                ToolTip = "Folders opened one at a time as a top-down tree"
            }
        ];

    // ---- the same entries as the app's own menu ----------------------------------------

    private void AddSortItems(ItemsControl menu, string? folder, string? name = null) => AddEntries(menu, [SortEntry(folder, name)]);

    private void AddColourItems(ItemsControl menu) => AddEntries(menu, [ColourEntry()]);

    private void AddHiddenFolderItems(ItemsControl menu)
    {
        if (HiddenFoldersEntry() is { } hidden)
        {
            AddEntries(menu, [hidden]);
        }
    }

    private void AddLayoutItems(ItemsControl menu) => AddEntries(menu, LayoutEntries());

    /// <summary>
    /// The app's own menu items for entries: a command with its glyph and
    /// shortcut, a switch, a swatch, a submenu, a line.  A line never starts
    /// the menu or follows another.
    /// </summary>
    internal static void AddEntries(ItemsControl menu, IEnumerable<ShellMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is not Separator)
                {
                    menu.Items.Add(new Separator());
                }

                continue;
            }

            menu.Items.Add(ToMenuItem(entry));
        }
    }

    private static MenuItem ToMenuItem(ShellMenuEntry entry)
    {
        var item = new MenuItem { Header = entry.Label, InputGestureText = entry.Shortcut };
        if (entry.ToolTip is { } tip)
        {
            item.ToolTip = tip;
        }

        if (entry.Checked is { } isChecked)
        {
            item.IsCheckable = true;
            item.IsChecked = isChecked;
        }

        if (entry.Swatch is { } hex)
        {
            item.Icon = new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(6),
                Background = string.IsNullOrEmpty(hex) ? Brushes.Transparent : Infrastructure.BrushCache.Get(hex),
                BorderBrush = Infrastructure.BrushCache.Get("#585858"),
                BorderThickness = new Thickness(string.IsNullOrEmpty(hex) ? 1 : 0)
            };
        }
        else if (entry.Glyph is { } glyph)
        {
            item.Icon = GlyphIcon(glyph);
        }

        if (entry.Children is { Count: > 0 } children)
        {
            AddEntries(item, children);
        }
        else
        {
            item.Command = entry.Command ?? new Infrastructure.RelayCommand(() => entry.Run?.Invoke());
        }

        if (!entry.IsEnabled)
        {
            item.IsEnabled = false;
        }

        return item;
    }

    /// <summary>A menu item's glyph, in the menu's text colour.</summary>
    private static TextBlock GlyphIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 13,

        // A menu lives in a popup, which is its own visual tree: nothing
        // inherits from the window into it, so a colour left unsaid here
        // is the system default - black text on a dark menu.
        Foreground = MenuGlyphBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
