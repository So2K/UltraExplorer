namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    // Wakes can arrive from an atlas or inbox thread after it was unsubscribed.
    private volatile bool _disposed;

    /// <summary>
    /// Releases a permanently closed pane's resources. Unloaded is deliberately
    /// reversible: a retained tab or a control reparented into another host still
    /// needs its camera, warm labels and native frame when it returns.
    /// </summary>
    internal void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;

        SetInlineThumbnailSource(null);

        Tree = null;
        IconArrivals = null;
        AttachChanges(null, null);
        if (_pendingTree is { } pending)
        {
            pending.FolderLoaded -= OnFolderLoadedForSelection;
            _pendingTree = null;
        }

        ForgetTextAsks();
        if (_textAsks is { } asks)
        {
            while (asks.Results.TryTake(out _)) { }
            _textAsks = null;
        }
        _asksThisFrame.Clear();
        while (_drivenSlots.Count > 0) StopDriving(_drivenSlots[^1]);
        _filterJudging = null;
        EndPress();
        StopFrames();
        _namesRedrawTimer?.Stop();
        _shareKeepAlive?.Stop();

        // Empty the recordings before releasing the surface they may show.
        // WPF bitmaps have no Dispose; dropping both their fields and drawings
        // lets WPF release them without waiting for this canvas to be collected.
        _wpfLabels.ReleaseRecords();
        using (_sceneVisual.RenderOpen()) { }
        using (_labelVisual.RenderOpen()) { }
        using (_decorVisual.RenderOpen()) { }
        using (_overlay.RenderOpen()) { }
        UnhookGpu();
        _gpuFrame?.ClearLabels();
        _gpuFrame?.Dispose();
        _gpuFrame = null;
        _gpuSink = null;
        _sink = _rasterSink;
        _drawSurface = null;
        _gpuLabels = null;
        _labelAtlases = null;
        _shownSurface = null;
        _bitmap = null;
        _shownBitmap = null;
        _raster.Detach();

        _textCache.Clear();
        _oldTextCache.Clear();
        _lastTrimmed.Clear();
        _textDrawings.Clear();
        _folderFacts.Clear();
        _oldFolderFacts.Clear();
        _fileFacts.Clear();
        _oldFileFacts.Clear();
        _labelled.Clear();
        _labels.Clear();
        _fileLabels.Clear();
        _hotspots.Clear();
        _labelHotspots.Clear();
        _drawnStale.Clear();
        _selection.Clear();
        _selected.Clear();
        _filterMatches.Clear();
        _filterMatchSet.Clear();
        _filterOutlines.Clear();
        _resolving.Clear();
        _unresolvable.Clear();
        _pinned.Clear();
        _filePalette.Clear();
        _brushes.Clear();
        _typeTexts.Clear();
        _textsAsked.Clear();
        _textsNotForWorker.Clear();
        _selectionMarquee = null;
        _selectionDrawing.Children.Clear();
        _tileDrawing = null;
        _resolvedBeacons.Clear();
        _beaconCaptions.Clear();
        _resolvedBeaconTree = null;
        _resolvedBeaconSource = null;
        _trail.Clear();
        _trailShown.Clear();
        _trailKey.Clear();
        _trailHotspots.Clear();
        _trailDrawing = null;
        _searchMapProjection = null;
        _mapFileAnswers.Clear();
        _fileFilterAnswers.Clear();
        _lastFileFilterAnswers = null;
        _lastFileFilterFolder = null;
        _dropTarget = null;
        _pressHotspot = null;
        _completedPreciseFlight = null;
        _beacons = [];
        _filter = null;
        _filterUnconfirmed = null;
        _markLookup = null;
        _markService = null;
        IconLookup = null;
        _dirty = Layers.None;
    }
}
