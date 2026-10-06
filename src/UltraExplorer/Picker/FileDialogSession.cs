using System.Collections.ObjectModel;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;

namespace UltraExplorer.Picker;

public enum FileDialogActionKind
{
    /// <summary>Nothing usable was typed or selected.</summary>
    None,

    /// <summary>Open this folder instead of returning it.</summary>
    Navigate,

    /// <summary>A wildcard was typed: re-filter the view.</summary>
    Filter,

    /// <summary>Return these paths to the caller, once they validate.</summary>
    Accept
}

public sealed record FileDialogAction(
    FileDialogActionKind Kind,
    IReadOnlyList<string> Paths,
    string Pattern = "",
    string Folder = "")
{
    public static readonly FileDialogAction None = new(FileDialogActionKind.None, []);
}

/// <summary>
/// The picker's own state: which file types are offered, which one is selected,
/// what is typed in the name box, where the view is, and what OK means right
/// now.  It holds no reference to the window, so every rule below is exercised
/// by the headless harness.
/// </summary>
public sealed class FileDialogSession : ObservableObject
{
    private readonly FileDialogClientStore _clients;
    private readonly HashSet<FileDialogGate> _resolved = [];

    private int _selectedFilterIndex;
    private string _fileNameText = string.Empty;
    private string? _selectionNameText;
    private IReadOnlyList<string> _pendingSelection = [];
    private string _currentFolder = string.Empty;
    private bool _hasInteracted;
    private FileDialogFilter _selectedFilter;

    /// <summary>
    /// A wildcard typed into the name box, which overrides the selected file
    /// type until the user picks one from the list again.
    /// </summary>
    private FileDialogFilter? _typedFilter;

    public FileDialogSession(FileDialogRequest request, FileDialogClientStore? clients = null)
    {
        Request = request.Normalize();
        _clients = clients ?? new FileDialogClientStore();

        foreach (var spec in Request.Filters)
        {
            Filters.Add(spec);
        }

        var remembered = Request.Has(FileDialogOptions.DontAddToRecent)
            ? null
            : _clients.Load(Request.ClientGuid);

        _selectedFilterIndex = Math.Clamp(Request.FileTypeIndex - 1, 0, Math.Max(0, Filters.Count - 1));
        if (remembered is not null
            && Request.FileTypeIndex == 1
            && remembered.FileTypeIndex >= 1
            && remembered.FileTypeIndex <= Filters.Count)
        {
            _selectedFilterIndex = remembered.FileTypeIndex - 1;
        }

        _selectedFilter = BuildFilter(_selectedFilterIndex);
        _fileNameText = Request.FileName;
        _currentFolder = ResolveStartFolder(remembered);

        foreach (var name in remembered?.RecentNames ?? [])
        {
            RecentNames.Add(name);
        }
    }

    /// <summary>Names this caller chose before, offered by the name box.</summary>
    public ObservableCollection<string> RecentNames { get; } = [];

    public FileDialogRequest Request { get; }

    public ObservableCollection<FileDialogFilterSpec> Filters { get; } = [];

    /// <summary>Raised when the set of entries the graph should show changes.</summary>
    public event Action? FilterChanged;

    /// <summary>Raised when the canvas moves to another folder.</summary>
    public event Action? FolderChanged;

    /// <summary>Raised when what is highlighted on the canvas changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// A caller's last word on what it is about to be handed.  Returning false
    /// leaves the dialog open, which is what <c>IFileDialogEvents::OnFileOk</c>
    /// is for.
    /// </summary>
    public Func<IReadOnlyList<string>, bool>? AcceptGuard { get; set; }

    /// <summary>
    /// A caller's answer to replacing a file: true to allow, false to refuse,
    /// null to ask the user.  <c>IFileDialogEvents::OnOverwrite</c>.
    /// </summary>
    public Func<string, bool?>? OverwriteGuard { get; set; }

    /// <summary>Zero-based for the combo box; the caller's index is one-based.</summary>
    public int SelectedFilterIndex
    {
        get => _selectedFilterIndex;
        set
        {
            var clamped = Filters.Count == 0 ? 0 : Math.Clamp(value, 0, Filters.Count - 1);
            var previous = CurrentFilter;
            var hadTypedFilter = _typedFilter is not null;
            _typedFilter = null;
            if (!SetProperty(ref _selectedFilterIndex, clamped) && !hadTypedFilter)
            {
                return;
            }

            _selectedFilter = BuildFilter(clamped);
            OnPropertyChanged(nameof(CurrentFilter));

            if (Request.IsSave)
            {
                FileNameText = SwapExtension(FileNameText, previous, CurrentFilter);
            }

            FilterChanged?.Invoke();
        }
    }

    /// <summary>The one-based index handed back to the caller.</summary>
    public int FileTypeIndex => _selectedFilterIndex + 1;

    public FileDialogFilter CurrentFilter => _typedFilter ?? _selectedFilter;

    /// <summary>
    /// Typing <c>*.log</c> into the name box filters the view, exactly as the
    /// standard dialog has always done.  Choosing a file type undoes it.
    /// </summary>
    public void ApplyTypedPattern(string pattern)
    {
        _typedFilter = FileDialogFilter.Parse(pattern);
        FileNameText = string.Empty;
        MarkInteraction();
        OnPropertyChanged(nameof(CurrentFilter));
        FilterChanged?.Invoke();
    }

    /// <summary>Adds a chosen name to the box's drop-down history.</summary>
    public void RememberName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmed = name.Trim();
        var existing = RecentNames.IndexOf(trimmed);
        if (existing >= 0)
        {
            RecentNames.Move(existing, 0);
            return;
        }

        RecentNames.Insert(0, trimmed);
        while (RecentNames.Count > 12)
        {
            RecentNames.RemoveAt(RecentNames.Count - 1);
        }
    }

    public string FileNameText
    {
        get => _fileNameText;
        set
        {
            if (SetProperty(ref _fileNameText, value ?? string.Empty))
            {
                _selectionNameText = null;
                SetPendingSelection([]);
                OnPropertyChanged(nameof(CanAccept));
            }
        }
    }

    /// <summary>
    /// Whether the name box holds a name of the user's own: not empty, and
    /// put there neither by a selection nor by the dialog as it opened.
    /// </summary>
    internal bool HasTypedName => _selectionNameText is null && _fileNameText.Trim().Length > 0
        && !string.Equals(_fileNameText, Request.FileName, StringComparison.Ordinal);

    /// <summary>The folder the canvas is showing, which bare names resolve against.</summary>
    public string CurrentFolder
    {
        get => _currentFolder;
        set
        {
            if (SetProperty(ref _currentFolder, value ?? string.Empty))
            {
                FolderChanged?.Invoke();
            }
        }
    }

    public string Title => Request.EffectiveTitle;

    public string OkLabel => Request.EffectiveOkLabel;

    public string FileNameLabel => Request.EffectiveFileNameLabel;

    public bool PicksFolders => Request.PicksFolders;

    public bool AllowsMultipleSelection => Request.AllowsMultipleSelection;

    public bool ShowsFileTypes => !PicksFolders && Filters.Count > 0;

    /// <summary>
    /// <see cref="FileDialogOptions.OkButtonNeedsInteraction"/>: the button stays
    /// disabled until the user has actually touched something, so a dialog that
    /// opens with a name already filled in cannot be dismissed by a stray Enter.
    /// </summary>
    public bool HasInteracted
    {
        get => _hasInteracted;
        private set
        {
            if (SetProperty(ref _hasInteracted, value))
            {
                OnPropertyChanged(nameof(CanAccept));
            }
        }
    }

    public bool CanAccept
    {
        get
        {
            if (Request.Has(FileDialogOptions.OkButtonNeedsInteraction) && !HasInteracted)
            {
                return false;
            }

            // Selecting the folder you are already in is always an option.
            return PicksFolders || FileNameText.Trim().Length > 0;
        }
    }

    public void MarkInteraction() => HasInteracted = true;

    /// <summary>
    /// The entry filter the graph should apply: none while a folder is being
    /// picked, otherwise the selected file type.
    /// </summary>
    public FileDialogFilter GraphFilter => PicksFolders ? FileDialogFilter.MatchAll : CurrentFilter;

    /// <summary>
    /// The last usable selection, kept as a plain snapshot so a caller on
    /// another thread can read it without touching the canvas.
    /// </summary>
    public IReadOnlyList<string> LastSelection { get; private set; } = [];

    /// <summary>The file choice awaiting OK, independent of the canvas highlight.</summary>
    public IReadOnlyList<string> PendingSelection => _pendingSelection;

    public bool HasPendingSelection => _pendingSelection.Count > 0;

    public string PendingSelectionLabel => _pendingSelection.Count switch
    {
        0 => string.Empty,
        1 => $"Selected {(PicksFolders ? "folder" : "file")}: {NameOrPath(_pendingSelection[0])}",
        _ => $"Selected files ({_pendingSelection.Count})"
    };

    /// <summary>
    /// Where the one choice is (nothing for a drive, which is in no folder);
    /// of several, the first <see cref="ListedChoices"/> and how many more.
    /// Thousands chosen at once (Ctrl+A over photos to upload) were each laid
    /// out in the footer's wrapping text on every further click; the tip over
    /// it still names them all.
    /// </summary>
    public string PendingSelectionLocation => _pendingSelection.Count switch
    {
        1 => Path.GetDirectoryName(_pendingSelection[0]) is { Length: > 0 } parent ? $"Location: {parent}" : string.Empty,
        <= ListedChoices => string.Join(Environment.NewLine, _pendingSelection),
        _ => string.Join(Environment.NewLine, _pendingSelection.Take(ListedChoices))
            + Environment.NewLine + $"and {_pendingSelection.Count - ListedChoices:N0} more"
    };

    /// <summary>How many of several chosen paths the footer lists.</summary>
    private const int ListedChoices = 10;

    /// <summary>A path's own name, or the whole path for a drive, which has none.</summary>
    private static string NameOrPath(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    public string PendingSelectionDetails => string.Join(Environment.NewLine, _pendingSelection);

    private void SetPendingSelection(IReadOnlyList<string> paths)
    {
        _pendingSelection = paths.ToArray();
        OnPropertyChanged(nameof(PendingSelection));
        OnPropertyChanged(nameof(HasPendingSelection));
        OnPropertyChanged(nameof(PendingSelectionLabel));
        OnPropertyChanged(nameof(PendingSelectionLocation));
        OnPropertyChanged(nameof(PendingSelectionDetails));
    }

    public void ClearChoice()
    {
        _selectionNameText = null;
        LastSelection = [];
        FileNameText = string.Empty;
        SetPendingSelection([]);
    }

    /// <summary>Contract completion for the same dialog retains a choice even
    /// after navigation removed its highlight. A new request never calls this.</summary>
    internal void CarryChoiceFrom(FileDialogSession previous)
    {
        if (previous._selectionNameText is not { } name || previous.PendingSelection.Count == 0
            || !string.Equals(FileNameText.Trim(), name, StringComparison.Ordinal)) return;
        var paths = AllowsMultipleSelection ? previous.PendingSelection : [previous.PendingSelection[0]];
        FileNameText = FileDialogNaming.Describe(paths);
        SetPendingSelection(paths);
        _selectionNameText = FileNameText.Trim();
    }

    /// <summary>Reflects a canvas selection in the name box.</summary>
    public void ReportSelection(IReadOnlyList<string> paths)
    {
        var usable = paths
            .Where(path => PicksFolders ? Directory.Exists(path) : !Directory.Exists(path))
            .ToArray();

        ReportUsableSelection(usable);
    }

    /// <summary>The tile canvas already knows which selected entries are
    /// folders. Avoid a synchronous filesystem probe on each selection click.</summary>
    public void ReportSelection(IEnumerable<SelectionItem> items)
    {
        var usable = items.Where(item => PicksFolders == item.IsDirectory)
            .Select(item => item.Path).ToArray();
        ReportUsableSelection(usable);
    }

    private void ReportUsableSelection(string[] usable)
    {
        LastSelection = usable;

        if (usable.Length == 0)
        {
            SelectionChanged?.Invoke();
            return;
        }

        MarkInteraction();
        FileNameText = FileDialogNaming.Describe(
            AllowsMultipleSelection ? usable : [usable[0]]);
        SetPendingSelection(AllowsMultipleSelection ? usable : [usable[0]]);
        _selectionNameText = FileNameText.Trim();
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Decides what pressing OK, or double-clicking, should do now.  Without
    /// <paramref name="readDisk"/> - the disk did not answer in time - every
    /// name is taken to be what the dialog asks for, and the application's
    /// own dialog, which checks it again, decides.
    /// </summary>
    public FileDialogAction Prepare(IReadOnlyList<string> selection, bool readDisk = true)
    {
        bool IsFolder(string path) => readDisk ? Directory.Exists(path) : PicksFolders;

        var text = FileNameText.Trim();
        if (text.Length > 0)
        {
            // Selection labels are display names, not newly typed relative
            // paths. Keep their exact locations even while the asynchronous
            // navigation/focus catches up or when two folders contain the
            // same basename. Editing the name clears this provenance.
            if (_selectionNameText == text && PendingSelection.Count > 0)
            {
                // A chosen shortcut to a folder opens the folder.
                if (readDisk && !PicksFolders && PendingSelection.Count == 1 && FolderBehindLink(PendingSelection[0]) is { } linked)
                    return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: linked);
                return new FileDialogAction(FileDialogActionKind.Accept,
                    AllowsMultipleSelection ? PendingSelection : [PendingSelection[0]]);
            }
            return PrepareTyped(text, IsFolder, readDisk);
        }

        if (selection.Count > 0)
        {
            if (!PicksFolders && selection.Count == 1 && IsFolder(selection[0]))
            {
                return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: selection[0]);
            }

            var usable = selection
                .Where(path => PicksFolders ? IsFolder(path) : !IsFolder(path))
                .ToArray();

            if (usable.Length > 0)
            {
                return new FileDialogAction(
                    FileDialogActionKind.Accept,
                    AllowsMultipleSelection ? usable : [usable[0]]);
            }
        }

        // "Select Folder" with nothing highlighted means the folder in view.
        return PicksFolders && IsFolder(CurrentFolder)
            ? new FileDialogAction(FileDialogActionKind.Accept, [CurrentFolder])
            : FileDialogAction.None;
    }

    /// <summary>
    /// The folder <paramref name="path"/> leads to when it is a shortcut to
    /// one: opening it opens that folder, in an Open and a Save dialog alike,
    /// as in the standard dialog.  Null for anything else, and for a caller
    /// that asked for shortcuts themselves.
    /// </summary>
    public string? FolderBehindLink(string path)
    {
        if (Request.Has(FileDialogOptions.NoDereferenceLinks) || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = ShellLinkResolver.Resolve(path);
        return !string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target) ? target : null;
    }

    private FileDialogAction PrepareTyped(string text, Func<string, bool> isFolder, bool readDisk)
    {
        if (FileDialogFilter.LooksLikePattern(text))
        {
            var folder = string.Empty;
            var pattern = text;
            var separator = text.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (separator > 0)
            {
                folder = FileDialogNaming.ToFullPath(text[..separator], CurrentFolder) ?? string.Empty;
                pattern = text[(separator + 1)..];
            }

            return new FileDialogAction(FileDialogActionKind.Filter, [], pattern, folder);
        }

        // Each path is kept with the name it was typed as: a name that makes
        // no path is dropped, and the first path's own name is needed below.
        var typed = new List<string>();
        var paths = new List<string>();
        foreach (var name in FileDialogNaming.SplitTypedNames(text))
        {
            if (FileDialogNaming.ToFullPath(name, CurrentFolder) is { } path)
            {
                typed.Add(name);
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            return FileDialogAction.None;
        }

        if (paths.Count == 1 && !PicksFolders)
        {
            if (isFolder(paths[0]))
            {
                return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: paths[0]);
            }

            if (readDisk && FolderBehindLink(paths[0]) is { } linked)
            {
                return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: linked);
            }
        }

        // Windows' GetFullPath drops a trailing dot, so "report." has become
        // "...\report" by now; the dot that asks for no extension is read
        // from what was typed instead.
        if (Request.IsSave && !Request.IsNativeProxy && !FileDialogNaming.AsksForNoExtension(typed[0]))
        {
            var directory = Path.GetDirectoryName(paths[0]) ?? CurrentFolder;
            var name = FileDialogNaming.ApplyDefaultExtension(
                Path.GetFileName(paths[0]),
                Request.DefaultExtension,
                CurrentFilter);
            paths[0] = Path.Combine(directory, name);
        }

        // A replaced dialog hands the dot back: it is how the application's
        // own dialog is told not to add an extension of its own.
        if (Request.IsSave && Request.IsNativeProxy && FileDialogNaming.AsksForNoExtension(typed[0]))
        {
            paths[0] += ".";
        }

        return new FileDialogAction(
            FileDialogActionKind.Accept,
            AllowsMultipleSelection ? paths : [paths[0]]);
    }

    public FileDialogVerdict Validate(IReadOnlyList<string> paths) =>
        FileDialogValidator.Evaluate(Request, CurrentFilter, paths, _resolved);

    /// <summary>Records a yes so the same question is not asked again.</summary>
    public void Allow(FileDialogGate gate) => _resolved.Add(gate);

    public void ForgetAnswers() => _resolved.Clear();

    /// <summary>Stores where this caller ended up, unless it asked us not to.</summary>
    public void Remember(string folder)
    {
        if (Request.ClientGuid == Guid.Empty
            || Request.Has(FileDialogOptions.DontAddToRecent)
            || string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _clients.Save(
            Request.ClientGuid,
            new FileDialogClientState(folder, FileTypeIndex, [.. RecentNames]));
    }

    public FileDialogResult Accepted(IReadOnlyList<string> paths) =>
        new(true, paths, FileTypeIndex);

    public FileDialogResult Cancelled() => FileDialogResult.Cancelled(FileTypeIndex);

    private FileDialogFilter BuildFilter(int index) =>
        index >= 0 && index < Filters.Count
            ? FileDialogFilter.Parse(Filters[index].Pattern)
            : FileDialogFilter.MatchAll;

    /// <summary>
    /// Changing the file type of a save retypes the name, the way the standard
    /// dialog does: "report.png" becomes "report.jpg" rather than growing a
    /// second extension.
    /// </summary>
    private string SwapExtension(string name, FileDialogFilter previous, FileDialogFilter current)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || current.PreferredExtension is not { Length: > 0 } extension)
        {
            return name;
        }

        var currentExtension = Path.GetExtension(trimmed);
        if (currentExtension.Length == 0)
        {
            return trimmed;
        }

        // Only a name that matched the type being left is retyped; a name the
        // user spelled out deliberately is left alone.
        if (!previous.MatchesEverything && !previous.Matches(Path.GetFileName(trimmed)))
        {
            return trimmed;
        }

        // Leaving All Files, only an extension one of the dialog's own types
        // names is retyped: "Report 01.10.2026" keeps its ".2026", which is
        // part of the name, and has the new type's extension added.
        if (previous.MatchesEverything && !Filters.Any(spec => FileDialogFilter.Parse(spec.Pattern) is { MatchesEverything: false } type
            && type.Matches(Path.GetFileName(trimmed))))
        {
            return $"{trimmed}.{extension}";
        }

        return Path.ChangeExtension(trimmed, extension);
    }

    private string ResolveStartFolder(FileDialogClientState? remembered)
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            // A replaced dialog's folder is the one it shows, as it is: a
            // folder may be named "%OS%", and expanded it would be another.
            var expanded = Request.IsNativeProxy ? candidate : Environment.ExpandEnvironmentVariables(candidate);
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            // A file was named: start in the folder holding it.
            var parent = Path.GetDirectoryName(expanded);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                return Path.GetFullPath(parent);
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        IEnumerable<string?> Candidates()
        {
            yield return Request.InitialFolder;
            yield return Request.SaveAsItem;
            yield return remembered?.Folder;
            yield return Request.DefaultFolder;
            yield return Request.FileName is { Length: > 0 } name && Path.IsPathFullyQualified(name)
                ? name
                : null;
        }
    }
}
