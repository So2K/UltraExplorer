using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public sealed class FolderNodeViewModel : ObservableObject, IDisposable
{
    private Point _location;
    private Size _actualSize;
    private bool _isBusy;
    private bool _isDropTarget;
    private bool _isNoteVisible;
    private string _note = string.Empty;
    private string _accentHex = "#4F7FFF";
    private Brush _accentBrush = CreateBrush("#4F7FFF");
    private string _errorMessage = string.Empty;
    private string _filterText = string.Empty;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public FolderNodeViewModel(string fullPath, Point location, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        FullPath = Path.GetFullPath(fullPath);
        DisplayName = GetDisplayName(FullPath);
        Location = location;
    }

    public Guid Id { get; }
    public string FullPath { get; }
    public string DisplayName { get; }
    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    public IEnumerable<FileItemViewModel> VisibleItems => string.IsNullOrWhiteSpace(FilterText)
        ? Items
        : Items.Where(item => item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    public Point Location
    {
        get => _location;
        set
        {
            if (SetProperty(ref _location, value))
            {
                RaiseAnchors();
            }
        }
    }

    public Size ActualSize
    {
        get => _actualSize;
        set
        {
            if (SetProperty(ref _actualSize, value))
            {
                RaiseAnchors();
            }
        }
    }

    public Point InputAnchor => new(Location.X, Location.Y + 31);
    public Point OutputAnchor => new(Location.X + (ActualSize.Width > 1 ? ActualSize.Width : 420), Location.Y + 31);

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }

    public bool IsNoteVisible
    {
        get => _isNoteVisible;
        set => SetProperty(ref _isNoteVisible, value);
    }

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value))
            {
                IsNoteVisible = !string.IsNullOrWhiteSpace(value) || IsNoteVisible;
            }
        }
    }

    public string AccentHex
    {
        get => _accentHex;
        set
        {
            if (SetProperty(ref _accentHex, value))
            {
                AccentBrush = CreateBrush(value);
            }
        }
    }

    public Brush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                OnPropertyChanged(nameof(VisibleItems));
                OnPropertyChanged(nameof(VisibleItemCountLabel));
            }
        }
    }

    public string ItemCountLabel => Items.Count switch
    {
        0 => "Empty",
        1 => "1 item",
        _ => $"{Items.Count:N0} items"
    };

    public string VisibleItemCountLabel => string.IsNullOrWhiteSpace(FilterText)
        ? ItemCountLabel
        : $"{VisibleItems.Count():N0} of {Items.Count:N0}";

    public void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(ItemCountLabel));
        OnPropertyChanged(nameof(VisibleItemCountLabel));
    }

    public void AttachWatcher(FileSystemWatcher watcher)
    {
        _watcher?.Dispose();
        _watcher = watcher;
    }

    public async Task DebounceReloadAsync(Func<Task> reload)
    {
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = new CancellationTokenSource();
        var token = _reloadDebounce.Token;

        try
        {
            await Task.Delay(350, token);
            await reload();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _watcher?.Dispose();
    }

    private void RaiseAnchors()
    {
        OnPropertyChanged(nameof(InputAnchor));
        OnPropertyChanged(nameof(OutputAnchor));
    }

    private static string GetDisplayName(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(root) ? path : root;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static Brush CreateBrush(string value)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }
}
