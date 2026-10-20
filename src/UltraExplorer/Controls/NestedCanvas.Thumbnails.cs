using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    // Content is deliberately a small retained WPF layer, including when the
    // scene and its names use D3D. Frozen pixels need no per-frame decode,
    // per-file control or second GPU atlas, and the existing scene still owns
    // the geometry, hit testing and selection.
    private const int MaximumInlineThumbnails = 24;
    private const double InlineThumbnailEnterHeight = 76;
    private const double InlineThumbnailEnterWidth = 220;
    private const double InlineThumbnailLeaveHeight = 64;
    private const double InlineThumbnailLeaveWidth = 200;
    private static readonly Color InlineThumbnailBackground = Color.FromRgb(0x16, 0x1B, 0x1F);
    private static readonly Pen InlineThumbnailEdge = MakeInlineThumbnailEdge();
    private readonly DrawingVisual _thumbnailVisual = new();
    private InlineThumbnailController? _inlineThumbnails;
    private readonly List<InlineThumbnailLayout> _inlineThumbnailLayouts = new(MaximumInlineThumbnails);
    private readonly List<InlineThumbnailCandidate> _inlineThumbnailDemand = new(MaximumInlineThumbnails);
    private Dictionary<(string Folder, string Name), InlineThumbnailLayout> _inlineThumbnailLookup = new(ReferenceKeyComparer.Instance);
    private Dictionary<(string Folder, string Name), InlineThumbnailLayout> _oldInlineThumbnailLookup = new(ReferenceKeyComparer.Instance);
    private readonly List<(string Path, Rect Slot, Rect Image, bool Ready)> _shownInlineThumbnails = new(MaximumInlineThumbnails);

    /// <summary>
    /// The window supplies its bounded thumbnail service. The canvas owns only
    /// this viewport's demand and small presentation cache, never that service
    /// or its hover's cancellation token.
    /// </summary>
    internal void SetInlineThumbnailSource(Func<string, CancellationToken, Task<ThumbnailResult?>>? fetch)
    {
        Dispatcher.VerifyAccess();
        _inlineThumbnails?.Dispose();
        _inlineThumbnails = null;
        ClearInlineThumbnailView();
        if (fetch is not null && !_disposed)
        {
            _inlineThumbnails = new InlineThumbnailController(fetch,
                action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action),
                () => { if (!_disposed) RequestFrame(Layers.Labels); });
        }
        if (!_disposed) RequestFrame(Layers.Labels);
    }

    private static Pen MakeInlineThumbnailEdge()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x68, 0x77, 0x88, 0x90));
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }

    private void ClearInlineThumbnailView()
    {
        _inlineThumbnailLayouts.Clear();
        _inlineThumbnailDemand.Clear();
        _inlineThumbnailLookup.Clear();
        _oldInlineThumbnailLookup.Clear();
        _shownInlineThumbnails.Clear();
        using (_thumbnailVisual.RenderOpen()) { }
    }

    private void SuspendInlineThumbnails()
    {
        _inlineThumbnails?.Suspend();
        ClearInlineThumbnailView();
    }

    /// <summary>
    /// Select only large, genuinely visible cards, nearest the view centre
    /// first. The fixed top-k insertion avoids sorting a whole directory and
    /// path strings are reused while their listing identities remain visible.
    /// This walk and every lookup are memory-only; the controller starts IO
    /// after the viewport settles.
    /// </summary>
    private void PrepareInlineThumbnails(bool moving)
    {
        if (_inlineThumbnails is not { } controller)
        {
            return;
        }

        (_oldInlineThumbnailLookup, _inlineThumbnailLookup) = (_inlineThumbnailLookup, _oldInlineThumbnailLookup);
        _inlineThumbnailLookup.Clear();
        _inlineThumbnailLayouts.Clear();
        _inlineThumbnailDemand.Clear();
        if (!Shows(CanvasLayer.Icons))
        {
            _oldInlineThumbnailLookup.Clear();
            controller.UpdateVisible(ReadOnlySpan<InlineThumbnailCandidate>.Empty, moving);
            return;
        }
        var viewport = new Rect(0, 0, _viewWidth, _viewHeight);
        foreach (ref readonly var job in CollectionsMarshal.AsSpan(_fileLabels))
        {
            if (job.Index >= job.Folder.Files.Count || job.W < InlineThumbnailLeaveWidth || job.H < InlineThumbnailLeaveHeight
                || !viewport.IntersectsWith(new Rect(job.X, job.Y, job.W, job.H)))
            {
                continue;
            }

            var file = job.Folder.Files[job.Index];
            if (!InlineThumbnailController.CanPreview(file.Extension)) continue;
            var key = (job.Folder.FullPath, file.Name);
            var retained = _oldInlineThumbnailLookup.TryGetValue(key, out var previous);
            if (!retained && (job.W < InlineThumbnailEnterWidth || job.H < InlineThumbnailEnterHeight)) continue;

            var dx = job.X + job.W / 2 - _viewWidth / 2;
            var dy = job.Y + job.H / 2 - _viewHeight / 2;
            var distance = dx * dx + dy * dy;
            var place = _inlineThumbnailLayouts.Count;
            while (place > 0 && _inlineThumbnailLayouts[place - 1].Distance > distance) place--;
            if (place >= MaximumInlineThumbnails) continue;

            var height = Math.Min(84, job.H - 16);
            var width = Math.Min(104, height * 1.4);
            var slot = new Rect(job.X + 11, job.Y + (job.H - height) / 2, width, height);
            if (!viewport.IntersectsWith(slot)) continue;
            // Listing facts are already on the model: neither file metadata
            // nor the filesystem are consulted by the render thread.
            var version = unchecked(file.ModifiedTicks * 397 ^ file.Length);
            var path = retained ? previous.Path : string.Empty;
            var faded = file.IsHidden || _filter is not null && !FilterTakesFile(job.Folder, job.Index);
            var layout = new InlineThumbnailLayout(key, path, version, slot, distance, null, faded);
            if (_inlineThumbnailLayouts.Count == MaximumInlineThumbnails)
                _inlineThumbnailLayouts.RemoveAt(MaximumInlineThumbnails - 1);
            _inlineThumbnailLayouts.Insert(place, layout);
        }

        for (var index = 0; index < _inlineThumbnailLayouts.Count; index++)
        {
            var layout = _inlineThumbnailLayouts[index];
            var path = layout.Path.Length == 0 ? Path.Combine(layout.Key.Folder, layout.Key.Name) : layout.Path;
            controller.TryGet(path, layout.Version, out var image);
            layout = layout with { Path = path, Image = image };
            _inlineThumbnailLayouts[index] = layout;
            _inlineThumbnailLookup.Add(layout.Key, layout);
            _inlineThumbnailDemand.Add(new InlineThumbnailCandidate(layout.Path, layout.Version));
        }
        controller.UpdateVisible(CollectionsMarshal.AsSpan(_inlineThumbnailDemand), moving);
        // Both dictionaries remain bounded by the viewport, not the tree.
        _oldInlineThumbnailLookup.Clear();
    }

    private bool TryGetInlineThumbnailLayout(NestedFolder folder, in NestedFile file, out InlineThumbnailLayout layout) =>
        _inlineThumbnailLookup.TryGetValue((folder.FullPath, file.Name), out layout);

    /// <summary>
    /// Recorded only after the scene with these positions was actually shown.
    /// A busy/lost D3D present keeps the previous overlay as well as the scene,
    /// rather than advancing content to a camera the user cannot see yet.
    /// </summary>
    private void DrawInlineThumbnails()
    {
        if (_inlineThumbnailLayouts.Count == 0 && _shownInlineThumbnails.Count == 0) return;
        _shownInlineThumbnails.Clear();
        using var drawing = _thumbnailVisual.RenderOpen();
        foreach (var layout in _inlineThumbnailLayouts)
        {
            if (layout.Faded) drawing.PushOpacity(0.42);
            var imageBounds = Rect.Empty;
            if (layout.Image is { PixelWidth: > 0, PixelHeight: > 0 } image)
            {
                var inside = layout.Slot;
                inside.Inflate(-3, -3);
                var scale = Math.Min(inside.Width / image.PixelWidth, inside.Height / image.PixelHeight);
                var width = image.PixelWidth * scale;
                var height = image.PixelHeight * scale;
                imageBounds = new Rect(inside.X + (inside.Width - width) / 2, inside.Y + (inside.Height - height) / 2, width, height);
                drawing.DrawImage(image, imageBounds);
            }
            drawing.DrawRoundedRectangle(null, InlineThumbnailEdge, layout.Slot, 4, 4);
            if (layout.Faded) drawing.Pop();
            _shownInlineThumbnails.Add((layout.Path, layout.Slot, imageBounds, layout.Image is not null));
        }
    }

    private readonly record struct InlineThumbnailLayout(
        (string Folder, string Name) Key, string Path, long Version, Rect Slot, double Distance, BitmapSource? Image, bool Faded);

    internal bool InlineThumbnailModeForTests => _inlineThumbnailLayouts.Count != 0;

    internal InlineThumbnailDiagnostics InlineThumbnailDiagnosticsForTests => _inlineThumbnails?.Diagnostics ?? default;

    internal List<(string Path, Rect Slot, Rect Image, bool Ready)> InlineThumbnailStatesForTests() => new(_shownInlineThumbnails);
}
