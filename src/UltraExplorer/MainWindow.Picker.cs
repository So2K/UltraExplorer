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
    internal void AttachNativeDialogControls(System.Windows.Controls.UserControl controls)
    {
        if (PickerFooter.Child is not Grid grid) return;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(controls, 3);
        Grid.SetColumnSpan(controls, 3);
        grid.Children.Add(controls);
    }

    private FileDialogSession? _picker;
    private bool _pickerNestedReady;
    private TaskCompletionSource<FileDialogResult>? _pickerCompletion;
    private bool _pickerFinished;
    private bool _pickerBindingSession;

    /// <summary>Set once the window class gives a picker the first look at each key, which is once per process.</summary>
    private static int _pickerKeysWatched;

    /// <summary>
    /// Whether a crumb's list of folders was open as the key went down.  The
    /// window's own Escape closes those lists without claiming the key, so
    /// by the time an unclaimed Escape comes back up, the list is gone.
    /// </summary>
    private bool _pickerMenuOpenAtKey;

    /// <summary>
    /// How long a replaced dialog waits for the disk before the application's
    /// own dialog, which checks the answer again anyway, decides instead.  A
    /// server that does not answer takes some 20 s to say so, and the guardian
    /// takes an interface thread that stops for 5 s for a hung one.
    /// </summary>
    private static readonly TimeSpan PickerDiskBudget = TimeSpan.FromSeconds(1);

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
        _picker is null ? [] : IsNested ? _viewModel.Tree.Selection.Paths : _viewModel.Tree.SelectedPaths;

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

        ShowPickerSession(session);
        _viewModel.Tree.ActivationOverride = OnPickerActivate;
        _viewModel.Tree.Selection.Changed += OnPickerSelectionChanged;
        _viewModel.Tree.PropertyChanged += OnPickerTreePropertyChanged;
        KeyDown += OnPickerKeyDown;
        if (Interlocked.Exchange(ref _pickerKeysWatched, 1) == 0)
        {
            EventManager.RegisterClassHandler(typeof(MainWindow), PreviewKeyDownEvent, new KeyEventHandler(OnPickerClassPreviewKeyDown));
        }
    }

    /// <summary>
    /// Everything of the window that follows the session: title, footer,
    /// file-type row, New folder, the pinned places and the caller's own
    /// places, and the file-type subscription.  Repeatable: a prepared picker
    /// bound to another dialog (<see cref="RebindAsync"/>) goes through it
    /// again, and nothing of the previous session is left.
    /// </summary>
    private void ShowPickerSession(FileDialogSession session)
    {
        _pickerStartFolder = session.CurrentFolder;
        _viewModel.ConfigurePickerFiles(session.Request.ShowsFiles);
        Title = session.Title;
        _viewModel.DialogTitle = session.Title;
        // A previous dialog may have left the name editor focused. Binding
        // the new request's text is setup, not the user's keyboard input.
        _pickerBindingSession = true;
        try
        {
            PickerFooter.DataContext = session;
            PickerNameBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();
        }
        finally { _pickerBindingSession = false; }
        PickerFooter.Visibility = Visibility.Visible;

        var typeRow = session.ShowsFileTypes ? Visibility.Visible : Visibility.Collapsed;
        PickerTypeLabel.Visibility = typeRow;
        PickerTypeBox.Visibility = typeRow;

        NewFolderButton.Visibility = session.Request.HideNewFolderButton ? Visibility.Collapsed : Visibility.Visible;

        var pinned = session.Request.Has(FileDialogOptions.HidePinnedPlaces) ? Visibility.Collapsed : Visibility.Visible;
        QuickAccessItems.Visibility = pinned;
        QuickAccessSeparator.Visibility = pinned;

        _viewModel.PickerPlaces.Clear();
        PlacesItems.Visibility = Visibility.Collapsed;
        PlacesSeparator.Visibility = Visibility.Collapsed;
        AddPickerPlaces(session);

        session.FilterChanged += OnPickerFilterChanged;
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
        if (IsNested)
        {
            var filter = session.GraphFilter;
            ActivePane.Tree.ShowFiles = session.Request.ShowsFiles;
            ActivePane.Tree.FileNameFilter = filter.MatchesEverything ? null : filter.Matches;
            ActivePane.Tree.IncludeHidden = forceHidden || _viewModel.Tree.ShowHiddenItems;
            return;
        }
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

    /// <summary>
    /// The tree's choice, once the shared selection has taken a click in.
    /// Nodify's own copy is no guide: a click adds the new node before it
    /// takes the old one out, and the selection follows only afterwards. The
    /// tile canvas reports from the same change (<see cref="OnSharedSelectionChanged"/>).
    /// </summary>
    private void OnPickerSelectionChanged(ItemSelection selection)
    {
        if (!IsNested) _picker?.ReportSelection(selection.Paths);
    }

    /// <summary>
    /// The folder the canvas's view rests on.  With nothing selected it is
    /// the dialog's folder.  Moved there by the user's hand, it is also when
    /// the only thing selected is a folder only gone to - the one the dialog
    /// opened at, revealed and selected for it: the dialog goes there as a
    /// folder opened in it does, so Save writes there and Select Folder
    /// answers it rather than the folder it opened at.  What the user chose
    /// themselves stays chosen.
    /// </summary>
    internal void OnPickerNestedFolderChanged(string? folder, bool movedByUser = false)
    {
        if (!_pickerNestedReady || _picker is not { } session || folder is not { Length: > 0 })
        {
            return;
        }

        var selection = _viewModel.Tree.Selection;
        if (selection.Count == 0)
        {
            session.CurrentFolder = folder;
        }
        else if (movedByUser && selection.Count == 1 && selection.FolderCount == 1
            && selection.LastSource == SelectionSource.Navigation
            && !ViewAllPath.Equals(folder, session.CurrentFolder))
        {
            // A move of the view is not a choice: a folder the user typed into
            // Select Folder's name box stays the answer.
            OpenPickerNestedFolder(folder, keepTypedName: true);
        }
    }

    internal void OpenPickerNestedFolder(string path, bool keepTypedName = false)
    {
        if (_picker is not { } session) return;
        session.CurrentFolder = path;
        _viewModel.Tree.Selection.Apply(new SelectionEdit
        { Clear = true, Focus = path, Source = SelectionSource.Navigation });
        if (session.PicksFolders && !(keepTypedName && session.HasTypedName)) session.FileNameText = string.Empty;
    }

    internal void OpenPickerNestedFile(string path)
    {
        if (_picker is not { } session || session.PicksFolders) return;
        session.MarkInteraction();
        session.FileNameText = Path.GetFileName(path);
        _ = OpenPickerFileAsync(path);
    }

    /// <summary>
    /// A file opened on the canvas is the caller's answer, unless it is a
    /// shortcut to a folder: that opens the folder, as in the standard dialog.
    /// </summary>
    private async Task OpenPickerFileAsync(string path)
    {
        if (_picker is { } session
            && await ReadPickerDiskAsync(session, () => session.FolderBehindLink(path), () => null) is { } folder)
        {
            if (!ReferenceEquals(session, _picker) || _pickerFinished) return;
            session.FileNameText = string.Empty;
            await NavigatePickerAsync(folder);
            return;
        }

        await FinishIfValidAsync([path]);
    }

    /// <summary>
    /// Runs <paramref name="read"/>, which looks at the disk.  A replaced
    /// dialog looks on a worker thread, for <see cref="PickerDiskBudget"/> at
    /// most, and takes <paramref name="unanswered"/> when that runs out;
    /// anything else looks in place, as it always has.
    /// </summary>
    private static async Task<T> ReadPickerDiskAsync<T>(FileDialogSession session, Func<T> read, Func<T> unanswered)
    {
        if (!session.Request.IsNativeProxy)
        {
            return read();
        }

        var reading = Task.Run(read);
        if (await Task.WhenAny(reading, Task.Delay(PickerDiskBudget)) == reading)
        {
            return await reading;
        }

        // The look goes on until Windows gives up; what it finds then is not wanted.
        _ = reading.ContinueWith(late => _ = late.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return unanswered();
    }

    private void OnPickerTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsNested || _picker is not { } session || e.PropertyName != nameof(_viewModel.Tree.ActiveNode))
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
        _ = OpenPickerFileAsync(node.FullPath);
        return true;
    }

    /// <summary>
    /// A picker's first look at a key, before the window's own handlers (a
    /// class handler comes before every one of them).  F4 in the name or
    /// type box opens that box's list, as in the standard dialog, not the
    /// address bar's recent places; whether a crumb's list is open is noted
    /// before the window's Escape closes it; and Escape in the list's empty
    /// filter, which would otherwise take every Escape, cancels.
    /// </summary>
    private static void OnPickerClassPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow { _picker: not null } window)
        {
            return;
        }

        window._pickerMenuOpenAtKey = window._viewModel.Address.Breadcrumbs.Any(crumb => crumb.IsMenuOpen);

        // The list's filter keeps the keyboard once Escape has cleared it, so
        // an Escape there with nothing left to clear is the dialog's, as one
        // that nothing else wanted is everywhere else.
        if (e.Key == Key.Escape && !window._pickerMenuOpenAtKey
            && !window._viewModel.Search.IsOpen && !window._viewModel.Address.IsEditing
            && window.FolderListFilterBox.Text.Length == 0
            && (window.FolderListFilterBox.IsKeyboardFocusWithin || ReferenceEquals(e.OriginalSource, window.FolderListFilterBox)))
        {
            e.Handled = true;
            window.CancelPicker();
            return;
        }

        if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None
            && new[] { window.PickerNameBox, window.PickerTypeBox }.FirstOrDefault(box => box.IsKeyboardFocusWithin
                || (e.OriginalSource is System.Windows.Media.Visual source && box.IsAncestorOf(source))) is { } box)
        {
            box.IsDropDownOpen = !box.IsDropDownOpen;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Escape that nothing else wanted cancels the dialog.  It is taken on
    /// the way back up, so a filter clears, an open drop-down closes and a
    /// crumb's list of folders goes away first, each with an Escape of its own.
    /// </summary>
    private void OnPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (_picker is null)
        {
            return;
        }

        if (e.Key == Key.Escape && !_pickerMenuOpenAtKey && !_viewModel.Search.IsOpen && !_viewModel.Address.IsEditing)
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

                // Enter is the OK button.  While OKBUTTONNEEDSINTERACTION
                // keeps that button disabled, Enter is exactly the stray
                // keystroke the flag is there to stop, so it is not itself
                // counted as the interaction.
                if (_picker.Request.Has(FileDialogOptions.OkButtonNeedsInteraction) && !_picker.HasInteracted)
                {
                    break;
                }

                _ = RunPickerAcceptAsync();
                break;

            case Key.Escape:
                // The box's own list of names closes first, as in any combo
                // box; so does a crumb's list of folders, which the window
                // leaves open while the keyboard is in a text box.
                if (sender is ComboBox { IsDropDownOpen: true })
                {
                    break;
                }

                e.Handled = true;
                if (_pickerMenuOpenAtKey)
                {
                    foreach (var crumb in _viewModel.Address.Breadcrumbs)
                    {
                        crumb.IsMenuOpen = false;
                    }

                    break;
                }

                CancelPicker();
                break;
        }
    }

    /// <summary>
    /// Editing the name is interaction (FOS_OKBUTTONNEEDSINTERACTION), but
    /// only when the user does it: the box's text also changes when a click
    /// on the canvas fills it in, which is interaction of its own, or when
    /// the window sets it up, which is not.
    /// </summary>
    private void PickerNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_pickerBindingSession && _picker is { } session && sender is UIElement { IsKeyboardFocusWithin: true })
        {
            session.MarkInteraction();
        }
    }

    private void PickerAccept_Click(object sender, RoutedEventArgs e)
    {
        _picker?.MarkInteraction();
        _ = RunPickerAcceptAsync();
    }

    private void PickerCancel_Click(object sender, RoutedEventArgs e) => CancelPicker();

    private void PickerClearChoice_Click(object sender, RoutedEventArgs e)
    {
        if (_picker is not { } session) return;
        _viewModel.Tree.Selection.Apply(new SelectionEdit { Clear = true, Source = SelectionSource.Navigation });
        if (!IsNested) _viewModel.Tree.SelectedNodes.Clear();
        session.ClearChoice();
    }

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
            // The editable ComboBox's inner text can be ahead of its Text
            // binding (paste, automation, a rapid type/OK sequence). Commit
            // what is actually displayed before resolving the destination.
            if (PickerNameBox.Template?.FindName("PART_EditableTextBox", PickerNameBox) is TextBox editable)
                PickerNameBox.SetCurrentValue(ComboBox.TextProperty, editable.Text);
            PickerNameBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            var selection = PickerSelection;
            var action = await ReadPickerDiskAsync(session,
                () => session.Prepare(selection), () => session.Prepare(selection, readDisk: false));
            if (_pickerFinished)
            {
                return;
            }

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
        if (IsNested) await ActivePane.FlyToAsync(folder, gentle: false, animated: false);
    }

    /// <summary>
    /// Runs the caller's validation rules, asking whatever they require, and
    /// finishes only when everything is satisfied.
    /// </summary>
    private async Task FinishIfValidAsync(IReadOnlyList<string> paths)
    {
        if (_pickerFinished || _picker is null)
        {
            return;
        }

        // The contract may arrive after Cancel, or after this prepared
        // window has been rebound to another dialog. A same-dialog rebind
        // keeps its completion, so a valid pending attempt can still finish
        // with the fully read session.
        var completion = _pickerCompletion;
        bool IsCurrentAttempt() => !_pickerFinished && ReferenceEquals(completion, _pickerCompletion);

        // A replacement shown before the caller's dialog was fully read
        // accepts nothing until its file types, options and multiple
        // selection are confirmed; a double-click or Enter meanwhile waits.
        if (!await WhenContractConfirmedAsync() || !IsCurrentAttempt() || _picker is not { } session)
        {
            return;
        }

        // An answer belongs to the attempt it was given in: a "Replace" for
        // a.txt whose save then stopped short (the caller's OnFileOk said no,
        // or a later question was answered No) must not replace b.txt without
        // asking when OK is pressed again.
        session.ForgetAnswers();

        // Each answered question is remembered, so the loop always advances.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!IsCurrentAttempt() || !ReferenceEquals(session, _picker)) return;

            // Unanswered, what was chosen goes to the application's own
            // dialog, whose checks are the final ones.
            var verdict = await ReadPickerDiskAsync(session, () => session.Validate(paths),
                () => new FileDialogVerdict(FileDialogVerdictKind.Accept, session.AllowsMultipleSelection ? paths : [.. paths.Take(1)]));
            if (!IsCurrentAttempt() || !ReferenceEquals(session, _picker)) return;

            if (verdict.IsAccept)
            {
                // The caller gets the last word on what it is handed.
                if (session.AcceptGuard is { } guard && !guard(verdict.Paths))
                {
                    return;
                }

                // A caller's guard can close or replace its dialog while
                // validating. That ended attempt records no accepted state.
                if (!IsCurrentAttempt() || !ReferenceEquals(session, _picker)) return;
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
        return dialog.ShowOwnerModal();
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

    /// <summary>
    /// Closing the window is a cancel, however it was closed.  The session
    /// lets go of the window too: a COM caller keeps it, through the dialog
    /// object, for as long as it keeps that, and a closed window has nothing
    /// to filter.
    /// </summary>
    private void CompletePickerOnClose()
    {
        if (_picker is not { } session)
        {
            return;
        }

        session.FilterChanged -= OnPickerFilterChanged;
        if (!_pickerFinished)
        {
            _pickerFinished = true;
            _pickerCompletion?.TrySetResult(session.Cancelled());
        }
    }
}
