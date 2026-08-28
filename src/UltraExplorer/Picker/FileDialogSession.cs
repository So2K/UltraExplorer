using System.Collections.ObjectModel;
using UltraExplorer.Infrastructure;

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
                OnPropertyChanged(nameof(CanAccept));
            }
        }
    }

    /// <summary>The folder the canvas is showing, which bare names resolve against.</summary>
    public string CurrentFolder
    {
        get => _currentFolder;
        set => SetProperty(ref _currentFolder, value ?? string.Empty);
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

    /// <summary>Reflects a canvas selection in the name box.</summary>
    public void ReportSelection(IReadOnlyList<string> paths)
    {
        var usable = paths
            .Where(path => PicksFolders ? Directory.Exists(path) : !Directory.Exists(path))
            .ToArray();

        if (usable.Length == 0)
        {
            return;
        }

        MarkInteraction();
        FileNameText = FileDialogNaming.Describe(
            AllowsMultipleSelection ? usable : [usable[0]]);
    }

    /// <summary>Decides what pressing OK, or double-clicking, should do now.</summary>
    public FileDialogAction Prepare(IReadOnlyList<string> selection)
    {
        var text = FileNameText.Trim();
        if (text.Length > 0)
        {
            return PrepareTyped(text);
        }

        if (selection.Count > 0)
        {
            if (!PicksFolders && selection.Count == 1 && Directory.Exists(selection[0]))
            {
                return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: selection[0]);
            }

            var usable = selection
                .Where(path => PicksFolders ? Directory.Exists(path) : !Directory.Exists(path))
                .ToArray();

            if (usable.Length > 0)
            {
                return new FileDialogAction(
                    FileDialogActionKind.Accept,
                    AllowsMultipleSelection ? usable : [usable[0]]);
            }
        }

        // "Select Folder" with nothing highlighted means the folder in view.
        return PicksFolders && Directory.Exists(CurrentFolder)
            ? new FileDialogAction(FileDialogActionKind.Accept, [CurrentFolder])
            : FileDialogAction.None;
    }

    private FileDialogAction PrepareTyped(string text)
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

        var names = FileDialogNaming.SplitTypedNames(text);
        var paths = names
            .Select(name => FileDialogNaming.ToFullPath(name, CurrentFolder))
            .OfType<string>()
            .ToList();

        if (paths.Count == 0)
        {
            return FileDialogAction.None;
        }

        if (paths.Count == 1 && Directory.Exists(paths[0]) && !PicksFolders)
        {
            return new FileDialogAction(FileDialogActionKind.Navigate, [], Folder: paths[0]);
        }

        if (Request.IsSave)
        {
            var directory = Path.GetDirectoryName(paths[0]) ?? CurrentFolder;
            var name = FileDialogNaming.ApplyDefaultExtension(
                Path.GetFileName(paths[0]),
                Request.DefaultExtension,
                CurrentFilter);
            paths[0] = Path.Combine(directory, name);
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

            var expanded = Environment.ExpandEnvironmentVariables(candidate);
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
