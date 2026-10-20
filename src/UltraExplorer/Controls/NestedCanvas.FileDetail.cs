using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    private FocusedFileDetailController? _focusedFileDetails;
    private FocusedFileLayout? _focusedFileLayout;
    private string? _expandedFileFolder, _expandedFileName;
    private string? _focusedPathFolder, _focusedPathName, _focusedPath;
    private bool _focusedDetailMoving;
    private bool _focusedDetailRecordingDirty;
    private DrawingGroup? _focusedDocumentDrawing;
    private DocumentPreviewData? _focusedDocument;
    private int _focusedDocumentWidth;
    private double _focusedDocumentDpi;
    private (string Path, Rect Content, Rect Header, Rect Image, bool Ready, bool Document,
        double TextFontSize, double HeaderFontSize, double TextLineHeight)? _shownFocusedFile;
    private static readonly Typeface FocusedDocumentFace = new("Cascadia Mono, Consolas");
    private const double FocusedDocumentFontSize = 14;

    internal void SetFocusedFileDetailSource(Func<string, CancellationToken, Task<FocusedFileDetailResult?>>? fetch)
    {
        Dispatcher.VerifyAccess();
        _focusedFileDetails?.Dispose();
        _focusedFileDetails = null;
        ClearFocusedFileDetailView();
        _expandedFileFolder = _expandedFileName = null;
        if (fetch is not null && !_disposed)
            _focusedFileDetails = new FocusedFileDetailController(fetch,
                action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
                () => { if (!_disposed) RequestFrame(Layers.Labels); });
        if (!_disposed) RequestFrame(Layers.Labels);
    }

    private void ClearFocusedFileDetailView()
    {
        _focusedDetailRecordingDirty |= _shownFocusedFile is not null;
        _focusedFileLayout = null;
        _shownFocusedFile = null;
        _focusedDocument = null;
        _focusedDocumentDrawing = null;
        _focusedDocumentWidth = 0;
        _focusedPathFolder = _focusedPathName = _focusedPath = null;
    }

    private void SuspendFocusedFileDetails()
    {
        _focusedFileDetails?.Suspend();
        ClearFocusedFileDetailView();
        // The camera's one literal target is retained by the root focus latch.
        // Keep only two strings for threshold hysteresis, never its old tree.
    }

    private void PrepareFocusedFileDetails(bool moving)
    {
        _focusedDetailMoving = moving;
        _focusedFileLayout = null;
        if (_focusedFileDetails is not { } controller) return;
        if (Shows(CanvasLayer.Icons) && ValidateFileDetailTarget())
        {
            var viewport = new Rect(0, 0, _viewWidth, _viewHeight);
            foreach (var job in _fileLabels)
            {
                if (job.Index >= job.Folder.Files.Count || job.H < 120 || job.W < 320) continue;
                var file = job.Folder.Files[job.Index];
                if (!IsFileDetailTarget(job.Folder, file)) continue;
                var expanded = string.Equals(_expandedFileFolder, job.Folder.FullPath, StringComparison.Ordinal)
                    && string.Equals(_expandedFileName, file.Name, StringComparison.Ordinal);
                if (!expanded && (job.H < 144 || job.W < 360)) continue;
                var card = new Rect(job.X, job.Y, job.W, job.H);
                var visible = Rect.Intersect(card, viewport);
                if (visible.IsEmpty || visible.Width < 80 || visible.Height < 52) continue;

                // A normalized left-hand content region grows affinely with
                // the file tile. It never jumps to the far centre of a very
                // wide row, and wheel zoom keeps the pixels under its pointer.
                var slot = new Rect(job.X + job.H * .08, job.Y + job.H * .30,
                    Math.Min(job.W - job.H * .16, job.H * .84), job.H * .60);
                if (slot.Width <= 0 || !viewport.IntersectsWith(slot)) continue;
                var header = new Rect(visible.Left + 10, visible.Top + 7, Math.Max(60, visible.Width - 20), 30);
                var path = string.Equals(_focusedPathFolder, job.Folder.FullPath, StringComparison.Ordinal)
                    && string.Equals(_focusedPathName, file.Name, StringComparison.Ordinal)
                    ? _focusedPath! : Path.Combine(job.Folder.FullPath, file.Name);
                if (!string.Equals(path, _focusedPath, StringComparison.Ordinal))
                {
                    _focusedDocument = null;
                    _focusedDocumentDrawing = null;
                }
                _focusedPathFolder = job.Folder.FullPath;
                _focusedPathName = file.Name;
                _focusedPath = path;
                _expandedFileFolder = job.Folder.FullPath;
                _expandedFileName = file.Name;
                var version = unchecked(file.ModifiedTicks * 397 ^ file.Length);
                controller.TryGet(path, version, out var detail);
                BitmapSource? fallback = null;
                _inlineThumbnails?.TryGet(path, version, out fallback);
                var faded = file.IsHidden || _filter is not null && !FilterTakesFile(job.Folder, job.Index);
                _focusedFileLayout = new(job.Folder, file.Name, path, version, slot, header, detail, fallback, faded);
                controller.Update(new InlineThumbnailCandidate(path, version), moving);
                return;
            }
        }
        controller.Update(null, moving);
        _expandedFileFolder = _expandedFileName = null;
        _focusedPathFolder = _focusedPathName = _focusedPath = null;
        _focusedDocument = null;
        _focusedDocumentDrawing = null;
    }

    private bool IsExpandedFile(NestedFolder folder, in NestedFile file) => _focusedFileLayout is { } focused
        && ReferenceEquals(focused.Folder, folder) && string.Equals(focused.Name, file.Name, StringComparison.Ordinal);

    private bool DrawFocusedFileLabel(LabelTarget target, in FileLabelJob job, ref FolderFacts? folderFacts)
    {
        if (_focusedFileLayout is not { } focused || !ReferenceEquals(focused.Folder, job.Folder)
            || !string.Equals(focused.Name, job.Folder.Files[job.Index].Name, StringComparison.Ordinal)) return false;
        var file = job.Folder.Files[job.Index];
        const double font = 13;
        target.FillRounded(focused.Slot, 4, InlineThumbnailBackground);
        target.FillRounded(focused.Header, 4, InlineThumbnailBackground);
        var cursor = focused.Header.Left + 8;
        var right = focused.Header.Right - 8;
        var iconDrawn = target.DrawIcon(job.Folder, job.Index, file, new Rect(cursor, focused.Header.Y + 5, 20, 20));
        if (_gpuIconsLooked && !_gpuIconWaiting && ReferenceEquals(target, _gpuLabels))
            _gpuIconWaiting = IconStillToCome(iconDrawn, job.Folder, file);
        cursor += 28;
        if (folderFacts is null || !ReferenceEquals(folderFacts.Folder, job.Folder)) folderFacts = FactsOf(job.Folder);
        var mark = FolderMark.None;
        if (Shows(CanvasLayer.Marks) && HoldsMarks(folderFacts))
        {
            var facts = FactsOf(job.Folder, file);
            mark = MarkOf(job.Folder, file, facts);
            if (facts.HasAccent)
                target.FillRect(new Rect(focused.Header.X + 1, focused.Header.Y + 4, 3, focused.Header.Height - 8), facts.Accent);
        }
        if (!string.IsNullOrWhiteSpace(mark.Note))
        {
            var note = target.Text("\uE70B", 11, TextDimColour, 20, LabelFace.Icons, scaled: false);
            right -= note.Width;
            target.DrawText(note, new Point(right, focused.Header.Y + (focused.Header.Height - note.Height) / 2));
            right -= 9;
        }
        if (Shows(CanvasLayer.Details))
        {
            var text = FileDetailText(job.Folder.PlacedSort.Column, file, dateOnly: false);
            var budget = Math.Min(220, Math.Max(0, right - cursor - 130) * .4);
            if (budget >= 30)
            {
                var detail = target.Text(text, 11, TextDimColour, budget, LabelFace.Regular, scaled: false);
                target.DrawText(detail, new Point(right - detail.Width, focused.Header.Y + (focused.Header.Height - detail.Height) / 2));
                right -= budget + 12;
            }
        }
        if (right > cursor)
        {
            var name = target.Text(file.Name, font, focused.Faded ? TextDimColour : TextColour,
                right - cursor, LabelFace.Regular, scaled: false);
            target.DrawText(name, new Point(cursor, focused.Header.Y + (focused.Header.Height - name.Height) / 2));
        }
        return true;
    }

    private void DrawFocusedFileDetails(DrawingContext drawing)
    {
        _focusedDetailRecordingDirty = false;
        _shownFocusedFile = null;
        if (_focusedFileLayout is not { } focused) return;
        if (focused.Faded) drawing.PushOpacity(.42);
        // The common label layer supplies the fixed-size header. Exclude it
        // from this higher image layer when a panned card fills the viewport.
        var clipped = focused.Slot.IntersectsWith(focused.Header);
        if (clipped)
            drawing.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(focused.Slot), new RectangleGeometry(focused.Header)));
        var imageBounds = Rect.Empty;
        var document = focused.Detail?.Document;
        if (document is not null)
        {
            var width = (int)(Math.Max(48, Math.Min(1600, focused.Slot.Width - 28)) / 16) * 16;
            if (!_focusedDetailMoving && (!ReferenceEquals(document, _focusedDocument)
                || width != _focusedDocumentWidth || _focusedDocumentDpi != _scaleY))
                RecordFocusedDocument(document, width);
            if (ReferenceEquals(document, _focusedDocument) && _focusedDocumentDrawing is { } text)
            {
                drawing.PushClip(new RectangleGeometry(focused.Slot));
                drawing.PushTransform(new TranslateTransform(focused.Slot.X + 14, focused.Slot.Y + 12));
                drawing.DrawDrawing(text);
                drawing.Pop();
                drawing.Pop();
            }
        }
        else if ((focused.Detail?.Image ?? focused.Fallback) is { PixelWidth: > 0, PixelHeight: > 0 } image)
        {
            var scale = Math.Min(focused.Slot.Width / image.PixelWidth, focused.Slot.Height / image.PixelHeight);
            var width = image.PixelWidth * scale;
            var height = image.PixelHeight * scale;
            imageBounds = new Rect(focused.Slot.X + (focused.Slot.Width - width) / 2,
                focused.Slot.Y + (focused.Slot.Height - height) / 2, width, height);
            drawing.DrawImage(image, imageBounds);
        }
        drawing.DrawRoundedRectangle(null, InlineThumbnailEdge, focused.Slot, 4, 4);
        if (clipped) drawing.Pop();
        if (focused.Faded) drawing.Pop();
        _shownFocusedFile = (focused.Path, focused.Slot, focused.Header, imageBounds,
            focused.Detail is not null, document is not null, document is null ? 0 : FocusedDocumentFontSize, 13, 20);
    }

    private void RecordFocusedDocument(DocumentPreviewData document, int width)
    {
        var text = new StringBuilder(Math.Min(document.Text.Length, 32768));
        foreach (var character in document.Text.AsSpan(0, Math.Min(document.Text.Length, 32768)))
        {
            if (text.Length >= 32768) break;
            if (character == '\t') text.Append(' ', Math.Min(4, 32768 - text.Length));
            else if (!char.IsControl(character) || character is '\r' or '\n') text.Append(character);
            else text.Append('·');
        }
        if (text.Length == 0) text.Append("Empty file");
        var formatted = new FormattedText(text.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            FocusedDocumentFace, FocusedDocumentFontSize, TextBrush, _scaleY)
        {
            MaxTextWidth = width,
            MaxTextHeight = 4096,
            LineHeight = 20,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var recorded = new DrawingGroup();
        using (var context = recorded.Open()) context.DrawText(formatted, new Point());
        recorded.Freeze();
        _focusedDocumentDrawing = recorded;
        _focusedDocument = document;
        _focusedDocumentWidth = width;
        _focusedDocumentDpi = _scaleY;
    }

    private readonly record struct FocusedFileLayout(NestedFolder Folder, string Name, string Path, long Version,
        Rect Slot, Rect Header, FocusedFileDetailResult? Detail, BitmapSource? Fallback, bool Faded);

    internal (string Path, Rect Content, Rect Header, Rect Image, bool Ready, bool Document,
        double TextFontSize, double HeaderFontSize, double TextLineHeight)? FocusedFileDetailStateForTests => _shownFocusedFile;

    internal (bool HasDemand, int ActiveRequests, long CacheBytes, long RequestsStarted) FocusedFileDetailDiagnosticsForTests
    {
        get
        {
            if (_focusedFileDetails is not { } controller) return default;
            var diagnostics = controller.Diagnostics;
            return (diagnostics.HasDemand, diagnostics.ActiveRequests, diagnostics.CacheBytes, diagnostics.RequestsStarted);
        }
    }
}
