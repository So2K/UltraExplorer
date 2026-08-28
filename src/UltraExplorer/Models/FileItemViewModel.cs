using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public sealed class FileItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _isCut;
    private bool _isDropTarget;

    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required string TypeDescription { get; init; }
    public required string SizeDisplay { get; init; }
    public required string ModifiedDisplay { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required long? SizeBytes { get; init; }
    public bool IsHidden { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsCut
    {
        get => _isCut;
        set => SetProperty(ref _isCut, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }
}
