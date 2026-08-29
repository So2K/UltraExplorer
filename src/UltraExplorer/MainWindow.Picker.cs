using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Picker;

namespace UltraExplorer;

/// <summary>
/// The window in file-dialog mode.  Nothing here replaces the canvas: the same
/// graph, sidebar, breadcrumbs and search are what the caller's user browses
/// with.  A footer appears, the entry rules tighten to what was asked for, and
/// activating an item answers the caller instead of launching it.
/// </summary>
public partial class MainWindow
{
    private FileDialogSession? _picker;
    private TaskCompletionSource<FileDialogResult>? _pickerCompletion;
    private bool _pickerFinished;

    /// <summary>
    /// The folder the caller asked for, kept from before the canvas is built:
    /// restoring the saved workspace selects whatever was open last time, which
    /// moves the session's idea of where it is.  The caller's choice wins.
    /// </summary>
    private string _pickerStartFolder = string.Empty;

    public bool IsPickerMode => _picker is not null;

    /// <summary>Completes once the user accepts, cancels or closes the window.</summary>
    public Task<FileDialogResult> PickerResult =>
        _pickerCompletion?.Task ?? Task.FromResult(FileDialogResult.Cancelled());

    /// <summary>What is highlighted right now, for a caller that is watching.</summary>
    public IReadOnlyList<string> PickerSelection =>
        _picker is null ? [] : _viewModel.Tree.SelectedPaths;

    /// <summary>The caller dismissing its own dialog through <c>IFileDialog::Close</c>.</summary>
    public void CloseFromCaller()
    {
        if (_picker is { } session)
        {
            FinishPicker(session.Cancelled());
        }
    }

    /// <summary>The caller moving the view with <c>IFileDialog::SetFolder</c>.</summary>
    public Task NavigateFromCallerAsync(string folder) => NavigatePickerAsync(folder);

    private void AttachPicker(FileDialogSession session)
    {
        _picker = session;
        _pickerCompletion = new TaskCompletionSource<FileDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pickerStartFolder = session.CurrentFolder;
        Title = session.Title;
        _viewModel.DialogTitle = session.Title;
        PickerFooter.DataContext = session;
        PickerFooter.Visibility = Visibility.Visible;

        var typeRow = session.ShowsFileTypes ? Visibility.Visible : Visibility.Collapsed;
        PickerTypeLabel.Visibility = typeRow;
        PickerTypeBox.Visibility = typeRow;

        if (session.Request.HideNewFolderButton)
        {
            NewFolderButton.Visibility = Visibility.Collapsed;
        }

        if (session.Request.Has(FileDialogOptions.HidePinnedPlaces))
        {
            QuickAccessItems.Visibility = Visibility.Collapsed;
            QuickAccessSeparator.Visibility = Visibility.Collapsed;
        }

        AddPickerPlaces(session);

        session.FilterChanged += OnPickerFilterChanged;
        _viewModel.Tree.ActivationOverride = OnPickerActivate;
        _viewModel.Tree.SelectedNodes.CollectionChanged += OnPickerSelectionChanged;
        _viewModel.Tree.PropertyChanged += OnPickerTreePropertyChanged;
        PreviewKeyDown += OnPickerPreviewKeyDown;
    }

    /// <summary>
    /// Folders the caller pinned with <c>AddPlace</c>.  FDAP_TOP entries lead,
    /// as they do in the standard dialog's sidebar.
    /// </summary>
    private void AddPickerPlaces(FileDialogSession session)
    {
        var places = session.Request.Places
            .Where(place => Directory.Exists(place.Path))
            .OrderByDescending(place => place.Top)
            .ToArray();

        foreach (var place in places)
        {
            var full = Path.GetFullPath(place.Path);
            _viewModel.PickerPlaces.Add(new FavoriteItemViewModel
            {
                Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                    ? name
                    : full,
                Path = full,
                Glyph = "",
                AccentHex = "#60CDFF",
                Kind = SidebarItemKind.QuickAccess
            });
        }

        if (_viewModel.PickerPlaces.Count > 0)
        {
            PlacesItems.Visibility = Visibility.Visible;
            PlacesSeparator.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Pushes the dialog's entry rules into the graph: files hidden entirely
    /// while a folder is being picked, otherwise only names the selected file
    /// type matches, plus hidden and system items when the caller demanded them.
    /// </summary>
    private async Task ApplyPickerRulesAsync()
    {
        if (_picker is not { } session)
        {
            return;
        }

        var forceHidden = session.Request.Has(FileDialogOptions.ForceShowHidden);
        await _viewModel.Tree.ApplyEntryRulesAsync(
            session.Request.ShowsFiles,
            session.GraphFilter.MatchesEverything ? null : session.GraphFilter,
            forceHidden ? true : null);
    }

    private async void OnPickerFilterChanged()
    {
        try
        {
            await ApplyPickerRulesAsync();
        }
        catch (Exception exception)
        {
            _viewModel.Toast.ShowError(exception.Message);
        }
    }

    private void OnPickerSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _picker?.ReportSelection(_viewModel.Tree.SelectedPaths);
    }

    private void OnPickerTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_picker is not { } session || e.PropertyName != nameof(_viewModel.Tree.ActiveNode))
        {
            return;
        }

        if (_viewModel.Tree.ActiveNode is not { } node)
        {
            return;
        }

        session.CurrentFolder = node.IsDirectory
            ? node.FullPath
            : Path.GetDirectoryName(node.FullPath) ?? session.CurrentFolder;
    }

    /// <summary>
    /// Double-clicking, or pressing Enter on, a node.  A folder still opens; a
    /// file is the answer the caller is waiting for.
    /// </summary>
    private bool OnPickerActivate(ViewAllNodeViewModel node)
    {
        if (_picker is not { } session)
        {
            return false;
        }

        session.MarkInteraction();

        if (node.IsDirectory)
        {
            session.CurrentFolder = node.FullPath;

            // Opening a folder is navigation even when a folder is what the
            // caller wants; the Select button is how one is returned.
            session.FileNameText = session.PicksFolders ? string.Empty : session.FileNameText;
            return false;
        }

        if (session.PicksFolders)
        {
            // Files are on show only as context, so a double-click does nothing.
            return true;
        }

        session.FileNameText = node.DisplayName;
        _ = FinishIfValidAsync([node.FullPath]);
        return true;
    }

    private void OnPickerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_picker is null)
        {
            return;
        }

        if (e.Key == Key.Escape && !_viewModel.IsSearchOpen && !_viewModel.Address.IsEditing)
        {
            e.Handled = true;
            CancelPicker();
        }
    }

    private void PickerNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_picker is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;

                // The editable ComboBox commits its text on Enter, and the
                // binding has to have run before the name is read.
                if (sender is ComboBox box)
                {
                    box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                }

                _picker.MarkInteraction();
                _ = RunPickerAcceptAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                CancelPicker();
                break;
        }
    }

    private void PickerAccept_Click(object sender, RoutedEventArgs e)
    {
        _picker?.MarkInteraction();
        _ = RunPickerAcceptAsync();
    }

    private void PickerCancel_Click(object sender, RoutedEventArgs e) => CancelPicker();

    /// <summary>
    /// What OK means right now: open a folder, re-filter on a wildcard, or
    /// answer the caller.
    /// </summary>
    private async Task RunPickerAcceptAsync()
    {
        if (_picker is not { } session || _pickerFinished)
        {
            return;
        }

        try
        {
            var action = session.Prepare(_viewModel.Tree.SelectedPaths);
            switch (action.Kind)
            {
                case FileDialogActionKind.Navigate:
                    session.FileNameText = string.Empty;
                    await NavigatePickerAsync(action.Folder);
                    break;

                case FileDialogActionKind.Filter:
                    if (!string.IsNullOrEmpty(action.Folder))
                    {
                        await NavigatePickerAsync(action.Folder);
                    }

                    session.ApplyTypedPattern(action.Pattern);
                    break;

                case FileDialogActionKind.Accept:
                    await FinishIfValidAsync(action.Paths);
                    break;

                default:
                    ConfirmDialog.Alert(this, "UltraExplorer", "Type or choose a name first.");
                    break;
            }
        }
        catch (Exception exception)
        {
            ConfirmDialog.Alert(this, "UltraExplorer", exception.Message);
        }
    }

    private async Task NavigatePickerAsync(string folder)
    {
        if (_picker is not { } session || string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        session.CurrentFolder = folder;
        await _viewModel.Tree.RevealPathAsync(folder);
    }

    /// <summary>
    /// Runs the caller's validation rules, asking whatever they require, and
    /// finishes only when everything is satisfied.
    /// </summary>
    private async Task FinishIfValidAsync(IReadOnlyList<string> paths)
    {
        if (_picker is not { } session)
        {
            return;
        }

        // Each answered question is remembered, so the loop always advances.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var verdict = session.Validate(paths);

            if (verdict.IsAccept)
            {
                // The caller gets the last word on what it is handed.
                if (session.AcceptGuard is { } guard && !guard(verdict.Paths))
                {
                    return;
                }

                foreach (var path in verdict.Paths)
                {
                    session.RememberName(Path.GetFileName(path));
                }

                session.Remember(session.CurrentFolder);
                FinishPicker(session.Accepted(verdict.Paths));
                return;
            }

            if (!verdict.IsQuestion)
            {
                ConfirmDialog.Alert(this, verdict.Caption, verdict.Message);
                return;
            }

            // A caller watching the dialog may answer the overwrite question
            // itself, in which case the user is never asked.
            if (verdict.Kind == FileDialogVerdictKind.ConfirmOverwrite
                && session.OverwriteGuard is { } overwrite
                && verdict.Paths.Count > 0
                && overwrite(verdict.Paths[0]) is { } decided)
            {
                if (!decided)
                {
                    return;
                }

                session.Allow(FileDialogGate.Overwrite);
                continue;
            }

            if (!AskPicker(verdict))
            {
                return;
            }

            session.Allow(verdict.Gate);
            await Task.Yield();
        }
    }

    /// <summary>
    /// Asks one of the dialog's own questions.  The shared confirmation reads
    /// "Delete" by default, which would be an alarming thing to offer somebody
    /// who is only replacing a file.
    /// </summary>
    private bool AskPicker(FileDialogVerdict verdict)
    {
        var label = verdict.Kind switch
        {
            FileDialogVerdictKind.ConfirmOverwrite => "Replace",
            FileDialogVerdictKind.ConfirmCreate => "Create",
            _ => "Yes"
        };

        var dialog = new ConfirmDialog(verdict.Caption, verdict.Message, label) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void CancelPicker()
    {
        if (_picker is { } session)
        {
            FinishPicker(session.Cancelled());
        }
    }

    private void FinishPicker(FileDialogResult result)
    {
        if (_pickerFinished)
        {
            return;
        }

        _pickerFinished = true;
        _pickerCompletion?.TrySetResult(result);
        Close();
    }

    /// <summary>Closing the window is a cancel, however it was closed.</summary>
    private void CompletePickerOnClose()
    {
        if (_picker is { } session && !_pickerFinished)
        {
            _pickerFinished = true;
            _pickerCompletion?.TrySetResult(session.Cancelled());
        }
    }
}
