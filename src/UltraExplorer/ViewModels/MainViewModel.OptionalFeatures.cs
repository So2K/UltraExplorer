using UltraExplorer.Models;
using UltraExplorer.Services.Archives;

namespace UltraExplorer.ViewModels;

public sealed partial class MainViewModel
{
    private const byte ArchivesPreference = 1, ShelfPreference = 2, CopyPathPreference = 4;
    private byte _optionalFeaturesChanged;
    private bool _browseArchives, _showDropShelf, _showCopyPathButton;

    public bool BrowseArchives
    {
        get => !_isPickerSession && _browseArchives;
        set
        {
            if (_isPickerSession || _browseArchives == value) return;
            _browseArchives = value;
            ArchiveService.BrowseArchives = value;
            Tree.BrowseArchives = value;
            _optionalFeaturesChanged |= ArchivesPreference;
            OnPropertyChanged();
            _ = SaveNowAsync();
        }
    }

    public bool ShowDropShelf
    {
        get => !_isPickerSession && _showDropShelf;
        set
        {
            if (_isPickerSession || _showDropShelf == value) return;
            _showDropShelf = value;
            _optionalFeaturesChanged |= ShelfPreference;
            OnPropertyChanged();
            _ = SaveNowAsync();
        }
    }

    public bool ShowCopyPathButton
    {
        get => !_isPickerSession && _showCopyPathButton;
        set
        {
            if (_isPickerSession || _showCopyPathButton == value) return;
            _showCopyPathButton = value;
            _optionalFeaturesChanged |= CopyPathPreference;
            OnPropertyChanged();
            _ = SaveNowAsync();
        }
    }

    private void ApplyOptionalFeaturePreferences(WorkspaceState state)
    {
        if (_isPickerSession) return;
        if ((_optionalFeaturesChanged & ArchivesPreference) == 0)
        {
            _browseArchives = state.BrowseArchives;
            ArchiveService.BrowseArchives = _browseArchives;
            Tree.BrowseArchives = _browseArchives;
            OnPropertyChanged(nameof(BrowseArchives));
        }
        if ((_optionalFeaturesChanged & ShelfPreference) == 0)
        {
            _showDropShelf = state.ShowDropShelf;
            OnPropertyChanged(nameof(ShowDropShelf));
        }
        if ((_optionalFeaturesChanged & CopyPathPreference) == 0)
        {
            _showCopyPathButton = state.ShowCopyPathButton;
            OnPropertyChanged(nameof(ShowCopyPathButton));
        }
    }

    private void FinishOptionalFeatureSave(WorkspaceState snapshot, byte changes)
    {
        if ((changes & ArchivesPreference) != 0 && _browseArchives == snapshot.BrowseArchives)
            _optionalFeaturesChanged &= unchecked((byte)~ArchivesPreference);
        if ((changes & ShelfPreference) != 0 && _showDropShelf == snapshot.ShowDropShelf)
            _optionalFeaturesChanged &= unchecked((byte)~ShelfPreference);
        if ((changes & CopyPathPreference) != 0 && _showCopyPathButton == snapshot.ShowCopyPathButton)
            _optionalFeaturesChanged &= unchecked((byte)~CopyPathPreference);
    }
}
