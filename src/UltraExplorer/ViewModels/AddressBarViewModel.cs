using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.ViewModels;

/// <summary>
/// The address bar.  It reads as a row of crumbs until it is clicked, and as a
/// path line afterwards - and while it is a line, the folders that could finish
/// what has been typed are offered underneath it, the first of them completed
/// in place.  The crumbs are not just labels either: the chevron after each one
/// opens the folders that could have been taken instead.
/// </summary>
public sealed class AddressBarViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Enough to fill the drop-down and scroll a little; past that a listing is a
    /// directory dump rather than a suggestion, and the fastest way to the folder
    /// is to keep typing.
    /// </summary>
    internal const int MaxFolders = 40;

    /// <summary>Files are offered only once something has been typed, so a handful is plenty.</summary>
    internal const int MaxFiles = 8;

    internal const int MaxRecent = 5;

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Characters that would turn a name into a search pattern or an illegal
    /// path.  Typing one of them means the line is not a path yet, so nothing is
    /// suggested rather than something surprising.
    /// </summary>
    private static readonly char[] Wildcards = ['*', '?', '"', '<', '>', '|'];

    private readonly Func<string, int, Task> _navigate;
    private readonly Func<int> _beginNavigation;
    private readonly Func<int, bool> _isLatestNavigation;
    private readonly Func<IReadOnlyList<string>> _recent;
    private readonly Action<string, bool> _report;
    private readonly DispatcherTimer _debounce;

    private CancellationTokenSource? _query;
    private string _currentPath = string.Empty;
    private string _text = string.Empty;
    private bool _isEditing;
    private bool _isDropDownOpen;
    private bool _quiet;
    private AddressSuggestion? _highlighted;
    private bool _isDisposed;

    /// <summary>Which press of the recent-places button is the latest (see <see cref="ShowRecentAsync"/>).</summary>
    private int _recentTicket;

    /// <summary>Which of the bar's own requests is the latest, when nothing else hands out tickets.</summary>
    private int _ownTicket;

    /// <param name="navigate">Takes the window to a path that has been checked to exist.</param>
    /// <param name="recent">Where the window has already been, newest first.</param>
    /// <param name="report">Something to say to the user; the flag marks it an error.</param>
    public AddressBarViewModel(
        Func<string, Task> navigate,
        Func<IReadOnlyList<string>> recent,
        Action<string, bool> report)
        : this((path, _) => navigate(path), null, null, recent, report)
    {
    }

    /// <param name="navigate">Takes the window to a path that has been checked to exist, as the navigation the ticket names.</param>
    /// <param name="beginNavigation">
    /// Hands out the window's next navigation ticket.  One is taken the
    /// moment Enter is pressed, before the typed path is checked, so that
    /// anything asked for while a slow share answers is newer and wins.
    /// Null counts the bar's own requests only.
    /// </param>
    /// <param name="isLatestNavigation">Whether a ticket is still the newest; null with <paramref name="beginNavigation"/>.</param>
    /// <param name="recent">Where the window has already been, newest first.</param>
    /// <param name="report">Something to say to the user; the flag marks it an error.</param>
    public AddressBarViewModel(
        Func<string, int, Task> navigate,
        Func<int>? beginNavigation,
        Func<int, bool>? isLatestNavigation,
        Func<IReadOnlyList<string>> recent,
        Action<string, bool> report)
    {
        _navigate = navigate;
        _beginNavigation = beginNavigation ?? (() => ++_ownTicket);
        _isLatestNavigation = isLatestNavigation ?? (ticket => ticket == _ownTicket);
        _recent = recent;
        _report = report;

        // Long enough that holding a key down does not start a listing per
        // keystroke, short enough that stopping to think shows the answer.
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _debounce.Tick += (_, _) => _ = RefreshSuggestionsAsync();

        // Going somewhere is never held up by a place still being reached: a
        // share that takes its time to answer would otherwise leave the crumbs
        // dead and Enter ignored until it did.  The window's navigation lets
        // the last place asked for win.
        OpenSegmentCommand = new AsyncRelayCommand<BreadcrumbSegment>(OpenSegmentAsync, allowConcurrent: true);
        ToggleSegmentMenuCommand = new AsyncRelayCommand<BreadcrumbSegment>(ToggleSegmentMenuAsync);
        AcceptCommand = new AsyncRelayCommand<AddressSuggestion>(AcceptAsync, allowConcurrent: true);
        GoCommand = new AsyncRelayCommand(GoAsync, allowConcurrent: true);
        EditCommand = new RelayCommand(BeginEdit);
        CancelCommand = new RelayCommand(EndEdit);
        CopyCommand = new RelayCommand(() => Copy(quoted: false));
        CopyQuotedCommand = new RelayCommand(() => Copy(quoted: true));
        PasteAndGoCommand = new AsyncRelayCommand(PasteAndGoAsync, allowConcurrent: true);
        ShowRecentCommand = new AsyncRelayCommand(ShowRecentAsync, allowConcurrent: true);
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer);
    }

    /// <summary>The line should take the keyboard and select what is in it.</summary>
    public event Action? EditRequested;

    /// <summary>
    /// The whole path the best suggestion would finish the typed text with.  The
    /// line puts it in place with the part that was not typed selected, so typing
    /// on throws it away and Right or End keeps it.
    /// </summary>
    public event Action<string>? CompletionOffered;

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    public ObservableCollection<AddressSuggestion> Suggestions { get; } = [];

    public ICommand OpenSegmentCommand { get; }
    public ICommand ToggleSegmentMenuCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand GoCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyQuotedCommand { get; }
    public ICommand PasteAndGoCommand { get; }
    public ICommand ShowRecentCommand { get; }
    public ICommand OpenInExplorerCommand { get; }

    /// <summary>Where the window is, which is what the crumbs spell out.</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(HasPath));
            }
        }
    }

    public bool HasPath => _currentPath.Length > 0;

    /// <summary>What is in the line while it is being edited.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value) && !_quiet)
            {
                ScheduleQuery();
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsShowingCrumbs));
            }
        }
    }

    /// <summary>Crumbs and line are the same strip of window, one at a time.</summary>
    public bool IsShowingCrumbs => !_isEditing;

    public bool IsDropDownOpen
    {
        get => _isDropDownOpen;
        set => SetProperty(ref _isDropDownOpen, value);
    }

    /// <summary>
    /// The suggestion the arrow keys are on.  Nothing is highlighted to begin
    /// with: the first answer is already offered inside the line itself, and
    /// highlighting it as well would make Enter ambiguous.
    /// </summary>
    public AddressSuggestion? Highlighted
    {
        get => _highlighted;
        set => SetProperty(ref _highlighted, value);
    }

    // ---- what the window is showing ---------------------------------------

    /// <summary>Points the bar at a folder, which is where the crumbs come from.</summary>
    public void SetPath(string? path)
    {
        CurrentPath = path ?? string.Empty;
        UpdateBreadcrumbs();

        if (!IsEditing)
        {
            SetTextQuietly(CurrentPath);
        }
    }

    private void UpdateBreadcrumbs()
    {
        foreach (var segment in Breadcrumbs)
        {
            segment.IsMenuOpen = false;
        }

        Breadcrumbs.Clear();
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        IReadOnlyList<string> chain;
        try
        {
            chain = ViewAllPath.AncestorChain(CurrentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        for (var index = 0; index < chain.Count; index++)
        {
            var step = chain[index];
            var name = System.IO.Path.GetFileName(step.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
            {
                name = step;
            }

            Breadcrumbs.Add(new BreadcrumbSegment(name, step, index == chain.Count - 1));
        }
    }

    // ---- editing the line --------------------------------------------------

    /// <summary>
    /// Turns the crumbs into a line holding the current path.  What is inside the
    /// current folder is offered at once rather than after a keystroke: the whole
    /// reason for opening the line is to go somewhere else, and the next place is
    /// usually one of the folders that are already here.
    /// </summary>
    public void BeginEdit()
    {
        SetTextQuietly(CurrentPath);
        IsEditing = true;
        EditRequested?.Invoke();
        _ = RefreshSuggestionsAsync();
    }

    /// <summary>Puts the crumbs back and forgets whatever was half-typed.</summary>
    public void EndEdit()
    {
        _debounce.Stop();
        CancelQuery();
        IsEditing = false;
        IsDropDownOpen = false;
        Highlighted = null;
        Suggestions.Clear();
        SetTextQuietly(CurrentPath);
    }

    /// <summary>
    /// Walks the drop-down, putting the highlighted path into the line as it goes
    /// so that Enter always sends you where the line says.
    /// </summary>
    public void MoveHighlight(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        IsDropDownOpen = true;

        var index = Highlighted is null ? (delta > 0 ? -1 : 0) : Suggestions.IndexOf(Highlighted);
        index += delta;
        if (index < 0)
        {
            index = Suggestions.Count - 1;
        }
        else if (index >= Suggestions.Count)
        {
            index = 0;
        }

        Highlighted = Suggestions[index];
        SetTextQuietly(Highlighted.FullPath);
    }

    /// <summary>
    /// Takes the offered folder into the line and asks what is inside it, which
    /// is what Tab does in a shell: one key per level, no mouse.
    /// </summary>
    public void Complete()
    {
        var chosen = Highlighted ?? Suggestions.FirstOrDefault();
        if (chosen is null)
        {
            return;
        }

        var completed = chosen.Kind == AddressSuggestionKind.File
            ? chosen.FullPath
            : chosen.FullPath.TrimEnd(Separators) + System.IO.Path.DirectorySeparatorChar;

        Highlighted = null;
        SetTextQuietly(completed);
        _ = RefreshSuggestionsAsync();
    }

    /// <summary>
    /// The drop-down button and F4: where this window has already been, which is
    /// the one list that cannot be worked out from what has been typed.
    ///
    /// Which of those places are still there, and which drives are ready, is
    /// asked off the interface thread: a share that has gone offline takes as
    /// long as the network allows to say so, and a disc spinning up takes
    /// seconds.  A list that arrives after the line has moved on, or after the
    /// button was pressed again, is dropped.
    /// </summary>
    public async Task ShowRecentAsync()
    {
        if (!IsEditing)
        {
            SetTextQuietly(CurrentPath);
            IsEditing = true;
            EditRequested?.Invoke();
        }

        var ticket = ++_recentTicket;
        var typed = Text;
        var recent = _recent();
        var found = await Task.Run(() => RecentOrDrives(recent));
        if (_isDisposed
            || ticket != _recentTicket
            || !IsEditing
            || !string.Equals(typed, Text, StringComparison.Ordinal))
        {
            return;
        }

        Show(found, string.Empty);
    }

    /// <summary>The places recently been to that are still there, or the drives when there are none.  Off the interface thread.</summary>
    private static IReadOnlyList<AddressSuggestion> RecentOrDrives(IReadOnlyList<string> recent)
    {
        var found = new List<AddressSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRecent(found, seen, recent, string.Empty);
        if (found.Count == 0)
        {
            AddDrives(found, seen, string.Empty);
        }

        return found;
    }

    // ---- going somewhere ---------------------------------------------------

    private Task OpenSegmentAsync(BreadcrumbSegment? segment)
        => segment is null ? Task.CompletedTask : _navigate(segment.FullPath, _beginNavigation());

    private async Task AcceptAsync(AddressSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        foreach (var segment in Breadcrumbs)
        {
            segment.IsMenuOpen = false;
        }

        await GoToAsync(suggestion.FullPath);
    }

    private Task GoAsync() => GoToAsync(Highlighted?.FullPath ?? Text);

    private async Task GoToAsync(string target)
    {
        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            expanded = target.Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return;
        }

        // Asked off the interface thread: a share that is offline takes as
        // long as the network allows to say it is not there.  The place in
        // line is taken now, so a favourite clicked or Back pressed while it
        // answers is newer and wins; and an answer that comes after the line
        // was typed in again, opened or closed is not what is on screen any
        // more - going there, closing the line over the new typing or saying
        // the old path does not exist would all be wrong.
        var ticket = _beginNavigation();
        var typed = Text;
        var wasEditing = IsEditing;
        var current = CurrentPath;
        var resolved = await Task.Run(() => Resolve(expanded, current));
        if (_isDisposed
            || !_isLatestNavigation(ticket)
            || IsEditing != wasEditing
            || !string.Equals(typed, Text, StringComparison.Ordinal))
        {
            return;
        }

        if (resolved is null)
        {
            _report("That location does not exist.", true);
            return;
        }

        EndEdit();
        await _navigate(resolved, ticket);
    }

    /// <summary>
    /// What a line typed into the bar names, as a full path that exists, or
    /// null.  A drive letter on its own means the drive: to Windows "C:" means
    /// whatever directory the process last left on C, which is where the
    /// program was started from.  Anything else that is not a full path -
    /// "..", "Windows", "\Temp" - is read the way a shell reads it, against
    /// the folder the window is showing (<paramref name="currentPath"/>, or
    /// the folder of the file it is showing), never against the directory
    /// the process happens to be in.  Touches the disk; off the interface
    /// thread.
    /// </summary>
    internal static string? Resolve(string expanded, string currentPath)
    {
        try
        {
            if (expanded.Length == 2 && expanded[1] == ':' && char.IsAsciiLetter(expanded[0]))
            {
                expanded += System.IO.Path.DirectorySeparatorChar;
            }

            if (!System.IO.Path.IsPathFullyQualified(expanded))
            {
                if (string.IsNullOrWhiteSpace(currentPath) || !System.IO.Path.IsPathFullyQualified(currentPath))
                {
                    return null;
                }

                var folder = File.Exists(currentPath) ? System.IO.Path.GetDirectoryName(currentPath) : currentPath;
                if (string.IsNullOrEmpty(folder))
                {
                    return null;
                }

                expanded = System.IO.Path.GetFullPath(expanded, folder);
            }

            return Directory.Exists(expanded) || File.Exists(expanded) ? expanded : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- the crumb chevrons ------------------------------------------------

    private async Task ToggleSegmentMenuAsync(BreadcrumbSegment? segment)
    {
        if (segment is null)
        {
            return;
        }

        if (segment.IsMenuOpen)
        {
            segment.IsMenuOpen = false;
            return;
        }

        foreach (var other in Breadcrumbs)
        {
            other.IsMenuOpen = false;
        }

        var path = segment.FullPath;
        var children = await Task.Run(() => ChildFolders(path));
        if (_isDisposed)
        {
            return;
        }

        segment.Children.Clear();
        foreach (var child in children)
        {
            segment.Children.Add(child);
        }

        // Opened even when empty: a chevron that does nothing reads as broken,
        // one that says "no sub-folders" has answered the question.
        segment.IsMenuOpen = true;
    }

    // ---- copying and pasting the path --------------------------------------

    private void Copy(bool quoted)
    {
        if (!HasPath)
        {
            return;
        }

        try
        {
            Clipboard.SetText(quoted ? $"\"{CurrentPath}\"" : CurrentPath);
            _report(quoted ? "Address copied as a quoted path" : "Address copied", false);
        }
        catch (Exception ex)
        {
            _report($"The clipboard is busy - {ex.Message}", true);
        }
    }

    private async Task PasteAndGoAsync()
    {
        string? pasted = null;
        try
        {
            // A path copied from Explorer arrives as a file drop rather than as
            // text, and that is the commonest way one gets onto the clipboard.
            if (NativeShellService.GetClipboardPayload() is { Paths.Length: > 0 } payload)
            {
                pasted = payload.Paths[0];
            }
            else if (Clipboard.ContainsText())
            {
                pasted = Clipboard.GetText();
            }
        }
        catch (Exception ex)
        {
            _report($"The clipboard is busy - {ex.Message}", true);
            return;
        }

        if (string.IsNullOrWhiteSpace(pasted))
        {
            _report("The clipboard does not hold a path.", true);
            return;
        }

        await GoToAsync(pasted.Trim());
    }

    private void OpenInExplorer()
    {
        if (!HasPath)
        {
            return;
        }

        try
        {
            NativeShellService.Open(CurrentPath);
        }
        catch (Exception ex)
        {
            _report(ex.Message, true);
        }
    }

    // ---- suggestions -------------------------------------------------------

    private void ScheduleQuery()
    {
        if (!IsEditing)
        {
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Reads the folder behind whatever is in the line and offers what is in it.
    /// The reading happens off the interface thread, and an answer that arrives
    /// after the text has moved on is dropped rather than shown late.
    /// </summary>
    internal async Task RefreshSuggestionsAsync()
    {
        _debounce.Stop();
        CancelQuery();

        var cancellation = new CancellationTokenSource();
        _query = cancellation;

        var typed = Text;
        var recent = _recent();

        IReadOnlyList<AddressSuggestion> found;
        try
        {
            found = await Task.Run(() => Collect(typed, recent), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isDisposed
            || cancellation.IsCancellationRequested
            || !IsEditing
            || !string.Equals(typed, Text, StringComparison.Ordinal))
        {
            return;
        }

        Show(found, typed);
    }

    private void Show(IReadOnlyList<AddressSuggestion> found, string typed)
    {
        var kept = Highlighted?.FullPath;

        Suggestions.Clear();
        foreach (var suggestion in found)
        {
            Suggestions.Add(suggestion);
        }

        Highlighted = kept is null
            ? null
            : Suggestions.FirstOrDefault(item => string.Equals(item.FullPath, kept, StringComparison.OrdinalIgnoreCase));

        IsDropDownOpen = IsEditing && Suggestions.Count > 0;

        if (Completion(found, typed) is { } completion)
        {
            CompletionOffered?.Invoke(completion);
        }
    }

    /// <summary>
    /// The path the line should be finished with, or null when nothing on offer
    /// carries on from what was typed.  Recents are left out: they are somewhere
    /// else in the tree, and pulling one into the line under the caret would
    /// rewrite what is being typed rather than continue it.
    /// </summary>
    internal static string? Completion(IReadOnlyList<AddressSuggestion> found, string typed)
    {
        if (typed.Length == 0)
        {
            return null;
        }

        foreach (var suggestion in found)
        {
            if (suggestion.Kind != AddressSuggestionKind.Recent
                && suggestion.FullPath.Length > typed.Length
                && suggestion.FullPath.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            {
                return suggestion.FullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Everything the half-typed path could mean.  Pure and off the interface
    /// thread: given the same text and the same history it gives the same list.
    /// </summary>
    internal static IReadOnlyList<AddressSuggestion> Collect(string typed, IReadOnlyList<string> recent)
    {
        var found = new List<AddressSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string text;
        try
        {
            text = Environment.ExpandEnvironmentVariables(typed.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            text = typed.Trim().Trim('"');
        }

        // "E:" on its own does not mean the drive: to Windows it means whatever
        // directory that drive was last left in, and enumerating it answers with
        // paths like "E:.git" that lead nowhere.  A drive letter means the drive.
        if (text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]))
        {
            text += System.IO.Path.DirectorySeparatorChar;
        }

        if (text.Length == 0)
        {
            AddDrives(found, seen, string.Empty);
        }
        else if (File.Exists(text))
        {
            // The line was opened on a file - selecting one on the canvas points
            // the bar at it - and what is worth offering then is the rest of the
            // folder it is in, not the one file already named.
            var (folder, _) = Split(text);
            if (folder is not null)
            {
                AddChildren(found, seen, folder, string.Empty);
            }
        }
        else if (Exists(text))
        {
            // The line already names a folder, so what is inside it is the useful
            // answer - but a sibling that carries on from the same letters is
            // still worth offering, because "Program Files" is also the start of
            // "Program Files (x86)".
            AddChildren(found, seen, text, string.Empty);

            var (parent, leaf) = Split(text);
            if (parent is not null && leaf.Length > 0)
            {
                AddChildren(found, seen, parent, leaf);
            }
        }
        else
        {
            var (folder, leaf) = Split(text);
            if (folder is null)
            {
                AddDrives(found, seen, leaf);
            }
            else
            {
                AddChildren(found, seen, folder, leaf);
            }
        }

        AddRecent(found, seen, recent, text);
        return found;
    }

    /// <summary>
    /// Splits a half-typed path at its last separator: everything before it is a
    /// folder to look in, everything after it is the start of a name.  A text
    /// with no separator at all is not a path yet, and answers with no folder.
    /// </summary>
    internal static (string? Folder, string Leaf) Split(string text)
    {
        var separator = text.LastIndexOfAny(Separators);
        return separator < 0
            ? (null, text)
            : (text[..(separator + 1)], text[(separator + 1)..]);
    }

    private static void AddChildren(
        List<AddressSuggestion> found,
        HashSet<string> seen,
        string folder,
        string leaf)
    {
        if (leaf.IndexOfAny(Wildcards) >= 0 || !Directory.Exists(folder))
        {
            return;
        }

        var pattern = leaf.Length == 0 ? "*" : leaf + "*";
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        try
        {
            Take(Directory.EnumerateDirectories(folder, pattern, options), MaxFolders, AddressSuggestionKind.Folder);

            // Every file in a folder is a listing, not a suggestion: files are
            // worth offering only once enough has been typed to mean one of them.
            if (leaf.Length > 0)
            {
                Take(Directory.EnumerateFiles(folder, pattern, options), MaxFiles, AddressSuggestionKind.File);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A folder that went away or was never readable simply has nothing to
            // suggest; the line still works as a place to type a path.
        }

        void Take(IEnumerable<string> paths, int limit, AddressSuggestionKind kind)
        {
            var batch = new List<AddressSuggestion>(limit);
            foreach (var path in paths)
            {
                if (batch.Count == limit)
                {
                    break;
                }

                var name = System.IO.Path.GetFileName(path);
                if (name.Length > 0)
                {
                    batch.Add(new AddressSuggestion(name, path, kind));
                }
            }

            batch.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var suggestion in batch)
            {
                if (seen.Add(suggestion.FullPath))
                {
                    found.Add(suggestion);
                }
            }
        }
    }

    private static void AddDrives(List<AddressSuggestion> found, HashSet<string> seen, string prefix)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return;
        }

        foreach (var drive in drives)
        {
            string name;
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                name = drive.Name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(name))
            {
                found.Add(new AddressSuggestion(name, name, AddressSuggestionKind.Drive));
            }
        }
    }

    private static void AddRecent(
        List<AddressSuggestion> found,
        HashSet<string> seen,
        IReadOnlyList<string> recent,
        string text)
    {
        var added = 0;
        foreach (var path in recent)
        {
            if (added == MaxRecent)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path)
                || (text.Length > 0 && path.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0))
            {
                continue;
            }

            if (seen.Contains(path) || !Directory.Exists(path))
            {
                continue;
            }

            seen.Add(path);
            var name = System.IO.Path.GetFileName(path.TrimEnd(Separators));
            found.Add(new AddressSuggestion(
                name.Length > 0 ? name : path,
                path,
                AddressSuggestionKind.Recent));
            added++;
        }
    }

    /// <summary>The sub-folders behind one crumb's chevron.</summary>
    internal static IReadOnlyList<AddressSuggestion> ChildFolders(string folder)
    {
        var found = new List<AddressSuggestion>();
        AddChildren(found, new HashSet<string>(StringComparer.OrdinalIgnoreCase), folder, string.Empty);
        return found;
    }

    private static bool Exists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void SetTextQuietly(string text)
    {
        _quiet = true;
        try
        {
            Text = text;
        }
        finally
        {
            _quiet = false;
        }
    }

    private void CancelQuery()
    {
        _query?.Cancel();
        _query?.Dispose();
        _query = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _debounce.Stop();
        CancelQuery();
    }
}
