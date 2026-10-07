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
using UltraExplorer.Dialogs;

namespace UltraExplorer;

/// <summary>One owned, resizable Quick Look window; all file work stays off the dispatcher.</summary>
internal sealed class QuickPreviewWindow : Window
{
    private readonly Grid _body = new();
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal };
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
    private readonly Button _edit, _save, _previous, _next;
    private readonly SearchPanel _search;
    private readonly Func<string, PreviewSaveChoice> _closeDecision;
    private CancellationTokenSource _load = new();
    private NativePreviewHost? _native;
    private PreviewTextEditSession? _editor;
    private DocumentPreviewData? _document;
    private string _path = "";
    private int _page, _pageCount, _generation;
    private bool _dirty, _closed, _saving, _allowClose;
    private bool _askingClose;
    private bool _renderingPdf;
    private double _zoom = 1;
    private bool _fit = true;

    internal QuickPreviewWindow(Func<string, PreviewSaveChoice>? closeDecision = null)
    {
        _closeDecision = closeDecision ?? (filename => PreviewSaveChangesDialog.Ask(this, filename));
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
        _picture = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _body.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x15));
        var root = new Grid();
        root.SetResourceReference(BackgroundProperty, "WindowBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(10, 6, 10, 6), LastChildFill = true };
        var open = Button("Open in app", () => { try { NativeShellService.Open(_path, false); } catch (Exception e) { Status(e.Message); } });
        DockPanel.SetDock(open, Dock.Right); top.Children.Add(open);
        _previous = Button("‹", () => _ = LoadPdfAsync(_page - 1)); _previous.ToolTip = "Previous page (Left)";
        _next = Button("›", () => _ = LoadPdfAsync(_page + 1)); _next.ToolTip = "Next page (Right)";
        _tools.Children.Add(_previous); _tools.Children.Add(_pageLabel); _tools.Children.Add(_next);
        _zoomTools.Children.Add(Button("−", () => ZoomBy(1 / 1.25)));
        _zoomTools.Children.Add(Button("+", () => ZoomBy(1.25)));
        _zoomTools.Children.Add(Button("Fit", () => { _fit = true; ApplyImageSize(); }));
        _tools.Children.Add(_zoomTools);
        _edit = Button("Edit text", () => _ = BeginEditAsync());
        _save = Button("Save", () => _ = SaveAsync());
        _tools.Children.Add(_edit); _tools.Children.Add(_save);
        top.Children.Add(_tools);
        root.Children.Add(top); Grid.SetRow(_body, 1); root.Children.Add(_body);
        Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
        _text.TextChanged += (_, _) => { if (!_text.IsReadOnly && _editor is not null) { _dirty = _text.Text != _editor.Text; UpdateSave(); } };
        _picture.SizeChanged += (_, _) => { if (_fit) ApplyImageSize(); };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _load.Cancel(); _load.Dispose(); _native?.Dispose(); };
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
    internal bool HasUnsavedChanges => _dirty || _saving;
    internal Task Loading { get; private set; } = Task.CompletedTask;
    internal void OpenFile(string path)
    {
        if (_dirty || _saving) { Status("Save your text edits or close this preview before viewing another file."); return; }
        _path = path; Title = Path.GetFileName(path) + " — Quick Look";
        _load.Cancel(); _load.Dispose(); _load = new CancellationTokenSource();
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
        Loading = LoadFileAsync(path, _generation, _load.Token);
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
            if (PreviewTools.IsModel(path) || PreviewTools.IsMedia(path))
            {
                _native = new NativePreviewHost(); _body.Children.Add(_native);
                await _native.LoadAsync(path, token);
                if (Current(generation)) Status(PreviewTools.IsModel(path) ? "Drag to orbit · Wheel to zoom · F3D" : "Playback controls appear over the video · mpv");
                return;
            }
            if (IsImage(path))
            {
                var image = await Task.Run(() => ReadImage(path), token);
                if (!Current(generation)) return;
                ShowImage(image); Status($"{image.PixelWidth:N0} × {image.PixelHeight:N0} · Wheel to scroll · Ctrl+wheel to zoom");
                return;
            }
            var document = await DocumentPreviewService.ReadAsync(path, 2 * 1024 * 1024, token);
            if (!Current(generation)) return;
            if (document is not null)
            {
                _document = document; _text.Text = document.Text; _body.Children.Add(_text);
                _edit.Visibility = document.CanEdit && !document.Truncated && !document.IsBinary ? Visibility.Visible : Visibility.Collapsed;
                Status(document.Detail); return;
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
        _zoomTools.Visibility = Visibility.Visible;
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

    private void ZoomBy(double factor) { _fit = false; _zoom = Math.Clamp(_zoom * factor, 0.05, 8); ApplyImageSize(); }

    private async Task BeginEditAsync()
    {
        if (_document is not { CanEdit: true, Truncated: false, IsBinary: false } document || _editor is not null) return;
        _edit.IsEnabled = false;
        var generation = _generation;
        var path = _path;
        try
        {
            var editor = await PreviewTextEditSession.OpenAsync(path, document.EncodingName ?? "utf-8", document.HasBom, _load.Token);
            if (!Current(generation)) return;
            _editor = editor;
            _text.Text = _editor.Text; _text.IsReadOnly = false; _text.Focus();
            _edit.Visibility = Visibility.Collapsed; _save.Visibility = Visibility.Visible; UpdateSave();
            Status("Editing text · Ctrl+S to save · Original encoding is preserved");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Current(generation)) { Status(e.Message); _edit.IsEnabled = true; } }
    }

    private async Task<bool> SaveAsync()
    {
        if (_editor is null || !_dirty) return true;
        if (_saving) return false;
        _saving = true; UpdateSave();
        try
        {
            var text = _text.Text;
            await _editor.SaveAsync(text, _load.Token);
            _dirty = _text.Text != _editor.Text; Status("Saved · " + Path.GetFileName(_path));
            return !_dirty;
        }
        catch (Exception e) { Status(e.Message); return false; }
        finally { _saving = false; UpdateSave(); }
    }

    private void UpdateSave() { _save.IsEnabled = _dirty && !_saving; Title = (_dirty ? "● " : "") + Path.GetFileName(_path) + " — Quick Look"; }
    private void ResetTools()
    {
        _edit.Visibility = _save.Visibility = _previous.Visibility = _next.Visibility = _pageLabel.Visibility = Visibility.Collapsed;
        _zoomTools.Visibility = Visibility.Collapsed;
        _edit.IsEnabled = true;
    }
    private void Status(string message) { _status.Text = message; _status.ToolTip = message; }
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
        if (e.Key == Key.Escape && !_search.IsClosed) { _search.Close(); e.Handled = true; }
        else if (e.Key == Key.Escape || (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)) { e.Handled = true; Close(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = SaveAsync(); }
        else if (_pageCount > 0 && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        { e.Handled = true; _ = LoadPdfAsync(_page + (e.Key == Key.Right ? 1 : -1)); }
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && _image.Source is not null)
        { ZoomBy(e.Delta > 0 ? 1.25 : 1 / 1.25); e.Handled = true; }
        base.OnPreviewMouseWheel(e);
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || (!_dirty && !_saving)) return;
        e.Cancel = true;
        if (_saving || _askingClose) return;
        if (!await ConfirmCloseAsync()) return;
        _allowClose = true; Close();
    }
    internal async Task<bool> ConfirmCloseAsync()
    {
        if (!_dirty) return !_saving;
        if (_saving || _askingClose) return false;
        _askingClose = true;
        try
        {
        var answer = _closeDecision(Path.GetFileName(_path));
        if (answer == PreviewSaveChoice.Cancel) return false;
        if (answer == PreviewSaveChoice.Save && !await SaveAsync()) return false;
        _dirty = false;
        return true;
        }
        finally { _askingClose = false; }
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1; var corners = 2; var handle = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(handle, 20, ref dark, 4); DwmSetWindowAttribute(handle, 33, ref corners, 4);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static bool IsImage(string path) => Path.GetExtension(path.TrimEnd(' ', '.')).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico" or ".wdp" or ".jxr";
    private static BitmapSource ReadImage(string path)
    {
        using var stream = new FileStream(DocumentPreviewService.LiteralPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 100000 || frame.PixelHeight > 100000 || (long)frame.PixelWidth * frame.PixelHeight > 200000000)
            throw new IOException("This image is too large for the mini viewer.");
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                try { if (metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8) { orientation = value; break; } } catch (Exception) { }
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(frame.PixelWidth, frame.PixelHeight) > 4096)
        { if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = 4096; else image.DecodePixelHeight = 4096; }
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return FileThumbnailService.OrientImage(image, orientation);
    }
}
