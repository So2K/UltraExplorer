using System.Collections.ObjectModel;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

/// <summary>
/// One clickable crumb in the address bar.  The chevron after it is a crumb of
/// its own: it stands for the folders that could have been taken from here
/// instead, which is what Explorer keeps behind the same chevron.
/// </summary>
public sealed class BreadcrumbSegment(string name, string fullPath, bool isLast) : ObservableObject
{
    private bool _isMenuOpen;
    private ObservableCollection<AddressSuggestion> _children = [];

    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    /// <summary>The folder the window is actually in; the crumbs before it are the way there.</summary>
    public bool IsLast { get; } = isLast;

    /// <summary>Sub-folders of this crumb, read when its chevron is first opened.</summary>
    public ObservableCollection<AddressSuggestion> Children => _children;

    /// <summary>Publishes an already-built menu in one bound change, not an Add per directory.</summary>
    internal void ReplaceChildren(ObservableCollection<AddressSuggestion> children)
        => SetProperty(ref _children, children, nameof(Children));

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetProperty(ref _isMenuOpen, value);
    }

    /// <summary>Said out loud in the menu, so an empty one does not read as a broken one.</summary>
    public string EmptyText => "No sub-folders";
}
