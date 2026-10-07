using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;

namespace UltraExplorer;

/// <summary>One owned, resizable Quick Look window; all file work stays off the dispatcher.</summary>
internal sealed class QuickPreviewWindow : Window
{
    private readonly Grid _body = new() { Focusable = true };
    private readonly WrapPanel _tools = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _zoomTools = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { Margin = new Thickness(14, 9, 14, 9), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _picture;
    private readonly TextEditor _text = new()
    {
        IsReadOnly = true, ShowLineNumbers = true,
        FontFamily = new FontFamily("Consolas"), FontSize = 13,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0), Padding = new Thickness(18), WordWrap = false
    };
    private readonly Button _save, _discard, _previous, _next;
    private Task<bool>? _pendingSave;
    private long _openRequest;
    private IReadOnlyList<string> _files = [];
    private readonly Button _previousFile, _nextFile;
    private readonly Button _delete;
    private readonly Button _open, _openWith;
    private ContextMenu? _openMenu;
    private CancellationTokenSource? _menuLoad;
    private bool _openingExternal;
    private bool _panningImage;
    private Point _imagePanStart, _imagePanOffsets;
    private bool _forceReadOnly;
    private bool _deleting;
    private bool _ownerCloseFrozen, _ownerCloseWasEnabled;
    private bool _activatedOnce;
    private readonly SearchPanel _search;
    private CancellationTokenSource _load = new();
    private NativePreviewHost? _native;
    private PreviewTextEditSession? _editor;
    private DocumentPreviewData? _document;
    private string _path = "";
    private string? _originPath;
    private int _page, _pageCount, _generation;
    private bool _dirty, _closed, _saving, _allowClose;
    private bool _askingClose;
    private bool _renderingPdf;
    private double _zoom = 1;
    private bool _fit = true;

    internal QuickPreviewWindow()
    {
        Title = "Quick Look — UltraExplorer";
        Width = 880; Height = 640; MinWidth = 480; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _text.SetResourceReference(BackgroundProperty, "CanvasBrush");
        _text.SetResourceReference(ForegroundProperty, "TextBrush");
        _text.LineNumbersForeground = (Brush)FindResource("TextDimBrush");
        _search = SearchPanel.Install(_text.TextArea);
        _search.SetResourceReference(BackgroundProperty, "SurfaceRaisedBrush");
        _search.SetResourceReference(ForegroundProperty, "TextBrush");
        _picture = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, CanContentScroll = false, PanningMode = PanningMode.Both };
        _picture.PreviewMouseLeftButtonDown += PicturePanStart;
        _picture.PreviewMouseMove += PicturePanMove;
        _picture.PreviewMouseLeftButtonUp += PicturePanEnd;
        _picture.LostMouseCapture += (_, _) => EndImagePan();
        _body.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x15));
        var root = new Grid();
        root.SetResourceReference(BackgroundProperty, "WindowBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(10, 6, 10, 6), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var openGroup = new StackPanel { Orientation = Orientation.Horizontal };
        _open = Button("Open", () => _ = OpenExternalAsync(false));
        _open.SetResourceReference(StyleProperty, "AccentButton");
        _open.ToolTip = "Open this file in its default application";
        _open.FontWeight = FontWeights.SemiBold;
        _openWith = Button("▾", () => _ = OpenWithMenuAsync(openGroup));
        _openWith.SetResourceReference(StyleProperty, "AccentButton");
        _openWith.Padding = new Thickness(8, 6, 8, 6);
        _openWith.ToolTip = "Open with another application";
        AutomationProperties.SetName(_openWith, "Open with another application");
        openGroup.Children.Add(_open); openGroup.Children.Add(_openWith);
        actions.Children.Add(openGroup);
        _delete = Button("\uE74D", () => _ = DeleteCurrentAsync());
        _delete.FontFamily = new FontFamily("Segoe MDL2 Assets");
        _delete.Margin = new Thickness(12, 0, 0, 0);
        _delete.ToolTip = "Move this file to the Recycle Bin";
        AutomationProperties.SetName(_delete, "Delete — move this file to the Recycle Bin");
        actions.Children.Add(_delete);
        DockPanel.SetDock(actions, Dock.Right); top.Children.Add(actions);
        _previousFile = Button("‹ File", () => _ = NavigateFileAsync(-1));
        _nextFile = Button("File ›", () => _ = NavigateFileAsync(1));
        _tools.Children.Add(_previousFile); _tools.Children.Add(_nextFile);
        _previous = Button("‹", () => _ = LoadPdfAsync(_page - 1)); _previous.ToolTip = "Previous page (Left)";
        _next = Button("›", () => _ = LoadPdfAsync(_page + 1)); _next.ToolTip = "Next page (Right)";
        _tools.Children.Add(_previous); _tools.Children.Add(_pageLabel); _tools.Children.Add(_next);
        _zoomTools.Children.Add(Button("−", () => ZoomBy(1 / 1.25)));
        _zoomTools.Children.Add(Button("+", () => ZoomBy(1.25)));
        _zoomTools.Children.Add(Button("Fit", () => { _fit = true; ApplyImageSize(); }));
        _tools.Children.Add(_zoomTools);
        _save = Button("Save", () => _ = SaveAsync());
        _discard = Button("Discard changes", () => { if (_ownerCloseFrozen) return; _allowClose = true; _dirty = false; Close(); });
        _tools.Children.Add(_save); _tools.Children.Add(_discard);
        top.Children.Add(_tools);
        root.Children.Add(top); Grid.SetRow(_body, 1); root.Children.Add(_body);
        Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
        _text.TextChanged += (_, _) => { if (_editor is not null) { _dirty = _text.Text != _editor.Text; UpdateSave(); } };
        _picture.SizeChanged += (_, _) => { if (_fit) ApplyImageSize(); };
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { if (_ownerCloseFrozen || !ShowActivated) return; if (_editor is null) _body.Focus(); else _text.Focus(); };
        Activated += (_, _) =>
        {
            if (!_activatedOnce) { _activatedOnce = true; return; }
            _ = RefreshTextAfterActivationAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; EndImagePan(); CloseOpenMenu(); _load.Cancel(); _load.Dispose(); _native?.Dispose(); };
        ResetTools();
    }

    private static Button Button(string label, Action run)
    {
        var button = new Button { Content = label, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(10, 6, 10, 6), MinHeight = 32 };
        button.SetResourceReference(StyleProperty, "FlatButton");
        button.Click += (_, _) => run();
        return button;
    }

    internal string FilePath => _path;
    internal void SetFileSequence(IReadOnlyList<string> files) { if (_ownerCloseFrozen || _closed) return; _files = files.ToArray(); UpdateFileNavigation(); }
    private int CurrentFileIndex()
    {
        for (var index = 0; index < _files.Count; index++)
            if (string.Equals(_files[index], _path, StringComparison.Ordinal)) return index;
        return -1;
    }
    private void UpdateFileNavigation()
    {
        var index = CurrentFileIndex();
        _previousFile.Visibility = _nextFile.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _previousFile.IsEnabled = !_ownerCloseFrozen && index > 0;
        _nextFile.IsEnabled = !_ownerCloseFrozen && index >= 0 && index + 1 < _files.Count;
    }
    internal async Task NavigateFileAsync(int direction)
    {
        if (_ownerCloseFrozen || _closed) return;
        var current = CurrentFileIndex();
        if (current < 0) return;
        var index = current + Math.Sign(direction);
        if (index < 0 || index >= _files.Count || direction == 0) return;
        var origin = string.IsNullOrEmpty(_originPath) ? null
            : Path.Combine(Path.GetDirectoryName(_originPath) ?? "", Path.GetFileName(_files[index]));
        OpenFile(_files[index], readOnly: _forceReadOnly, originPath: origin);
        await Loading;
    }
    internal Task RefreshTextAfterActivationAsync()
    {
        if (!_closed && !_ownerCloseFrozen && !_dirty && !_saving && _document is not null)
        { OpenFile(_path, readOnly: _forceReadOnly, originPath: _originPath); return Loading; }
        return Task.CompletedTask;
    }
    internal bool HasUnsavedChanges => _dirty || _saving;
    internal Task Loading { get; private set; } = Task.CompletedTask;
    internal void OpenFile(string path, bool readOnly = false, string? originPath = null)
    {
        if (_closed || _ownerCloseFrozen) return;
        var request = ++_openRequest;
        Loading = _dirty || _saving ? SaveAndOpenAsync(path, request, readOnly, originPath) : StartOpenFile(path, readOnly, originPath);
    }
    private async Task SaveAndOpenAsync(string path, long request, bool readOnly, string? originPath)
    {
        if (!await CommitForCloseAsync() || _closed || _ownerCloseFrozen || request != _openRequest) return;
        await StartOpenFile(path, readOnly, originPath);
    }
    private Task StartOpenFile(string path, bool readOnly = false, string? originPath = null)
    {
        _forceReadOnly = readOnly;
        _originPath = readOnly ? originPath : null;
        _delete.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        _delete.IsEnabled = !readOnly && !_deleting;
        _path = path; UpdateTitle();
        UpdateFileNavigation();
        _load.Cancel(); _load.Dispose(); _load = new CancellationTokenSource();
        EndImagePan(); CloseOpenMenu();
        _generation++; _native?.Dispose(); _native = null; _editor = null; _document = null;
        _dirty = false; _renderingPdf = false; _page = 0; _pageCount = 0; _fit = true; _zoom = 1;
        _image.Source = null; _text.Text = ""; _text.IsReadOnly = true; ResetTools();
        var syntax = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
        if (syntax is not null)
            foreach (var color in syntax.NamedHighlightingColors)
            {
                var name = color.Name?.ToLowerInvariant() ?? "";
                color.Foreground = new SimpleHighlightingBrush(name.Contains("comment") ? Color.FromRgb(0x89, 0xA5, 0x81)
                    : name.Contains("string") ? Color.FromRgb(0xE8, 0xC7, 0x94)
                    : name.Contains("keyword") ? Color.FromRgb(0x60, 0xCD, 0xFF)
                    : name.Contains("number") ? Color.FromRgb(0xB5, 0xCE, 0xA8)
                    : Color.FromRgb(0xD4, 0xD4, 0xD4));
            }
        _text.SyntaxHighlighting = syntax;
        _body.Children.Clear(); Status("Loading preview…");
        return LoadFileAsync(path, _generation, _load.Token);
    }

    private async Task LoadFileAsync(string path, int generation, CancellationToken token)
    {
        try
        {
            // Do not hydrate cloud placeholders simply to view their content.
            var readable = await Task.Run(() => new FileInfo(DocumentPreviewService.LiteralPath(path)) is { Exists: true } f
                && new ThumbnailFileStamp(f.Length, f.LastWriteTimeUtc.Ticks, (uint)f.Attributes).CanReadContent, token);
            if (!Current(generation)) return;
            if (!readable) throw new IOException("This file is unavailable locally. Download it first, then reopen the preview.");
            if (Path.GetExtension(path.TrimEnd(' ', '.')).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await LoadPdfAsync(0); return;
            }
            if (PreviewImageService.Supports(path))
            {
                var image = await PreviewImageService.ReadAsync(path, 4096, token);
                if (!Current(generation)) return;
                if (image is not null && !image.IsAnimated)
                {
                    ShowImage(image.Image);
                    Status($"{image.OriginalWidth:N0} × {image.OriginalHeight:N0} · Wheel to zoom · Drag to pan · Ctrl+wheel to scroll");
                    return;
                }
            }
            if (PreviewTools.IsModel(path) || PreviewTools.IsMedia(path))
            {
                _native = new NativePreviewHost(); _body.Children.Add(_native);
                await _native.LoadAsync(path, token);
                if (Current(generation)) Status(PreviewTools.IsModel(path) ? "Drag to orbit · Wheel to zoom" : "Space to play or pause · Escape to close");
                return;
            }
            var document = await DocumentPreviewService.ReadAsync(path, 2 * 1024 * 1024, token);
            if (!Current(generation)) return;
            if (document is not null)
            {
                _document = document; _text.Text = document.Text; _body.Children.Add(_text);
                Status(document.Detail);
                if (!_forceReadOnly && document.CanEdit && !document.Truncated && !document.IsBinary)
                    await PrepareTextEditorAsync(path, document, generation, token);
                return;
            }
            using var thumbnails = new FileThumbnailService();
            var thumbnail = await thumbnails.GetAsync(path, token).WaitAsync(TimeSpan.FromSeconds(8), token);
            if (!Current(generation)) return;
            if (thumbnail is null) throw new IOException("No preview is available. Use Open in app to view this file.");
            ShowImage(thumbnail.Image); Status(thumbnail.Detail ?? "File preview");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Current(generation)) ShowError(e.Message); }
    }

    private async Task LoadPdfAsync(int page)
    {
        if (_closed || _renderingPdf || (_pageCount > 0 && (page < 0 || page >= _pageCount))) return;
        var generation = ++_generation;
        _renderingPdf = true;
        _previous.IsEnabled = _next.IsEnabled = false; Status("Rendering page…");
        try
        {
            var rendered = await PdfPreviewService.RenderAsync(_path, page, 1800, _load.Token);
            if (!Current(generation)) return;
            _page = page; _pageCount = rendered.PageCount; ShowImage(rendered.Image);
            _previous.Visibility = _next.Visibility = _pageLabel.Visibility = Visibility.Visible;
            _pageLabel.Text = $"{_page + 1} / {_pageCount}";
            _previous.IsEnabled = _page > 0; _next.IsEnabled = _page + 1 < _pageCount;
            Status("PDF · Left/Right to change page · Ctrl+wheel to zoom");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Current(generation)) ShowError(e.Message); }
        finally { if (Current(generation)) _renderingPdf = false; }
    }

    private void ShowImage(BitmapSource image)
    {
        _body.Children.Clear(); _body.Children.Add(_picture); _image.Source = image;
        if (IsActive) _body.Focus();
        _zoomTools.Visibility = Visibility.Visible;
        _picture.HorizontalScrollBarVisibility = _picture.VerticalScrollBarVisibility = _pageCount > 0
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        ApplyImageSize();
    }

    private void ApplyImageSize()
    {
        if (_image.Source is not BitmapSource image) return;
        var scale = _fit ? Math.Min(Math.Max(100, _picture.ActualWidth - 28) / image.PixelWidth,
            Math.Max(100, _picture.ActualHeight - 28) / image.PixelHeight) : _zoom;
        _zoom = Math.Clamp(scale, 0.05, 8);
        _image.Width = image.PixelWidth * _zoom; _image.Height = image.PixelHeight * _zoom;
    }

    private void ZoomBy(double factor) => ZoomAt(factor, new Point(_picture.ViewportWidth / 2, _picture.ViewportHeight / 2));
    internal void ZoomAt(double factor, Point anchor)
    {
        if (_image.Source is null || _ownerCloseFrozen || _closed || !double.IsFinite(factor) || factor <= 0) return;
        _picture.UpdateLayout();
        var oldOrigin = _image.TranslatePoint(new Point(), _picture);
        var relative = new Point((anchor.X - oldOrigin.X) / Math.Max(_zoom, 0.001),
            (anchor.Y - oldOrigin.Y) / Math.Max(_zoom, 0.001));
        _fit = false; _zoom = Math.Clamp(_zoom * factor, 0.05, 8); ApplyImageSize();
        _picture.UpdateLayout();
        var newOrigin = _image.TranslatePoint(new Point(), _picture);
        _picture.ScrollToHorizontalOffset(_picture.HorizontalOffset + newOrigin.X + relative.X * _zoom - anchor.X);
        _picture.ScrollToVerticalOffset(_picture.VerticalOffset + newOrigin.Y + relative.Y * _zoom - anchor.Y);
    }

    internal bool HandleImageWheel(int delta, ModifierKeys modifiers, Point anchor)
    {
        if (_image.Source is null || !_body.Children.Contains(_picture) || _ownerCloseFrozen || _closed || delta == 0) return false;
        if (_pageCount > 0)
        {
            if (modifiers != ModifierKeys.Control) return false;
            ZoomAt(Math.Pow(1.25, delta / 120.0), anchor); return true;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if ((modifiers & ModifierKeys.Shift) != 0) _picture.ScrollToHorizontalOffset(_picture.HorizontalOffset - delta);
            else _picture.ScrollToVerticalOffset(_picture.VerticalOffset - delta);
            return true;
        }
        if (modifiers != ModifierKeys.None) return false;
        ZoomAt(Math.Pow(1.25, delta / 120.0), anchor); return true;
    }
    private void PicturePanStart(object sender, MouseButtonEventArgs e)
    {
        if (_image.Source is null || _ownerCloseFrozen || _closed || _pageCount > 0
            || (_picture.ScrollableWidth <= 0 && _picture.ScrollableHeight <= 0)) return;
        _imagePanStart = e.GetPosition(_picture);
        _imagePanOffsets = new Point(_picture.HorizontalOffset, _picture.VerticalOffset);
        _panningImage = _picture.CaptureMouse();
        if (_panningImage) { _picture.Cursor = Cursors.Hand; e.Handled = true; }
    }
    private void PicturePanMove(object sender, MouseEventArgs e)
    {
        if (!_panningImage) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndImagePan(); return; }
        var at = e.GetPosition(_picture);
        _picture.ScrollToHorizontalOffset(_imagePanOffsets.X + _imagePanStart.X - at.X);
        _picture.ScrollToVerticalOffset(_imagePanOffsets.Y + _imagePanStart.Y - at.Y);
        e.Handled = true;
    }
    private void PicturePanEnd(object sender, MouseButtonEventArgs e)
    { if (_panningImage) { EndImagePan(); e.Handled = true; } }
    private void EndImagePan()
    {
        _panningImage = false;
        _picture.Cursor = null;
        if (_picture.IsMouseCaptured) _picture.ReleaseMouseCapture();
    }

    private async Task PrepareTextEditorAsync(string path, DocumentPreviewData document, int generation, CancellationToken token)
    {
        try
        {
            var editor = await Task.Run(() => PreviewTextEditSession.OpenAsync(path, document.EncodingName ?? "utf-8", document.HasBom, token), token);
            if (!Current(generation)) return;
            _editor = editor; _text.Text = editor.Text; _text.IsReadOnly = _ownerCloseFrozen;
            _save.Visibility = Visibility.Visible; UpdateSave();
            if (!_ownerCloseFrozen && IsActive && ShowActivated) _text.Focus();
            Status("Text · Changes save when you close · Ctrl+S to save now");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Current(generation)) Status("Read-only preview · " + e.Message); }
    }

    private Task<bool> SaveAsync()
    {
        if (_pendingSave is { IsCompleted: false } pending) return pending;
        if (_editor is null || !_dirty) return Task.FromResult(true);
        return _pendingSave = SaveCoreAsync();
    }
    private async Task<bool> SaveCoreAsync()
    {
        _saving = true; UpdateSave();
        try
        {
            var text = _text.Text;
            var editor = _editor!;
            await Task.Run(() => editor.SaveAsync(text, _load.Token), _load.Token);
            _dirty = _text.Text != editor.Text;
            _discard.Visibility = Visibility.Collapsed;
            Status("Saved · " + Path.GetFileName(_path));
            return !_dirty;
        }
        catch (Exception e) { Status(e.Message); _discard.Visibility = Visibility.Visible; return false; }
        finally { _saving = false; UpdateSave(); }
    }
    internal async Task<bool> CommitForCloseAsync()
    {
        var request = _openRequest; var editor = _editor;
        var saved = await SaveAsync();
        return saved && !_dirty && !_saving && request == _openRequest && ReferenceEquals(editor, _editor);
    }
    internal Task<bool> ConfirmCloseAsync() => CommitForCloseAsync();
    internal bool IsOwnerCloseFrozen => _ownerCloseFrozen;
    internal string? SourceOriginForChecks => _originPath;
    internal void BeginOwnerClose()
    {
        if (_closed || _ownerCloseFrozen) return;
        _ownerCloseFrozen = true;
        _ownerCloseWasEnabled = IsEnabled;
        // Invalidate actions which were already awaiting a save when the
        // owner began closing. The current editor/session remains available
        // for its own final commit.
        _openRequest++;
        IsEnabled = false;
        _text.IsReadOnly = true;
        _delete.IsEnabled = false;
        EndImagePan(); CloseOpenMenu(); UpdateOpenActions();
        UpdateFileNavigation(); UpdateSave();
    }
    internal void EndOwnerClose()
    {
        if (_closed || !_ownerCloseFrozen) return;
        _ownerCloseFrozen = false;
        IsEnabled = _ownerCloseWasEnabled;
        _text.IsReadOnly = _editor is null || _forceReadOnly || _deleting;
        _delete.IsEnabled = !_forceReadOnly && !_deleting;
        UpdateOpenActions();
        UpdateFileNavigation(); UpdateSave();
    }
    internal void CompleteOwnerClose()
    {
        if (_closed) return;
        _allowClose = true;
        Close();
    }
    internal async Task OpenExternalAsync(bool chooseApp, string? handlerId = null)
    {
        if (_ownerCloseFrozen || _closed || _openingExternal) return;
        var path = _path; var request = _openRequest;
        var token = _load.Token;
        var handle = new WindowInteropHelper(this).Handle;
        _openingExternal = true; UpdateOpenActions();
        bool CurrentRequest() => !token.IsCancellationRequested && !Volatile.Read(ref _closed)
            && !Volatile.Read(ref _ownerCloseFrozen) && request == Volatile.Read(ref _openRequest)
            && string.Equals(path, Volatile.Read(ref _path), StringComparison.Ordinal);
        try
        {
            if (!await CommitForCloseAsync() || !CurrentRequest()) return;
            var result = handlerId is not null
                ? await PreviewOpenWithService.InvokeAsync(path, handlerId, handle, token, CurrentRequest)
                : chooseApp ? await PreviewOpenWithService.ChooseAsync(path, handle, token, CurrentRequest)
                : await PreviewOpenWithService.OpenDefaultAsync(path, handle, token, CurrentRequest);
            if (!CurrentRequest()) return;
            if (result == PreviewOpenResult.NeedsChoice)
                await OpenWithMenuAsync(_openWith);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (CurrentRequest()) Status("Could not open this file · " + e.Message); }
        finally { _openingExternal = false; UpdateOpenActions(); }
    }
    internal async Task OpenWithMenuAsync(FrameworkElement target)
    {
        if (_ownerCloseFrozen || _closed) return;
        CloseOpenMenu();
        var request = _openRequest; var path = _path;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_load.Token);
        _menuLoad = cancellation;
        var menu = new ContextMenu { PlacementTarget = target, MaxHeight = 520 };
        _openMenu = menu;
        menu.Items.Add(new MenuItem { Header = "Loading applications…", IsEnabled = false });
        menu.Closed += (_, _) => { if (ReferenceEquals(_openMenu, menu)) CloseOpenMenu(); };
        menu.IsOpen = true;
        try
        {
            var apps = await PreviewOpenWithService.ListAsync(path, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closed || _ownerCloseFrozen || request != _openRequest
                || !ReferenceEquals(_openMenu, menu)) return;
            menu.Items.Clear();
            foreach (var app in apps)
            {
                var item = new MenuItem { Header = app.Name };
                if (app.Icon is not null) item.Icon = new Image { Source = app.Icon, Width = 18, Height = 18 };
                var id = app.Id;
                item.Click += (_, _) => { if (request == _openRequest && !_ownerCloseFrozen && !_closed) _ = OpenExternalAsync(false, id); };
                menu.Items.Add(item);
            }
            if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = "No applications registered for this file", IsEnabled = false });
            menu.Items.Add(new Separator());
            var choose = new MenuItem { Header = "Choose another app…" };
            choose.Click += (_, _) => { if (request == _openRequest && !_ownerCloseFrozen && !_closed) _ = OpenExternalAsync(true); };
            menu.Items.Add(choose);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ReferenceEquals(_openMenu, menu) || _closed || _ownerCloseFrozen) return;
            menu.Items.Clear();
            var choose = new MenuItem { Header = "Choose another app…" };
            choose.Click += (_, _) => { if (request == _openRequest && !_ownerCloseFrozen && !_closed) _ = OpenExternalAsync(true); };
            menu.Items.Add(choose);
        }
    }
    private void CloseOpenMenu()
    {
        var menu = _openMenu; _openMenu = null;
        var cancellation = _menuLoad; _menuLoad = null;
        if (menu is not null) menu.IsOpen = false;
        cancellation?.Cancel(); cancellation?.Dispose();
    }
    private void UpdateOpenActions() => _open.IsEnabled = _openWith.IsEnabled = !_closed && !_ownerCloseFrozen && !_openingExternal;
    internal async Task DeleteCurrentAsync(Func<string, Task>? recycleForChecks = null)
    {
        if (_forceReadOnly || _deleting || _ownerCloseFrozen || _closed) return;
        var path = _path; var request = _openRequest;
        if (!await CommitForCloseAsync() || _closed) return;
        if (_ownerCloseFrozen || request != _openRequest || !string.Equals(path, _path, StringComparison.Ordinal) || _forceReadOnly || _deleting) return;
        _deleting = true;
        _delete.IsEnabled = false;
        var editor = _editor;
        _text.IsReadOnly = true;
        Status("Moving to the Recycle Bin…");
        try
        {
            _native?.Dispose(); _native = null;
            if (recycleForChecks is not null) await recycleForChecks(path);
            else await new NativeShellService().DeleteAsync([path], permanently: false);
            if (_closed || request != _openRequest || !string.Equals(path, _path, StringComparison.Ordinal)) return;
            var at = CurrentFileIndex();
            var remaining = _files.Where(file => !string.Equals(file, path, StringComparison.Ordinal)).ToArray();
            if (remaining.Length > 0)
            { _files = remaining; OpenFile(remaining[Math.Clamp(at, 0, remaining.Length - 1)]); }
            else { _allowClose = true; Close(); }
        }
        catch (Exception e)
        {
            if (_closed || request != _openRequest) return;
            await StartOpenFile(path);
            Status(e.Message);
        }
        finally
        {
            _deleting = false;
            if (!_closed)
            {
                _delete.IsEnabled = !_forceReadOnly && !_ownerCloseFrozen;
                // Owner-close cancellation changes the request number without
                // replacing the current editor. Restore that editor's current
                // capability after a late recycle failure as well.
                if (ReferenceEquals(editor, _editor) && string.Equals(path, _path, StringComparison.Ordinal))
                    _text.IsReadOnly = _editor is null || _forceReadOnly || _ownerCloseFrozen;
            }
        }
    }
    private void UpdateSave() { _save.IsEnabled = _dirty && !_saving && !_ownerCloseFrozen; _discard.IsEnabled = _dirty && !_saving && !_ownerCloseFrozen; UpdateTitle(); }
    private void UpdateTitle() => Title = (_dirty ? "● " : "") + Path.GetFileName(_originPath ?? _path)
        + (string.IsNullOrEmpty(_originPath) ? " — Quick Look" : " — Archive preview (read-only)");
    private void ResetTools()
    {
        _save.Visibility = _discard.Visibility = _previous.Visibility = _next.Visibility = _pageLabel.Visibility = Visibility.Collapsed;
        _zoomTools.Visibility = Visibility.Collapsed;
    }
    private void Status(string message) { var label = string.IsNullOrEmpty(_originPath) ? message : $"Read-only archive preview · {_originPath} · {message}"; _status.Text = label; _status.ToolTip = label; }
    private void ShowError(string message)
    {
        Status(message);
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32),
            Foreground = (Brush)FindResource("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 600 });
    }
    private bool Current(int generation) => !_closed && generation == _generation && !_load.IsCancellationRequested;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_ownerCloseFrozen) { e.Handled = true; return; }
        if (e.Key == Key.Escape && !_search.IsClosed) { _search.Close(); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && _native is { IsMediaLoaded: true } media)
        {
            if (!media.OwnsPlaybackControl(e.OriginalSource as DependencyObject)
                && !IsInsideButton(e.OriginalSource as DependencyObject)
                && !MainWindow.IsFocusTextInput(e.OriginalSource as DependencyObject))
            { e.Handled = true; if (!e.IsRepeat) _ = ToggleMediaPlaybackAsync(media); }
        }
        else if (e.Key == Key.Escape || (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            || (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && (_text.IsReadOnly || (!_text.IsKeyboardFocusWithin && !IsInsideTextEditor(e.OriginalSource as DependencyObject)))
                && !MainWindow.IsFocusTextInput(e.OriginalSource as DependencyObject)
                && !IsInsideButton(e.OriginalSource as DependencyObject))) { e.Handled = true; Close(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = SaveAsync(); }
        else if (_pageCount > 0 && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        { e.Handled = true; _ = LoadPdfAsync(_page + (e.Key == Key.Right ? 1 : -1)); }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        { e.Handled = true; _ = NavigateFileAsync(e.SystemKey == Key.Right ? 1 : -1); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right && PreviewSurfaceKey(e.OriginalSource as DependencyObject))
        { e.Handled = true; _ = NavigateFileAsync(e.Key == Key.Right ? 1 : -1); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && PreviewSurfaceKey(e.OriginalSource as DependencyObject))
        { e.Handled = true; _ = DeleteCurrentAsync(); }
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        for (DependencyObject? target = e.OriginalSource as DependencyObject; target is Visual; target = VisualTreeHelper.GetParent(target))
            if (ReferenceEquals(target, _picture))
            { if (HandleImageWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(_picture))) e.Handled = true; break; }
        base.OnPreviewMouseWheel(e);
    }
    private async Task ToggleMediaPlaybackAsync(NativePreviewHost media)
    {
        try { await media.TogglePlaybackAsync(_load.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed && ReferenceEquals(media, _native)) Status("Could not control playback · " + e.Message); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_ownerCloseFrozen && !_allowClose) { e.Cancel = true; return; }
        if (_allowClose || (!_dirty && !_saving)) return;
        e.Cancel = true;
        if (_askingClose) return;
        _askingClose = true;
        var request = _openRequest;
        try
        {
            if (!await CommitForCloseAsync() || request != _openRequest || _dirty || _saving) return;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_closed || request != _openRequest || _dirty || _saving) return;
                _allowClose = true;
                Close();
            });
        }
        finally { _askingClose = false; }
    }
    private bool IsInsideTextEditor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _text) || ReferenceEquals(current, _text.TextArea)) return true;
        return false;
    }
    private bool PreviewSurfaceKey(DependencyObject? source)
    {
        if (!_text.IsReadOnly && (_text.IsKeyboardFocusWithin || IsInsideTextEditor(source))) return false;
        if (MainWindow.IsFocusTextInput(source) || IsInsideButton(source)) return false;
        if (_native?.OwnsPlaybackControl(source) == true) return false;
        for (var current = source; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is Slider or ComboBox) return false;
        return true;
    }
    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1; var corners = 2; var handle = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(handle, 20, ref dark, 4); DwmSetWindowAttribute(handle, 33, ref corners, 4);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

}
