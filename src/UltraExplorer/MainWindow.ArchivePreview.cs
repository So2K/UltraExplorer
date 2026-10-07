using System.ComponentModel;
using UltraExplorer.Models;
using UltraExplorer.Services.Archives;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow
{
    private long _archiveQuickPreviewRequest;
    private CancellationTokenSource? _archiveQuickPreviewReads;
    private ItemSelection? _archiveQuickPreviewSelection;
    private bool _archiveQuickPreviewAttached, _archiveQuickPreviewClosed;

    // The production resolver always extracts a real archive entry. A held
    // resolver lets the owned fixture verify late, non-cooperative completion.
    internal Func<string, CancellationToken, Func<string, string?>, Task<string>>? ArchivePreviewExtractorForChecks { get; set; }
    internal Func<string, CancellationToken, Task<bool>>? ArchivePreviewClassifierForChecks { get; set; }
    internal Task ArchiveQuickPreviewLoadingForChecks { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Handles an explicitly requested archive candidate without checking the
    /// filesystem on the dispatcher. Virtual files present a read-only copy
    /// with their original archive path; physical candidates present normally.
    /// Other files return false after cancelling an older archive request.
    /// </summary>
    internal bool TryBeginArchiveQuickPreview(string path, Action<string, bool, string> present)
    {
        CancelArchiveQuickPreview();
        if (!MayContainArchivePreviewName(path)) return false;
        if (_archiveQuickPreviewClosed || _previewDetached || _closeRequested || _viewModel.IsDisposed) return true;

        AttachArchiveQuickPreview();
        var request = _archiveQuickPreviewRequest;
        var reads = new CancellationTokenSource();
        var selection = _viewModel.Tree.Selection;
        var version = selection.Version;
        var pane = ActivePane;
        var nested = IsNested;
        _archiveQuickPreviewReads = reads;
        _archiveQuickPreviewSelection = selection;
        selection.Changed += ArchiveQuickPreviewSelectionChanged;
        var extractor = ArchivePreviewExtractorForChecks;
        var classifier = ArchivePreviewClassifierForChecks;
        ArchiveQuickPreviewLoadingForChecks = LoadArchiveQuickPreviewAsync();
        return true;

        bool Current(bool archive = false) => !reads.IsCancellationRequested && request == _archiveQuickPreviewRequest
            && !_archiveQuickPreviewClosed && !_previewDetached && !_closeRequested && !_viewModel.IsDisposed
            && (!archive || _viewModel.BrowseArchives && !IsPickerMode)
            && ReferenceEquals(pane, ActivePane) && nested == IsNested
            && ReferenceEquals(selection, _viewModel.Tree.Selection) && version == selection.Version;

        async Task LoadArchiveQuickPreviewAsync()
        {
            try
            {
                var inside = classifier is null
                    ? await Task.Run(() => ArchiveService.IsInsideArchive(path), reads.Token)
                    : await classifier(path, reads.Token);
                if (!Current()) return;
                if (!inside)
                {
                    ReleaseArchiveQuickPreviewRequest(reads);
                    present(path, false, string.Empty);
                    return;
                }
                if (!_viewModel.BrowseArchives || IsPickerMode)
                {
                    _viewModel.Toast.ShowError("Archive folders are disabled. Enable them in Settings to preview these items.");
                    return;
                }
                var physical = extractor is null
                    ? await ArchiveService.ExtractForOpenAsync(path, null, reads.Token, _viewModel.AskArchivePassword)
                    : await extractor(path, reads.Token, _viewModel.AskArchivePassword);
                if (!Current(archive: true)) return;

                // The callback may enter the ordinary physical-preview path,
                // which cancels pending archive work. Release this request's
                // hooks before calling it so that cancellation cannot reenter
                // and tear down the preview being delivered now.
                ReleaseArchiveQuickPreviewRequest(reads);
                present(physical, true, path);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (Current()) _viewModel.Toast.ShowError(exception.Message);
            }
            finally
            {
                ReleaseArchiveQuickPreviewRequest(reads);
                reads.Dispose();
            }
        }
    }

    private static bool MayContainArchivePreviewName(string path)
    {
        var remaining = path.AsSpan();
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOfAny('\\', '/');
            var segment = separator < 0 ? remaining : remaining[..separator];
            if (ArchiveFormats.IsBrowsable(segment)) return true;
            if (separator < 0) break;
            remaining = remaining[(separator + 1)..];
        }
        return false;
    }

    internal void CancelArchiveQuickPreview()
    {
        _archiveQuickPreviewRequest++;
        var reads = _archiveQuickPreviewReads;
        if (reads is null) return;
        ReleaseArchiveQuickPreviewRequest(reads);
        // Disposal belongs to the reader's finally; its continuation may
        // still need the token after a cancelled native extraction returns.
        reads.Cancel();
    }

    private void ReleaseArchiveQuickPreviewRequest(CancellationTokenSource reads)
    {
        if (!ReferenceEquals(reads, _archiveQuickPreviewReads)) return;
        _archiveQuickPreviewReads = null;
        if (_archiveQuickPreviewSelection is { } selection)
            selection.Changed -= ArchiveQuickPreviewSelectionChanged;
        _archiveQuickPreviewSelection = null;
    }

    private void AttachArchiveQuickPreview()
    {
        if (_archiveQuickPreviewAttached) return;
        _archiveQuickPreviewAttached = true;
        Closing += ArchiveQuickPreviewClosing;
        Closed += ArchiveQuickPreviewClosed;
        _viewModel.PropertyChanged += ArchiveQuickPreviewPreferenceChanged;
    }

    private void ArchiveQuickPreviewSelectionChanged(ItemSelection selection) => CancelArchiveQuickPreview();
    private void ArchiveQuickPreviewClosing(object? sender, CancelEventArgs args) => CancelArchiveQuickPreview();
    private void ArchiveQuickPreviewPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.BrowseArchives) && !_viewModel.BrowseArchives
            || args.PropertyName is nameof(MainViewModel.ActivePaneIndex) or nameof(MainViewModel.Layout)
                or nameof(MainViewModel.IsSplit) or nameof(MainViewModel.SplitOrientation))
            CancelArchiveQuickPreview();
    }
    private void ArchiveQuickPreviewClosed(object? sender, EventArgs args)
    {
        _archiveQuickPreviewClosed = true;
        CancelArchiveQuickPreview();
        Closing -= ArchiveQuickPreviewClosing;
        Closed -= ArchiveQuickPreviewClosed;
        _viewModel.PropertyChanged -= ArchiveQuickPreviewPreferenceChanged;
        _archiveQuickPreviewAttached = false;
        ArchivePreviewExtractorForChecks = null;
        ArchivePreviewClassifierForChecks = null;
    }
}
