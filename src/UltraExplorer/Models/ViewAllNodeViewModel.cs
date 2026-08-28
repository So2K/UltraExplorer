using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

/// <summary>
/// A deliberately small graph node: exactly one file-system object, never a
/// window with an embedded file list.
/// </summary>
public sealed class ViewAllNodeViewModel : ObservableObject
{
    public const double DefaultWidth = 188;
    public const double DefaultHeight = 44;

    private Point _location;
    private bool _hasLayoutPosition;
    private bool _hasManualPosition;
    private bool _isExpanded;
    private bool _areChildrenLoaded;
    private bool _isLoading;
    private bool _isTreeVisible = true;
    private bool _isViewportRealized = true;
    private bool _isSelected;
    private bool _isDropTarget;
    private bool _isTruncated;
    private int _childLoadLimit;
    private string _errorMessage = string.Empty;
    private string _accentHex = string.Empty;
    private string _note = string.Empty;
    private ImageSource? _icon;
    private ViewAllDetailLevel _detailLevel = ViewAllDetailLevel.Detailed;

    public ViewAllNodeViewModel(
        ViewAllEntryDescriptor entry,
        int depth,
        ViewAllNodeViewModel? parent = null)
    {
        Entry = entry;
        Depth = depth;
        Parent = parent;
        Id = ViewAllNodeIdentity.FromPath(entry.FullPath);
    }

    public Guid Id { get; }
    public ViewAllEntryDescriptor Entry { get; }
    public string FullPath => Entry.FullPath;
    public string DisplayName => Entry.DisplayName;
    public string SecondaryText => Entry.SecondaryText;
    public ViewAllEntryKind Kind => Entry.Kind;
    public bool IsDrive => Kind == ViewAllEntryKind.Drive;
    public bool IsDirectory => Kind is ViewAllEntryKind.Drive or ViewAllEntryKind.Folder;
    public bool IsFile => Kind == ViewAllEntryKind.File;
    public bool IsHidden => Entry.IsHidden;
    public bool IsReparsePoint => Entry.IsReparsePoint;
    public int Depth { get; }
    public ViewAllNodeViewModel? Parent { get; }
    public Guid? ParentId => Parent?.Id;
    public ObservableCollection<ViewAllNodeViewModel> Children { get; } = [];

    public bool CanExpand => IsDirectory;
    public bool HasVisibleChildren => IsExpanded && Children.Any(child => child.IsTreeVisible);
    public int LoadedChildCount => Children.Count;
    public bool HasMoreChildren => IsTruncated;

    /// <summary>
    /// Public setter intentionally treats a change from the canvas as a user
    /// move.  Layout code uses SetAutomaticLocation instead.
    /// </summary>
    public Point Location
    {
        get => _location;
        set => SetLocation(value, isManual: true, hasPosition: true);
    }

    public bool HasLayoutPosition
    {
        get => _hasLayoutPosition;
        private set => SetProperty(ref _hasLayoutPosition, value);
    }

    public bool HasManualPosition
    {
        get => _hasManualPosition;
        private set => SetProperty(ref _hasManualPosition, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(HasVisibleChildren));
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(ChevronAngle));
            }
        }
    }

    public bool AreChildrenLoaded
    {
        get => _areChildrenLoaded;
        internal set => SetProperty(ref _areChildrenLoaded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        internal set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Visibility inherited from collapsed ancestors.</summary>
    public bool IsTreeVisible
    {
        get => _isTreeVisible;
        internal set => SetProperty(ref _isTreeVisible, value);
    }

    /// <summary>Viewport culling flag; it does not change tree state.</summary>
    public bool IsViewportRealized
    {
        get => _isViewportRealized;
        internal set => SetProperty(ref _isViewportRealized, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }

    public bool IsTruncated
    {
        get => _isTruncated;
        internal set
        {
            if (SetProperty(ref _isTruncated, value))
            {
                OnPropertyChanged(nameof(HasMoreChildren));
            }
        }
    }

    public int ChildLoadLimit
    {
        get => _childLoadLimit;
        internal set => SetProperty(ref _childLoadLimit, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        internal set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public ViewAllDetailLevel DetailLevel
    {
        get => _detailLevel;
        internal set => SetProperty(ref _detailLevel, value);
    }

    /// <summary>The real Shell icon; resolved once per extension by the graph.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>Empty means "use the colour that belongs to this object kind".</summary>
    public string AccentHex
    {
        get => _accentHex;
        set
        {
            if (SetProperty(ref _accentHex, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasCustomAccent));
                OnPropertyChanged(nameof(AccentBrush));
            }
        }
    }

    public bool HasCustomAccent => !string.IsNullOrEmpty(_accentHex);

    public Brush AccentBrush => BrushCache.Get(HasCustomAccent ? _accentHex : DefaultAccentHex);

    public string DefaultAccentHex => Kind switch
    {
        ViewAllEntryKind.Drive => "#9AA4B2",
        ViewAllEntryKind.Folder => "#E3B341",
        _ => "#5C6675"
    };

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_note);

    /// <summary>Fallback Segoe Fluent glyph used until the Shell icon arrives.</summary>
    public string Glyph => Kind switch
    {
        ViewAllEntryKind.Drive => "\uEDA2",
        ViewAllEntryKind.Folder => IsExpanded ? "\uE838" : "\uE8B7",
        _ => "\uE8A5"
    };

    /// <summary>Disclosure chevron rotation: 0 collapsed, 90 expanded.</summary>
    public double ChevronAngle => IsExpanded ? 90 : 0;

    public Point InputAnchor => new(Location.X, Location.Y + DefaultHeight / 2);
    public Point OutputAnchor => new(Location.X + DefaultWidth, Location.Y + DefaultHeight / 2);
    public Rect Bounds => new(Location, new Size(DefaultWidth, DefaultHeight));

    public void SetAutomaticLocation(Point location)
        => SetLocation(location, isManual: false, hasPosition: true);

    public void RestoreLocation(Point location, bool isManual)
        => SetLocation(location, isManual, hasPosition: true);

    public void ReleaseManualPosition()
    {
        if (HasManualPosition)
        {
            HasManualPosition = false;
        }
    }

    public void ReleaseAutomaticLocation()
    {
        if (!HasManualPosition)
        {
            HasLayoutPosition = false;
        }
    }

    internal void NotifyChildrenChanged()
    {
        OnPropertyChanged(nameof(LoadedChildCount));
        OnPropertyChanged(nameof(HasVisibleChildren));
    }

    private void SetLocation(Point location, bool isManual, bool hasPosition)
    {
        var changed = SetProperty(ref _location, location, nameof(Location));
        HasLayoutPosition = hasPosition;
        HasManualPosition = isManual;
        if (changed)
        {
            OnPropertyChanged(nameof(InputAnchor));
            OnPropertyChanged(nameof(OutputAnchor));
            OnPropertyChanged(nameof(Bounds));
        }
    }
}

public static class ViewAllNodeIdentity
{
    /// <summary>Creates a repeatable id so a restored path keeps its node identity.</summary>
    public static Guid FromPath(string path)
    {
        var canonical = ViewAllPath.Normalize(path).ToUpperInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        Span<byte> id = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(id);
        id[7] = (byte)((id[7] & 0x0F) | 0x50); // RFC 4122, name-based shape.
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id);
    }
}

public static class ViewAllPath
{
    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool Equals(string left, string right)
    {
        try
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Root-first chain of every ancestor path including <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> AncestorChain(string path)
    {
        var chain = new List<string>();
        var current = Normalize(path);
        while (true)
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            var normalizedParent = Normalize(parent);
            if (string.Equals(normalizedParent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = normalizedParent;
        }

        chain.Reverse();
        return chain;
    }
}
