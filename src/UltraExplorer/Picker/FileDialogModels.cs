namespace UltraExplorer.Picker;

/// <summary>
/// What the caller is asking for.  The Windows common dialog expresses the same
/// three jobs through one interface plus <see cref="FileDialogOptions.PickFolders"/>;
/// keeping them as an enum makes every switch in the picker exhaustive.
/// </summary>
public enum FileDialogMode
{
    Open,
    Save,
    PickFolder
}

/// <summary>
/// <c>FILEOPENDIALOGOPTIONS</c> from <c>shobjidl_core.h</c>, value for value, so
/// a caller can pass the mask it already has for <c>IFileDialog::SetOptions</c>
/// straight through to us.
/// </summary>
[Flags]
public enum FileDialogOptions : uint
{
    None = 0,

    /// <summary>Saving over an existing file asks first.</summary>
    OverwritePrompt = 0x2,

    /// <summary>Only names matching the selected file type are acceptable.</summary>
    StrictFileTypes = 0x4,

    /// <summary>Do not change the caller's working directory.</summary>
    NoChangeDir = 0x8,

    /// <summary>Pick a folder rather than a file.</summary>
    PickFolders = 0x20,

    /// <summary>Only items with a real file-system path may be returned.</summary>
    ForceFileSystem = 0x40,

    /// <summary>The opposite: allow items that are not storage at all.</summary>
    AllNonStorageItems = 0x80,

    /// <summary>Return whatever was typed without checking it.</summary>
    NoValidate = 0x100,

    AllowMultiSelect = 0x200,

    PathMustExist = 0x800,

    FileMustExist = 0x1000,

    /// <summary>Offer to create a file that does not exist yet.</summary>
    CreatePrompt = 0x2000,

    /// <summary>Ask what to do when a file is locked by someone else.</summary>
    ShareAware = 0x4000,

    /// <summary>Refuse a read-only file, and refuse a write-protected folder.</summary>
    NoReadOnlyReturn = 0x8000,

    /// <summary>Skip the create-then-delete probe that proves the file is writable.</summary>
    NoTestFileCreate = 0x10000,

    HideMruPlaces = 0x20000,

    HidePinnedPlaces = 0x40000,

    /// <summary>Return the shortcut itself instead of what it points at.</summary>
    NoDereferenceLinks = 0x100000,

    /// <summary>OK stays disabled until the user actually touches the selection.</summary>
    OkButtonNeedsInteraction = 0x200000,

    DontAddToRecent = 0x2000000,

    /// <summary>Show hidden and system items regardless of the Explorer setting.</summary>
    ForceShowHidden = 0x10000000,

    DefaultNoMiniMode = 0x20000000,

    ForcePreviewPaneOn = 0x40000000,

    SupportStreamableItems = 0x80000000
}

/// <summary>One entry of the "Files of type" list: a label and its patterns.</summary>
public sealed record FileDialogFilterSpec(string Name, string Pattern)
{
    /// <summary>What the combo box shows — Windows appends the patterns itself.</summary>
    public string DisplayText =>
        Name.Contains('(') || string.IsNullOrWhiteSpace(Pattern)
            ? Name
            : $"{Name} ({Pattern})";

    /// <summary>
    /// A record prints its own fields by default, and a ComboBox that falls
    /// back to ToString would show that.  The label is the only sensible text.
    /// </summary>
    public override string ToString() => DisplayText;
}

/// <summary>A shortcut the caller pinned into the sidebar via <c>AddPlace</c>.</summary>
public sealed record FileDialogPlace(string Path, bool Top);

/// <summary>
/// Everything <c>IFileDialog</c>, <c>IFileOpenDialog</c>, <c>IFileSaveDialog</c>,
/// <c>OPENFILENAME</c> and <c>BROWSEINFO</c> can ask for, in one object.
/// </summary>
public sealed class FileDialogRequest
{
    public FileDialogMode Mode { get; set; } = FileDialogMode.Open;

    public FileDialogOptions Options { get; set; } = FileDialogOptions.PathMustExist;

    public List<FileDialogFilterSpec> Filters { get; } = [];

    /// <summary>One-based, exactly like <c>SetFileTypeIndex</c>.</summary>
    public int FileTypeIndex { get; set; } = 1;

    public string Title { get; set; } = string.Empty;

    public string OkButtonLabel { get; set; } = string.Empty;

    public string FileNameLabel { get; set; } = string.Empty;

    /// <summary>Pre-filled name, as <c>SetFileName</c> does.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Appended when the typed name has no extension of its own.</summary>
    public string DefaultExtension { get; set; } = string.Empty;

    /// <summary>Where to open.  Wins over <see cref="DefaultFolder"/>.</summary>
    public string InitialFolder { get; set; } = string.Empty;

    /// <summary>Where to open the first time this client is seen.</summary>
    public string DefaultFolder { get; set; } = string.Empty;

    /// <summary>The item a Save As dialog is re-saving.</summary>
    public string SaveAsItem { get; set; } = string.Empty;

    public List<FileDialogPlace> Places { get; } = [];

    /// <summary>
    /// Identifies the caller so its last folder and file type come back next
    /// time, which is what <c>SetClientGuid</c> buys a real dialog.
    /// </summary>
    public Guid ClientGuid { get; set; }

    /// <summary>Owner window; the picker goes modal against it.</summary>
    public nint OwnerHandle { get; set; }

    /// <summary>
    /// <c>BIF_BROWSEINCLUDEFILES</c>: show files while picking a folder, so the
    /// user can see what is in there without being able to choose one.
    /// </summary>
    public bool ShowFilesWhilePickingFolders { get; set; }

    /// <summary><c>BIF_NONEWFOLDERBUTTON</c>.</summary>
    public bool HideNewFolderButton { get; set; }

    public bool Has(FileDialogOptions option) => (Options & option) == option;

    public bool AllowsMultipleSelection => Has(FileDialogOptions.AllowMultiSelect);

    public bool PicksFolders => Mode == FileDialogMode.PickFolder || Has(FileDialogOptions.PickFolders);

    public bool IsSave => Mode == FileDialogMode.Save;

    /// <summary>
    /// While picking a folder the file list is off unless the caller asked to
    /// see it, which is what the folder browser has always done.
    /// </summary>
    public bool ShowsFiles => !PicksFolders || ShowFilesWhilePickingFolders;

    public string EffectiveOkLabel => OkButtonLabel.Length > 0
        ? OkButtonLabel
        : Mode switch
        {
            FileDialogMode.Save => "Save",
            FileDialogMode.PickFolder => "Select Folder",
            _ => "Open"
        };

    public string EffectiveFileNameLabel => FileNameLabel.Length > 0
        ? FileNameLabel
        : PicksFolders ? "Folder:" : "File name:";

    public string EffectiveTitle => Title.Length > 0
        ? Title
        : Mode switch
        {
            FileDialogMode.Save => "Save As",
            FileDialogMode.PickFolder => "Select Folder",
            _ => "Open"
        };

    /// <summary>
    /// Normalizes the parts a caller is allowed to get wrong: an out-of-range
    /// type index, a leading dot on the default extension, a save request that
    /// insists its own new file already exists.
    /// </summary>
    public FileDialogRequest Normalize()
    {
        if (Mode == FileDialogMode.Open && Has(FileDialogOptions.PickFolders))
        {
            Mode = FileDialogMode.PickFolder;
        }

        if (Mode == FileDialogMode.PickFolder)
        {
            Options |= FileDialogOptions.PickFolders;
        }

        if (Filters.Count == 0 && !PicksFolders)
        {
            Filters.Add(new FileDialogFilterSpec("All Files", "*.*"));
        }

        FileTypeIndex = Filters.Count == 0 ? 1 : Math.Clamp(FileTypeIndex, 1, Filters.Count);
        DefaultExtension = DefaultExtension.TrimStart('.').Trim();

        // A save target does not have to exist yet; requiring it would make
        // every Save As dialog refuse the name the user just typed.
        if (Mode == FileDialogMode.Save)
        {
            Options &= ~FileDialogOptions.FileMustExist;
        }

        return this;
    }
}

/// <summary>What the picker hands back to the caller.</summary>
public sealed record FileDialogResult(
    bool Accepted,
    IReadOnlyList<string> Paths,
    int FileTypeIndex,
    string? Error = null)
{
    public static FileDialogResult Cancelled(int fileTypeIndex = 1) =>
        new(false, [], fileTypeIndex);

    public static FileDialogResult Failed(string error) =>
        new(false, [], 1, error);

    public string? FirstPath => Paths.Count > 0 ? Paths[0] : null;
}
