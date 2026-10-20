using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    // This is camera intent, separate from selection and hover. A wheel zoom
    // holds the literal file it actually landed on, never its grid position.
    private NestedItemKey? _fileDetailTarget;
    private IReadOnlyList<NestedFile>? _fileDetailFiles;
    private int _fileDetailIndex = -1;

    internal bool IsFileDetailTarget(NestedFolder folder, in NestedFile file) => !_disposed
        && _fileDetailTarget is { } target && ReferenceEquals(target.Container, folder)
        && string.Equals(target.FileName, file.Name, StringComparison.Ordinal);

    internal string? FileDetailTargetForTests => _fileDetailTarget?.Path;

    private void ClearFileDetailTarget()
    {
        _fileDetailTarget = null;
        _fileDetailFiles = null;
        _fileDetailIndex = -1;
    }

    private void PrepareFileDetailZoom(Point at, double factor)
    {
        // Zooming out keeps the current file until it is small again. Clearing
        // it earlier would snap a deep view back to the ordinary folder cap.
        if (factor <= 1) return;
        if (HotspotAt(at) is not null || PointerHit(at) is not { IsFile: true } hit)
        {
            ClearFileDetailTarget();
            return;
        }
        _fileDetailTarget = NestedItemKey.OfFile(hit.Folder, hit.FileName!);
        _fileDetailFiles = hit.Folder.Files;
        _fileDetailIndex = hit.FileIndex;
    }

    /// <summary>Validate once against the current listing, with no file IO.</summary>
    internal bool ValidateFileDetailTarget()
    {
        if (_fileDetailTarget is not { } target) return false;
        var folder = target.Container;
        if (_disposed || _tree is null || NestedTree.IsDetached(folder) || !NestedTree.IsOnCanvas(folder)
            || !Shows(CanvasLayer.Files))
        {
            ClearFileDetailTarget();
            return false;
        }
        var files = folder.Files;
        if (!ReferenceEquals(files, _fileDetailFiles) || _fileDetailIndex < 0 || _fileDetailIndex >= files.Count
            || !string.Equals(files[_fileDetailIndex].Name, target.FileName, StringComparison.Ordinal))
        {
            _fileDetailFiles = files;
            _fileDetailIndex = NestedTree.FileIndexAsPlaced(folder, target.FileName!);
            // A case-insensitive lookup is not permission to switch to a twin.
            if (_fileDetailIndex >= files.Count || _fileDetailIndex >= 0
                && !string.Equals(files[_fileDetailIndex].Name, target.FileName, StringComparison.Ordinal))
            {
                _fileDetailIndex = -1;
                for (var index = 0; index < files.Count; index++)
                    if (string.Equals(files[index].Name, target.FileName, StringComparison.Ordinal))
                    { _fileDetailIndex = index; break; }
            }
        }
        if (_fileDetailIndex < 0 || !MapFileVisible(folder, _fileDetailIndex))
        {
            ClearFileDetailTarget();
            return false;
        }
        return true;
    }

    private bool TryFileDetailMaximum(out double maximum)
    {
        maximum = 0;
        if (!ValidateFileDetailTarget() || _fileDetailTarget is not { } target
            || ScreenRectOf(target.Container) is not { } cell) return false;
        var height = cell.Width * target.Container.FileGrid.TileHeight;
        if (!(height > 0) || !double.IsFinite(height)) return false;
        // Enough range to inspect pixels and pan within a large image, while
        // retaining a finite bound independent of the folder's file count.
        var maximumHeight = Math.Max(2048, 8 * _viewHeight);
        maximum = _aw * (maximumHeight / height);
        return maximum > 0 && double.IsFinite(maximum);
    }

    private void ReleaseSmallFileDetailTarget()
    {
        if (_fileDetailTarget is not { } target || !ValidateFileDetailTarget()) return;
        if (ScreenRectOf(target.Container) is { } cell && cell.Width * target.Container.FileGrid.TileHeight < 120)
            ClearFileDetailTarget();
    }
}
