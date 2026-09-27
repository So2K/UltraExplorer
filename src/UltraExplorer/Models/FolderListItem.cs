using System.Windows.Media;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services;

namespace UltraExplorer.Models;

/// <summary>
/// One line of the folder list: the same file-system object a node stands for on
/// the canvas, drawn as a row instead of a box.
/// </summary>
public sealed class FolderListItem(ViewAllEntryDescriptor entry) : ObservableObject
{
    private ViewAllEntryDescriptor _entry = entry;
    private ImageSource? _icon;
    private bool _isOnCanvas;
    private bool _isNew;
    private SortColumn _detailColumn;
    private string? _detail;

    /// <summary>
    /// What the row shows.  Replaced in place when the folder is read again
    /// and the object is still there with a new size or date: the row keeps
    /// its place, its icon and its highlight, and only the text that changed
    /// is drawn again.
    /// </summary>
    public ViewAllEntryDescriptor Entry
    {
        get => _entry;
        internal set
        {
            if (_entry == value)
            {
                return;
            }

            _entry = value;
            _detail = null;

            // Everything the row shows comes from the entry.
            OnPropertyChanged(string.Empty);
        }
    }

    public string FullPath => Entry.FullPath;
    public string DisplayName => Entry.DisplayName;
    public string SecondaryText => Entry.SecondaryText;
    public ViewAllEntryKind Kind => Entry.Kind;

    /// <summary>What the list is ordered by, which decides what <see cref="Detail"/> says.</summary>
    public SortColumn DetailColumn
    {
        get => _detailColumn;
        set
        {
            if (_detailColumn == value)
            {
                return;
            }

            _detailColumn = value;
            _detail = null;
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>
    /// What the row says on its right: the thing the list is ordered by, so
    /// the order can be read off the rows - when each was written under a
    /// date order, what kind of file it is under a type order, a file's size
    /// under a size order - and otherwise what it always said, a folder's
    /// date and a file's kind and size.  A drive always says what it is.
    /// Made when first shown and kept - all but a kind of file the Shell has
    /// not named yet, which is said again when its name is in.
    /// </summary>
    public string Detail
    {
        get
        {
            if (_detail is not null)
            {
                return _detail;
            }

            var detail = DetailFor(_entry, _detailColumn, out var standIn);
            if (standIn)
            {
                // The Shell has not named this kind yet: shown as "XYZ File"
                // for now, not kept, and shown again once the name is in.
                _ = ShowTypeNameWhenKnownAsync();
                return detail;
            }

            return _detail = detail;
        }
    }

    private bool _typeNameWaiting;

    private async Task ShowTypeNameWhenKnownAsync()
    {
        if (_typeNameWaiting)
        {
            return;
        }

        _typeNameWaiting = true;
        try
        {
            // Never straight back into the binding that is asking.
            await Task.Yield();
            await FileTypeNames.WhenPrefetchedAsync();
        }
        finally
        {
            _typeNameWaiting = false;
        }

        _detail = null;
        OnPropertyChanged(nameof(Detail));
    }

    private static string DetailFor(ViewAllEntryDescriptor entry, SortColumn column, out bool standIn)
    {
        standIn = false;
        if (entry.Kind == ViewAllEntryKind.Drive)
        {
            return entry.SecondaryText;
        }

        var isFolder = entry.Kind == ViewAllEntryKind.Folder;
        switch (column)
        {
            case SortColumn.Modified when entry.ModifiedUtc > DateTime.MinValue:
                return Infrastructure.CultureDates.Format(entry.ModifiedUtc.ToLocalTime(), "g");
            case SortColumn.Type when isFolder:
                return FileTypeNames.Folder;
            case SortColumn.Type:
                // The extension exactly as ordering by type reads it - none
                // for ".gitignore" - so the row names the kind it is ranked by.
                standIn = !FileTypeNames.TryGet(ViewAllEntryOrder.ExtensionOf(entry.DisplayName), out var name);
                return name;
            case SortColumn.Size when !isFolder && entry.SizeBytes is { } size:
                return FormatSize(size);
            default:
                return entry.SecondaryText;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{bytes:N0} B" : $"{value:0.#} {suffixes[suffix]}";
    }
    public bool IsDirectory => Kind is ViewAllEntryKind.Drive or ViewAllEntryKind.Folder;

    /// <summary>Fallback Segoe Fluent glyph, used until the Shell icon arrives.</summary>
    public string Glyph => IsDirectory ? "" : "";

    public Brush Tint => BrushCache.Get(IsDirectory ? "#E3B341" : "#5C6675");

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>
    /// Whether the canvas already holds a node for this object.  A row that is
    /// not on the canvas can still be clicked - it is revealed first - but saying
    /// so up front is the difference between "the list lies" and "the list knows
    /// more than the canvas does".
    /// </summary>
    public bool IsOnCanvas
    {
        get => _isOnCanvas;
        set => SetProperty(ref _isOnCanvas, value);
    }

    /// <summary>
    /// The row appeared while the folder was being shown - something new on
    /// disk, not a row of a folder just opened - and fades in rather than
    /// popping up (the row style's trigger).  Cleared again a moment later, so
    /// a row scrolled back into view later is simply there.
    /// </summary>
    public bool IsNew
    {
        get => _isNew;
        set => SetProperty(ref _isNew, value);
    }
}

/// <summary>
/// How well a name answers what was typed, lower being better.  Typing is for
/// finding one thing quickly, so an exact prefix has to come out above a name
/// that merely contains the letters somewhere.
/// </summary>
public static class FolderListMatch
{
    public const int NoMatch = int.MaxValue;

    public static int Score(string name, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        var contains = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (contains > 0)
        {
            // Later in the name is a weaker answer than earlier in it, but every
            // substring hit still beats a scattered one.
            return 1 + Math.Min(contains, 998);
        }

        // "asrip" finds "AssetRipper": the letters in order, not necessarily
        // together.  Worth having, and worth ranking below a real substring.
        return IsSubsequence(name, query) ? 1_000_000 : NoMatch;
    }

    private static bool IsSubsequence(string name, string query)
    {
        var index = 0;
        foreach (var character in name)
        {
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[index]))
            {
                index++;
                if (index == query.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
