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

    /// <summary>Every this many levels the node is half the size.</summary>
    public const int ScaleHalvingDepth = 8;

    /// <summary>Past this depth the node stops shrinking.</summary>
    public const int MaximumScaledDepth = 32;

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
    private bool _isRunTarget;
    private bool _isUserHidden;

    private readonly Color _pathColor;
    private readonly Brush _pathBrush;
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
        Scale = Math.Pow(0.5, Math.Min(depth, MaximumScaledDepth) / (double)ScaleHalvingDepth);
        _pathColor = BranchColorFor(entry.FullPath, depth);
        _pathBrush = FrozenBrush(_pathColor);
    }

    /// <summary>
    /// Set by the graph so a move can update the spatial index in constant time
    /// instead of forcing a rebuild over every node.
    /// </summary>
    internal Action<ViewAllNodeViewModel>? LocationObserver { get; set; }

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

    /// <summary>
    /// The rectangle this folder's children were laid out in, or null when it
    /// holds none.  There is one per folder rather than one per expansion: the
    /// layout is a single pass over the whole tree, so a second page of children
    /// joins the same block instead of starting one beside it.
    /// </summary>
    public ViewAllChildBlock? ChildBlock { get; internal set; }

    public bool CanExpand => IsDirectory;
    public bool HasVisibleChildren => IsExpanded && Children.Any(child => child.IsTreeVisible);
    public int LoadedChildCount => Children.Count;
    public bool HasMoreChildren => IsTruncated;

    /// <summary>
    /// Public setter intentionally treats a change from the canvas as a user
    /// move, and carries the whole subtree with it: dragging a drive drags its
    /// tree.  Layout code uses SetAutomaticLocation instead, which moves only
    /// the node itself.
    /// </summary>
    public Point Location
    {
        get => _location;
        set
        {
            var delta = value - _location;
            SetLocation(value, isManual: true, hasPosition: true);
            if (delta.X != 0 || delta.Y != 0)
            {
                OffsetDescendants(delta);
            }
        }
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

    /// <summary>
    /// Hidden from the canvas by an explicit user action, together with its whole
    /// subtree.  Unlike the visibility a collapsed ancestor imposes, this one
    /// survives expanding, refreshing and restarting: it is intent, not state.
    /// </summary>
    public bool IsUserHidden
    {
        get => _isUserHidden;
        internal set => SetProperty(ref _isUserHidden, value);
    }

    /// <summary>
    /// A program the dragged file would be opened with, rather than a folder it
    /// would be moved into.  Two different outcomes need two different lights.
    /// </summary>
    public bool IsRunTarget
    {
        get => _isRunTarget;
        set => SetProperty(ref _isRunTarget, value);
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
            if (!SetProperty(ref _accentHex, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(HasCustomAccent));
            OnPropertyChanged(nameof(AccentBrush));

            // A colour picked for a folder is the whole point of picking it: it
            // takes over the folder's branch, so every line out of it and the
            // stripe on every child it holds change with it.
            OnPropertyChanged(nameof(BranchColor));
            OnPropertyChanged(nameof(BranchBrush));
            OnPropertyChanged(nameof(FamilyBrush));
            foreach (var child in Children)
            {
                child.NotifyFamilyBrushChanged();
            }
        }
    }

    internal void NotifyFamilyBrushChanged() => OnPropertyChanged(nameof(FamilyBrush));

    public bool HasCustomAccent => !string.IsNullOrEmpty(_accentHex);

    public Brush AccentBrush => BrushCache.Get(HasCustomAccent ? _accentHex : DefaultAccentHex);

    /// <summary>
    /// The colour of this folder's own branch: every line leaving it, and the
    /// stripe on every child it owns.  Once a folder holds a block of children
    /// rather than a row, one colour for every line on the canvas is unreadable -
    /// the eye cannot tell which run belongs to which parent.  The hue comes from
    /// the path, so it is the same colour every session and two folders side by
    /// side are almost never the same.
    /// </summary>
    public Color BranchColor => HasCustomAccent && AccentBrush is SolidColorBrush accent
        ? accent.Color
        : _pathColor;

    public Brush BranchBrush => HasCustomAccent ? AccentBrush : _pathBrush;

    /// <summary>
    /// What the stripe down the left of the node shows: the colour the user
    /// chose if there is one, otherwise the colour of the folder this node lives
    /// in, so a node reads as belonging to its parent.
    /// </summary>
    public Brush FamilyBrush => HasCustomAccent
        ? BrushCache.Get(_accentHex)
        : Parent?.BranchBrush ?? BrushCache.Get(DefaultAccentHex);

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

    /// <summary>
    /// Size relative to a root.  A deeper folder occupies proportionally less of
    /// the canvas: half the size every <see cref="ScaleHalvingDepth"/> levels, so
    /// depth 8 is 50%, depth 16 is 25% and depth 32 is 6.25%.
    /// </summary>
    public double Scale { get; }

    public double Width => DefaultWidth * Scale;

    public double Height => DefaultHeight * Scale;

    // Top-down tree: a branch leaves the bottom of the parent and enters the
    // top of the child.
    public Point InputAnchor => new(Location.X + Width / 2, Location.Y);
    public Point OutputAnchor => new(Location.X + Width / 2, Location.Y + Height);
    public Rect Bounds => new(Location, new Size(Width, Height));

    /// <summary>
    /// A stable hue per path.  FNV-1a rather than string.GetHashCode, which is
    /// randomised per process and would repaint the graph on every launch.
    /// Deeper branches are drawn a little darker, so the eye reads the shallow
    /// structure first.
    /// </summary>
    private static Color BranchColorFor(string path, int depth)
    {
        var hash = 2166136261u;
        foreach (var character in path)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * 16777619u;
        }

        var hue = hash % 360u;
        var lightness = Math.Clamp(0.66 - depth * 0.022, 0.42, 0.66);
        return FromHsl(hue, 0.52, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var sector = hue / 60.0;
        var second = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (red, green, blue) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };

        var offset = lightness - chroma / 2;
        return Color.FromRgb(
            (byte)Math.Round((red + offset) * 255),
            (byte)Math.Round((green + offset) * 255),
            (byte)Math.Round((blue + offset) * 255));
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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

    /// <summary>
    /// Shifts every descendant by the same delta.  A descendant that is itself
    /// selected is skipped: the canvas is already dragging it, and its own
    /// setter carries its subtree, so touching it here would move it twice.
    /// </summary>
    private void OffsetDescendants(Vector delta)
    {
        foreach (var child in Children)
        {
            if (child.IsSelected)
            {
                continue;
            }

            child.OffsetSelf(delta);
        }
    }

    private void OffsetSelf(Vector delta)
    {
        _location = new Point(_location.X + delta.X, _location.Y + delta.Y);
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(InputAnchor));
        OnPropertyChanged(nameof(OutputAnchor));
        OnPropertyChanged(nameof(Bounds));
        LocationObserver?.Invoke(this);
        OffsetDescendants(delta);
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
            LocationObserver?.Invoke(this);
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
