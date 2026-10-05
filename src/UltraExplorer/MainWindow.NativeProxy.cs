using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer;

/// <summary>
/// What the replacement of another program's Windows file dialog
/// (Picker/Integration/NativeDialogProxy) needs of the window beyond an
/// ordinary picker - including being prepared before any dialog exists:
/// shown once, DWM-cloaked, and later bound to a dialog with
/// <see cref="RebindAsync"/> and uncloaked over it.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Set by the replacement of another program's dialog: puts the window
    /// over that dialog once its handle exists, before it is first shown (and
    /// after a test copy's own placement, which it may only refine).
    /// </summary>
    internal Action<nint>? PlaceOverCaller { get; private set; }

    /// <summary>A prepared picker never takes the keyboard by focusing something until it is handed over.</summary>
    private bool _keyboardWithheld;

    /// <summary>Neither a test copy nor a prepared picker moves the keyboard by itself (see <see cref="FocusCanvas(NestedPane)"/>).</summary>
    private bool WithholdsKeyboard => IsTestWindow || _keyboardWithheld;

    private readonly AcceptGate _acceptGate = new();
    private nint _preparedHandle;
    private TaskCompletionSource<bool>? _pickerContract;

    /// <summary>
    /// Before the window is shown: it goes where <paramref name="place"/> puts
    /// it, does not take the keyboard by itself (the replacement hands it over
    /// once the original is hidden), and writes nothing of the user's state
    /// but a mark changed in it.
    /// </summary>
    internal void PrepareAsNativeProxy(Action<nint> place)
    {
        PlaceOverCaller = place;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        _viewModel.Tree.SuppressWrites = true;
    }

    /// <summary>
    /// A picker made before any dialog needs it: shown (so its surface,
    /// renderer and first layout exist) but cloaked from its very first frame,
    /// never activated or focused, out of the taskbar. Its OK waits for
    /// <see cref="ConfirmContract"/> as well as for a name.
    /// </summary>
    internal void PrepareAsCloakedPicker(Action<nint> place)
    {
        _keyboardWithheld = true;
        ShowInTaskbar = false;
        DetachChanges();
        PrepareAsNativeProxy(handle =>
        {
            _preparedHandle = handle;
            ActivationGuard.Guard(handle);
            DialogNative.SetNoActivate(handle, true);
            place(handle);
            DialogNative.CloakOwn(handle, true);
        });
        Closed += (_, _) =>
        {
            _pickerClosed = true;
            ActivationGuard.Release(_preparedHandle);
            // An accept still waiting for the contract ends with the window.
            _pickerContract?.TrySetResult(false);
        };
        var enabled = new MultiBinding { Converter = AllTrueConverter.Instance };
        enabled.Bindings.Add(new Binding(nameof(FileDialogSession.CanAccept)));
        enabled.Bindings.Add(new Binding(nameof(AcceptGate.IsOpen)) { Source = _acceptGate });
        PickerAcceptButton.SetBinding(IsEnabledProperty, enabled);
    }

    /// <summary>Set while the canvas's folders, and the tree's, are not watched on disk.</summary>
    private bool _canvasUnwatched, _treeUnwatched;

    /// <summary>Set once a prepared picker has closed.</summary>
    private bool _pickerClosed;

    /// <summary>
    /// A prepared picker waits, bound to no dialog, for as long as the
    /// integration is on: meanwhile nothing on disk is watched for it - no
    /// volume armed, no folder registered, not the placeholder nor the folders
    /// on the way to it - so what changes on disk costs it nothing. A dialog
    /// binding it (<see cref="RebindAsync"/>) watches again: the canvas from
    /// its folder on, the tree behind it once the picker is on screen
    /// (<see cref="WatchChanges"/>).
    /// </summary>
    private void DetachChanges()
    {
        _canvasUnwatched = _treeUnwatched = true;
        _viewModel.Tree.Changes = null;
        foreach (var pane in _panes)
        {
            pane.Canvas.AttachChanges(null, null);
            pane.Tree.Changes = null;
        }
    }

    /// <summary>
    /// The canvas's folders watched again, as <see cref="NestedPane.Attach"/>
    /// first wired them: before the dialog's folder becomes a root, which
    /// takes its volume's watch from the hub as it is made.
    /// </summary>
    private void WatchCanvasChanges()
    {
        if (!_canvasUnwatched) return;
        _canvasUnwatched = false;
        var hub = _viewModel.Changes;
        foreach (var pane in _panes)
        {
            pane.Tree.Changes = hub;
            pane.Canvas.AttachChanges(hub, _viewModel.Tree);
        }
    }

    /// <summary>
    /// The tree behind the canvas watched again - the folders on the way to
    /// the dialog's, which the address shows - once the picker is on screen:
    /// registering them takes the interface thread several milliseconds,
    /// which the first frame does not wait for.
    /// </summary>
    internal void WatchChanges()
    {
        // Asked for after the frame: the picker may have been closed meanwhile.
        if (_pickerClosed) return;
        WatchCanvasChanges();
        if (!_treeUnwatched) return;
        _treeUnwatched = false;
        _viewModel.Tree.Changes = _viewModel.Changes;
    }

    /// <summary>The keyboard may come to this window now (a replacement in real use, never a test copy).</summary>
    internal void ReleaseKeyboard()
    {
        if (IsTestWindow) return;
        _keyboardWithheld = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        ActivationGuard.Release(handle);
        DialogNative.SetNoActivate(handle, false);
    }

    /// <summary>Until <see cref="ConfirmContract"/>: OK is disabled, and Enter or a double-click on a file waits for it.</summary>
    internal void HoldAccept()
    {
        _pickerContract?.TrySetResult(false);
        _pickerContract = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptGate.IsOpen = false;
    }

    /// <summary>The caller's whole contract - file types, options, multiple selection - is known and mirrored.</summary>
    internal void ConfirmContract()
    {
        _acceptGate.IsOpen = true;
        _pickerContract?.TrySetResult(true);
    }

    /// <summary>
    /// Waits, when the caller's contract is still being read, for it to be
    /// confirmed; false when it will not be (the dialog went back to Windows).
    /// </summary>
    private async Task<bool> WhenContractConfirmedAsync()
    {
        if (_pickerContract is not { Task.IsCompleted: false } contract) return _pickerContract?.Task.Result ?? true;
        return await contract.Task;
    }

    /// <summary>
    /// Binds this already shown picker to another request - a repeatable
    /// version of what the constructor and the first Loaded do for one.
    /// Everything the previous session put in the window goes: its title,
    /// footer, file-type row, places, selection limit, entry rules, the
    /// selection itself, Back and Forward, and the nested roots, which are
    /// replaced (never added to) so nothing piles up. The canvas starts at
    /// the new folder exactly as a newly made picker would.
    /// <para><paramref name="sameDialog"/>: the same dialog, now fully read
    /// (its file types, options and multiple selection known). The view, the
    /// selection and what the user has done meanwhile stay; only what follows
    /// the request changes, and the answer is still awaited on the same
    /// <see cref="PickerResult"/>.</para>
    /// </summary>
    internal async Task RebindAsync(FileDialogSession session, bool sameDialog = false)
    {
        var previous = _picker;
        if (previous is not null) previous.FilterChanged -= OnPickerFilterChanged;
        _picker = session;
        if (!sameDialog || _pickerCompletion is null)
        {
            var abandoned = _pickerCompletion;
            _pickerCompletion = new TaskCompletionSource<FileDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pickerFinished = false;
            abandoned?.TrySetResult(previous?.Cancelled() ?? FileDialogResult.Cancelled());
            HoldAccept();
        }

        if (!sameDialog)
        {
            await _viewModel.RefreshPickerNavigationPreferencesAsync();
            if (!ReferenceEquals(_picker, session)) return;
            foreach (var pane in _panes) pane.RebuildBeacons();
        }
        ShowPickerSession(session);
        _viewModel.Tree.Selection.MaxCount = int.MaxValue;
        FolderListItems.SelectionMode = SelectionMode.Extended;
        ConfigurePickerSelection();

        if (sameDialog)
        {
            if (previous is not null) session.CarryChoiceFrom(previous);
            // What is selected already is the new session's selection too -
            // unless the user typed a name since: the selection names the
            // file only while the name box still shows what it put there.
            var selected = _viewModel.Tree.Selection.Items.ToArray();
            if (NativeDialogRules.SelectionNamesFile(session, selected)) session.ReportSelection(selected);
            await ApplyPickerRulesAsync();
            return;
        }

        // As a new picker's Loaded: rules before the first read, the folder
        // within its physical parent chain and the camera on it - the canvas reads and draws
        // it from here - and the tree's own reveal of the folder (its
        // selection, address and first step of Back), which may have to ask
        // the disk about the folders on the way, meanwhile.
        _pickerNestedReady = false;
        _viewModel.History.Clear();
        _viewModel.Tree.Selection.Apply(new SelectionEdit { Clear = true, Source = SelectionSource.Navigation });
        if (IsNested)
        {
            // The old folder goes first, so the new rules re-filter nothing
            // but the new one, which is not read yet.
            _pickerNestedRoots.Clear();
            EnsurePickerVolumeRoot(_pickerStartFolder);
            WatchCanvasChanges();
            SyncNestedRoots();
        }
        await ApplyPickerRulesAsync();
        if (!ReferenceEquals(_picker, session)) return;
        if (IsNested)
        {
            foreach (var pane in _panes)
            {
                var start = await pane.Tree.MaterializePathAsync(_pickerStartFolder);
                if (!ReferenceEquals(_picker, session)) return;

                // Read by name, as a flight reads where it goes: a junction,
                // a link or a placeholder is never read for being drawn, and
                // the dialog would open on it empty.
                if (start is not null) _ = pane.Tree.LoadAsync(start);
            }
            // Native placement and footer changes can still be waiting for
            // layout. Frame the actual final viewport before it is uncloaked.
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_picker, session)) FramePickerStartFolder();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (!ReferenceEquals(_picker, session)) return;
        session.CurrentFolder = _pickerStartFolder;
        FocusManager.SetFocusedElement(this, PickerNameBox);
        await _viewModel.Tree.RevealPathAsync(_pickerStartFolder, focus: false);
        if (!ReferenceEquals(_picker, session)) return;
        session.CurrentFolder = _pickerStartFolder;
        _pickerNestedReady = IsNested;
    }

    /// <summary>
    /// A test copy's picture of the replacement (<c>--capture</c>), once it is
    /// on screen and ready: rendering the window to a file takes the
    /// interface thread some 50 ms, which in the middle of binding would
    /// delay the very frame the checks time.
    /// </summary>
    internal void CaptureForTests() => _capture?.Start();

    /// <summary>For the checks and the log: how many roots the tile canvas holds now.</summary>
    internal int PickerRootCount => IsNested ? ActivePane.Tree.Root.AllChildren.Length : 0;

    /// <summary>Exact roots for the owned native-dialog fixture's private diagnostics.</summary>
    internal string[] PickerRootPaths => IsNested
        ? ActivePane.Tree.Root.AllChildren.Select(folder => folder.FullPath).ToArray() : [];

    private void FramePickerStartFolder()
    {
        UpdateLayout();
        if (IsNested && ActivePane.Tree.Find(_pickerStartFolder) is { } start)
            ActivePane.Canvas.FlyTo(start, 0.92, animated: false);
    }

    /// <summary>
    /// Completes once the folder the picker is bound to has been read into the
    /// canvas, a frame showing it has been drawn, and one more frame has come
    /// round (so the one that drew it has been handed to the compositor); or
    /// false after <paramref name="limit"/>. The window may be cloaked
    /// meanwhile: it is rendered all the same.
    /// </summary>
    internal async Task<bool> WhenFolderDrawnAsync(TimeSpan limit)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pane = IsNested ? ActivePane : null;
        var folder = _pickerStartFolder;
        var completion = _pickerCompletion;
        long mark = -1;
        TimeSpan? drawnAt = null;
        void OnFrame(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(completion, _pickerCompletion) || _pickerFinished)
            {
                done.TrySetResult(false);
                return;
            }
            var time = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
            if (drawnAt is { } drawn)
            {
                if (time != drawn) done.TrySetResult(true);
                return;
            }
            if (pane is null) { drawnAt = time; return; }
            if (mark < 0)
            {
                if (pane.Tree.Find(folder) is { IsLoaded: true } start)
                {
                    // A late DPI/size change must not leave the first frame
                    // as a small overview. Once the user acts, their camera wins.
                    if (_picker is { HasInteracted: false }
                        && pane.Canvas.ScreenRectOf(start) is { } rect
                        && rect.Height < pane.Canvas.ActualHeight * 0.75)
                        FramePickerStartFolder();
                    mark = pane.Canvas.LoopFrameCount;
                }
                return;
            }
            if (pane.Canvas.LoopFrameCount > mark || pane.Canvas.IsIdle) drawnAt = time;
        }

        CompositionTarget.Rendering += OnFrame;
        try { return await done.Task.WaitAsync(limit); }
        catch (TimeoutException) { return false; }
        finally { CompositionTarget.Rendering -= OnFrame; }
    }

    /// <summary>The OK button's own condition and the contract's, both.</summary>
    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public static readonly AllTrueConverter Instance = new();
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.All(value => value is true);
        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class AcceptGate : ObservableObject
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }
    }
}
