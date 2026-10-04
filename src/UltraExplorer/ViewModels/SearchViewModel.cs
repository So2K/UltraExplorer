using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;

namespace UltraExplorer.ViewModels;

/// <summary>
/// The search box and the panel of results under it.
///
/// <para><b>Everywhere, here first.</b>  Every search covers every drive, and
/// the results come in groups: what is in the folder the search was made
/// from and inside it, then everything else (<see cref="SearchRanking"/>).
/// The folder is the one the sort headers would sort - the folder selected,
/// the one the selected file is in, or the one in view - taken when the
/// text changes.  Going to another folder - the navigation pane, the
/// canvas, the address bar - orders the results for that one once the
/// move settles; going to a result from the list does not, so the list
/// never reshuffles under the pointer that picked from it.</para>
///
/// <para><b>As you type.</b>  A search starts a moment after the last key -
/// Everything answers in milliseconds - and replaces the one before.  The
/// results stay up while the canvas is worked with: clicking a result shows
/// it on the canvas, the arrows go through them, and only Escape in the box,
/// the close button or clearing the text puts the panel away.</para>
/// </summary>
public sealed class SearchViewModel : ObservableObject, IDisposable
{
    /// <summary>The pause after the last key before a search starts.</summary>
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(70);

    /// <summary>The pause after the last arrow key before the canvas goes to the result.</summary>
    private static readonly TimeSpan RevealPause = TimeSpan.FromMilliseconds(160);

    /// <summary>How long the folder must stay put before the results are ordered for it.</summary>
    private static readonly TimeSpan FolderSettle = TimeSpan.FromMilliseconds(600);

    /// <summary>How long after going to a result the folder's changes are that trip's, not the user's.</summary>
    private static readonly TimeSpan RevealQuiet = TimeSpan.FromMilliseconds(1500);

    private readonly SearchEngine _engine = new();
    private readonly ShellIconService _icons;
    private readonly Func<string, Task> _reveal;
    private readonly Action<string, bool> _open;
    private readonly DispatcherTimer _typing;
    private readonly DispatcherTimer _revealing;
    private readonly DispatcherTimer _waitingForEverything;
    private readonly DispatcherTimer _folderSettling;
    private CancellationTokenSource? _run;
    private Dictionary<string, SearchResultViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private string _text = string.Empty;
    private bool _isOpen;
    private bool _isBusy;
    private IReadOnlyList<ISearchRow> _rows = [];
    private IReadOnlyList<SearchResultViewModel> _results = [];
    private SearchResultViewModel? _selected;
    private string _summary = string.Empty;
    private string _status = string.Empty;
    private string _emptyText = string.Empty;
    private SearchSource _source = SearchSource.Everything;
    private string? _here;
    private string? _seenFolder;
    private long _quietUntil;
    private bool _disposed;

    /// <summary>The snapshot on screen, and the search it answers: a walk's is ordered again for another folder rather than searched again.</summary>
    private SearchSnapshot? _shown;
    private SearchQuery? _shownQuery;

    /// <summary>Set while the wait for Everything is asking whether it is ready yet.</summary>
    private bool _askingEverything;

    /// <param name="icons">Where the rows' icons come from.</param>
    /// <param name="reveal">Shows a path on the canvas: flies there and selects it.</param>
    /// <param name="open">Opens a path: a file in its program, a folder by going into it.  The second argument says whether it is a folder.</param>
    public SearchViewModel(ShellIconService icons, Func<string, Task> reveal, Action<string, bool> open)
    {
        _icons = icons;
        _reveal = reveal;
        _open = open;
        _typing = new DispatcherTimer(DispatcherPriority.Input) { Interval = TypingPause };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            _ = RunAsync();
        };
        _revealing = new DispatcherTimer(DispatcherPriority.Input) { Interval = RevealPause };
        _revealing.Tick += (_, _) =>
        {
            _revealing.Stop();
            _ = RevealSelectedAsync();
        };

        // While a walk stands in for an Everything that is starting or still
        // reading the drives, the search is made again once it can answer.
        _waitingForEverything = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _waitingForEverything.Tick += async (_, _) =>
        {
            if (!IsOpen || _source != SearchSource.Walk)
            {
                _waitingForEverything.Stop();
                return;
            }

            // Asked off this thread, one look at a time: an Everything busy
            // with a query takes up to a second to say whether it is ready,
            // and the window would wait with it at every tick.
            if (_askingEverything)
            {
                return;
            }

            bool ready;
            _askingEverything = true;
            try
            {
                ready = await Task.Run(() => SearchEngine.EverythingReady);
            }
            finally
            {
                _askingEverything = false;
            }

            if (ready && _waitingForEverything.IsEnabled && IsOpen && _source == SearchSource.Walk)
            {
                _waitingForEverything.Stop();
                _ = RunAsync();
            }
        };

        _folderSettling = new DispatcherTimer(DispatcherPriority.Background) { Interval = FolderSettle };
        _folderSettling.Tick += (_, _) =>
        {
            _folderSettling.Stop();
            if (IsOpen && !string.Equals(_seenFolder, _here, StringComparison.OrdinalIgnoreCase))
            {
                // A walk is not made again for the folder: that threw away all
                // it had found and walked every drive again from the start -
                // at every stop while browsing, so it never got to the end.
                // What it has found is ordered for the folder instead, and so
                // is what it goes on to find.  One stopped at its limit holds
                // only part of what there is, found from the folder before,
                // and is made again from this one.
                if (_shown is { Source: SearchSource.Walk } shown && shown.Hits.Count < SearchEngine.WalkLimit && _shownQuery is { } query)
                {
                    _here = SearchRanking.Normalize(HereNow());
                    _seenFolder = _here;
                    Apply(shown, query);

                    // Made again, the search went to an Everything that had
                    // become ready meanwhile; this one still goes to it, once
                    // it is.
                    if (!shown.EverythingFailed)
                    {
                        _waitingForEverything.Start();
                    }
                }
                else
                {
                    _ = RunAsync();
                }
            }
        };

        SearchNowCommand = new RelayCommand(SearchNow);
        CloseCommand = new RelayCommand(Close);
        RevealCommand = new RelayCommand<SearchResultViewModel>(result => _ = RevealAsync(result));
        OpenCommand = new RelayCommand<SearchResultViewModel>(Open);
        GetEverythingCommand = new RelayCommand(() => NativeShellService.Open("https://www.voidtools.com/"));
    }

    /// <summary>The folder a search is made from; set by the window, which knows what is selected and in view.</summary>
    public Func<string?>? HereFolder { get; set; }

    public ICommand SearchNowCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand GetEverythingCommand { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? string.Empty))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_text))
            {
                Close();
                return;
            }

            IsOpen = true;
            _typing.Stop();
            _typing.Start();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The results and their group headings, in order.</summary>
    public IReadOnlyList<ISearchRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>The results alone, in order.</summary>
    public IReadOnlyList<SearchResultViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public SearchResultViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>"58 results", for the panel's heading.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Which engine answered and how fast, or why it is walking.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>What the panel says when nothing was found.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set => SetProperty(ref _emptyText, value);
    }

    /// <summary>Whether the last results came from walking the folders, for the hint to install Everything.</summary>
    public bool IsWalking => _source == SearchSource.Walk;

    /// <summary>Whether Everything is not installed, so the panel can offer it.</summary>
    public bool OffersEverything => IsWalking && !SearchEngine.EverythingInstalled;

    public string Placeholder => "Search everywhere";

    /// <summary>
    /// Told whenever the folder the search would be made from may have
    /// changed - the selection, the camera - with what it is now.  A folder
    /// the user went to has the results ordered for it once it settles; one
    /// the canvas went to for a result is only remembered.
    /// </summary>
    public void NoteFolder(string? folder)
    {
        if (!IsOpen)
        {
            return;
        }

        folder = SearchRanking.Normalize(folder);
        if (string.Equals(folder, _seenFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _seenFolder = folder;
        if (Stopwatch.GetTimestamp() < _quietUntil)
        {
            return;
        }

        _folderSettling.Stop();
        _folderSettling.Start();
    }

    /// <summary>Starts the search now, without waiting for the pause after typing.</summary>
    public void SearchNow()
    {
        _typing.Stop();
        if (string.IsNullOrWhiteSpace(_text))
        {
            return;
        }

        IsOpen = true;
        _ = RunAsync();
    }

    /// <summary>Puts the panel away and forgets the text.</summary>
    public void Close()
    {
        _typing.Stop();
        _revealing.Stop();
        _waitingForEverything.Stop();
        _folderSettling.Stop();
        _run?.Cancel();
        _run = null;
        _here = null;
        _seenFolder = null;
        IsBusy = false;
        IsOpen = false;
        if (_text.Length > 0)
        {
            _text = string.Empty;
            OnPropertyChanged(nameof(Text));
        }

        Selected = null;
        Rows = [];
        Results = [];
        _byPath = new Dictionary<string, SearchResultViewModel>(StringComparer.OrdinalIgnoreCase);
        _shown = null;
        _shownQuery = null;
        Summary = string.Empty;
        Status = string.Empty;
        EmptyText = string.Empty;
    }

    /// <summary>
    /// Moves the selection <paramref name="delta"/> results on - past the
    /// headings, stopping at either end - and has the canvas follow once the
    /// keys pause.
    /// </summary>
    public void MoveSelection(int delta)
    {
        var results = _results;
        if (results.Count == 0)
        {
            return;
        }

        var index = _selected is null ? -1 : IndexOf(results, _selected);
        var next = index < 0
            ? (delta > 0 ? 0 : results.Count - 1)
            : Math.Clamp(index + delta, 0, results.Count - 1);
        if (next == index)
        {
            return;
        }

        Selected = results[next];
        _revealing.Stop();
        _revealing.Start();
    }

    /// <summary>Shows the selected result - or the first, when none is - on the canvas now.</summary>
    public Task RevealSelectedAsync()
    {
        _revealing.Stop();
        var result = _selected ?? (_results.Count > 0 ? _results[0] : null);
        return RevealAsync(result);
    }

    /// <summary>Opens the selected result, or the first.</summary>
    public void OpenSelected() => Open(_selected ?? (_results.Count > 0 ? _results[0] : null));

    public async Task RevealAsync(SearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        _revealing.Stop();
        _folderSettling.Stop();
        Selected = result;
        Quiet();
        try
        {
            await _reveal(result.FullPath);
        }
        finally
        {
            // The camera is still on its way when the reveal returns.
            Quiet();
        }

        void Quiet() => _quietUntil = Stopwatch.GetTimestamp() + (long)(RevealQuiet.TotalSeconds * Stopwatch.Frequency);
    }

    private void Open(SearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        Selected = result;
        _open(result.FullPath, result.IsDirectory);
    }

    private static int IndexOf(IReadOnlyList<SearchResultViewModel> results, SearchResultViewModel result)
    {
        for (var index = 0; index < results.Count; index++)
        {
            if (ReferenceEquals(results[index], result))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task RunAsync()
    {
        if (_disposed)
        {
            return;
        }

        var query = SearchQuery.Parse(_text);
        if (query.IsEmpty)
        {
            Close();
            return;
        }

        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        var token = run.Token;
        _shown = null;
        _shownQuery = null;

        _here = SearchRanking.Normalize(HereNow());
        _seenFolder = _here;
        _folderSettling.Stop();
        IsBusy = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var from = _here;
        try
        {
            // Started off this thread: before its first wait the engine asks
            // Everything whether it is ready - up to a second when it is busy -
            // and looks at the drives, and this is the window's thread, at
            // every key.  A walk orders what it finds from the folder the
            // results are ordered from now, which going to another one while
            // it runs changes (see the folder settling).
            await Task.Run(
                () => _engine.RunAsync(
                    query,
                    from,
                    snapshot => dispatcher.InvokeAsync(
                        () =>
                        {
                            if (!token.IsCancellationRequested && ReferenceEquals(_run, run))
                            {
                                Apply(snapshot, query);
                            }
                        },
                        DispatcherPriority.Input),
                    token,
                    () => Volatile.Read(ref _here)),
                token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (ReferenceEquals(_run, run))
            {
                Status = $"The search failed: {exception.Message}";
                IsBusy = false;
            }
        }
    }

    /// <summary>The folder a search is made from now, as the window says it.</summary>
    private string? HereNow()
    {
        try
        {
            return HereFolder?.Invoke();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Puts a snapshot of the results on screen, keeping what was selected when it is still there.</summary>
    private void Apply(SearchSnapshot snapshot, SearchQuery query)
    {
        // A walk's results ordered from a folder the results are no longer
        // ordered from - sent before the walk heard of the move, or its last,
        // sent before the move - are ordered again for the one they are.
        if (snapshot.Source == SearchSource.Walk && !string.Equals(snapshot.Folder, _here, StringComparison.OrdinalIgnoreCase))
        {
            snapshot = SearchEngine.OrderedFrom(snapshot, query, _here);
        }

        var rows = new List<ISearchRow>(snapshot.Hits.Count + 2);
        var results = new List<SearchResultViewModel>(snapshot.Hits.Count);
        var byPath = new Dictionary<string, SearchResultViewModel>(snapshot.Hits.Count, StringComparer.OrdinalIgnoreCase);
        var here = _here;
        var hereName = here is null ? null : FolderName(here);
        var hereShown = snapshot.Hits.Count(hit => hit.Place != SearchPlace.Elsewhere);
        var elsewhereShown = snapshot.Hits.Count - hereShown;

        if (here is not null)
        {
            rows.Add(new SearchHeaderRow(
                $"In {hereName}",
                CountText(hereShown, snapshot.HereTotal, snapshot.IsFinal),
                here,
                SearchPlace.Here));
        }

        var elsewhereHeaderAdded = false;
        foreach (var hit in snapshot.Hits)
        {
            if (hit.Place == SearchPlace.Elsewhere && !elsewhereHeaderAdded)
            {
                rows.Add(new SearchHeaderRow(
                    here is null ? "Everywhere" : "Everywhere else",
                    CountText(elsewhereShown, snapshot.ElsewhereTotal, snapshot.IsFinal),
                    null,
                    SearchPlace.Elsewhere));
                elsewhereHeaderAdded = true;
            }

            // The row of the search before is kept - its icon, its selection -
            // only while it says what this one would: the same group, the
            // parts this query matches, the same place from the folder
            // searched now, the same size and date.
            var segments = Segments(hit, query);
            var location = LocationOf(hit, here, hereName);
            if (!_byPath.TryGetValue(hit.FullPath, out var result)
                || result.Place != hit.Place
                || result.Location != location
                || result.Size != hit.Size
                || result.Modified != hit.Modified
                || !result.NameSegments.SequenceEqual(segments))
            {
                result = new SearchResultViewModel(
                    hit.Name,
                    hit.FullPath,
                    hit.Directory,
                    hit.IsFolder,
                    hit.Size,
                    hit.Modified,
                    hit.Place,
                    segments,
                    location);
                if (_icons.GetCached(hit.FullPath, hit.IsFolder) is { } icon)
                {
                    result.Icon = icon;
                }
                else
                {
                    var target = result;
                    _icons.Request(hit.FullPath, hit.IsFolder, found => target.Icon = found);
                }
            }

            byPath[hit.FullPath] = result;
            rows.Add(result);
            results.Add(result);
        }

        if (!elsewhereHeaderAdded && here is not null && snapshot.IsFinal)
        {
            rows.Add(new SearchHeaderRow("Everywhere else", CountText(0, snapshot.ElsewhereTotal, true), null, SearchPlace.Elsewhere));
        }

        var selectedPath = _selected?.FullPath;
        _byPath = byPath;
        _source = snapshot.Source;
        _shown = snapshot;
        _shownQuery = query;
        Rows = rows;
        Results = results;
        Selected = selectedPath is not null && byPath.TryGetValue(selectedPath, out var kept)
            ? kept
            : results.Count > 0 ? results[0] : null;

        var total = snapshot.HereTotal + snapshot.ElsewhereTotal;
        Summary = total == 0 && snapshot.IsFinal
            ? "No results"
            : total == 1 ? "1 result" : $"{total:N0} results";
        Status = snapshot.Status;
        EmptyText = results.Count == 0
            ? snapshot.IsFinal ? $"Nothing named like “{query.Text}” on any drive." : "Searching…"
            : string.Empty;
        IsBusy = !snapshot.IsFinal;
        OnPropertyChanged(nameof(IsWalking));
        OnPropertyChanged(nameof(OffersEverything));

        if (snapshot.IsFinal && snapshot.Source == SearchSource.Walk && !snapshot.EverythingFailed)
        {
            _waitingForEverything.Start();
        }
    }

    /// <summary>"12", or "400 of 1,234" when there are more than are shown.</summary>
    private static string CountText(int shown, long total, bool final)
    {
        total = Math.Max(total, shown);
        if (total > shown)
        {
            return $"{shown:N0} of {total:N0}";
        }

        return final || shown > 0 ? $"{shown:N0}" : "…";
    }

    private static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        var slash = trimmed.LastIndexOf('\\');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// Where a result is, for its second line: under the folder searched
    /// from, the path from that folder's name down (Downloads\Photos); a
    /// result right in it, the folder's name; anywhere else, the full folder.
    /// Long ones lose their middle, never their ends.
    /// </summary>
    private static string LocationOf(SearchHit hit, string? here, string? hereName)
    {
        string location;
        if (here is not null && hereName is not null && hit.Place != SearchPlace.Elsewhere)
        {
            var directory = hit.Directory.TrimEnd('\\');
            var inside = directory.Length > here.TrimEnd('\\').Length
                ? directory[(here.TrimEnd('\\').Length + 1)..]
                : string.Empty;
            location = inside.Length == 0 ? hereName : $"{hereName}\\{inside}";
        }
        else
        {
            location = hit.Directory;
        }

        const int longest = 90;
        if (location.Length <= longest)
        {
            return location;
        }

        const int head = 28;
        return string.Concat(location.AsSpan(0, head), "…", location.AsSpan(location.Length - (longest - head - 1)));
    }

    /// <summary>
    /// The name in pieces: Everything's own highlighting when it sent one,
    /// where the matched parts sit between asterisks, otherwise where the
    /// words of the search occur.
    /// </summary>
    internal static IReadOnlyList<TextSegment> Segments(SearchHit hit, SearchQuery query)
    {
        if (hit.Highlighted is { } highlighted && highlighted.Replace("*", string.Empty) == hit.Name)
        {
            var segments = new List<TextSegment>();
            var match = false;
            foreach (var part in highlighted.Split('*'))
            {
                if (part.Length > 0)
                {
                    segments.Add(new TextSegment(part, match));
                }

                match = !match;
            }

            return segments;
        }

        var spans = query.Highlights(hit.Name);
        if (spans.Count == 0)
        {
            return [new TextSegment(hit.Name, false)];
        }

        var pieces = new List<TextSegment>();
        var at = 0;
        foreach (var (start, length) in spans)
        {
            if (start > at)
            {
                pieces.Add(new TextSegment(hit.Name[at..start], false));
            }

            pieces.Add(new TextSegment(hit.Name.Substring(start, length), true));
            at = start + length;
        }

        if (at < hit.Name.Length)
        {
            pieces.Add(new TextSegment(hit.Name[at..], false));
        }

        return pieces;
    }

    public void Dispose()
    {
        _disposed = true;
        _folderSettling.Stop();
        _typing.Stop();
        _revealing.Stop();
        _waitingForEverything.Stop();
        _run?.Cancel();
    }
}
