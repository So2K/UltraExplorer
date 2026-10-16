using System.Runtime.CompilerServices;
using UltraExplorer.Models;
using UltraExplorer.Services.Search;

namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    private SearchMapProjection? _searchMapProjection;
    private ConditionalWeakTable<NestedFolder, MapFileAnswers> _mapFileAnswers = new();

    internal SearchMapProjection? SearchMapProjection => _searchMapProjection;

    /// <summary>Changes only visibility. Every cell keeps its ordinary grid, rectangle and camera.</summary>
    internal void SetSearchMapProjection(SearchMapProjection? projection)
    {
        if (ReferenceEquals(_searchMapProjection, projection)) return;
        CancelMarquee();
        PointerLost();
        _searchMapProjection = projection;
        _mapFileAnswers = new();
        _hover = null;
        _dropTarget = null;
        _hotspots.Clear();
        _labelHotspots.Clear();
        _resolvedBeaconFilterStamp = -1;
        _selectionKey = default;
        RequestFrame(Layers.All);
    }

    private bool MapFolderVisible(NestedFolder folder) => _searchMapProjection?.ContainsFolder(folder.FullPath) != false;

    private bool MapFileVisible(NestedFolder folder, int index) => _searchMapProjection is null
        || index >= 0 && index < folder.Files.Count && MapFiles(folder).Matches[index];

    private bool MapTargetVisible(NestedFolder folder, int fileIndex) => MapFolderVisible(folder)
        && (fileIndex < 0 || MapFileVisible(folder, fileIndex));

    private MapFileAnswers MapFiles(NestedFolder folder)
    {
        var answers = _mapFileAnswers.GetValue(folder, static _ => new());
        if (ReferenceEquals(answers.Files, folder.Files)) return answers;
        var matcher = _searchMapProjection!.FileMatcher(folder.FullPath);
        var files = folder.Files;
        var matches = new bool[files.Count];
        var indices = new List<int>();
        for (var index = 0; index < files.Count; index++)
        {
            if (!matcher(files[index].Name)) continue;
            matches[index] = true;
            indices.Add(index);
        }
        answers.Files = files;
        answers.Matches = matches;
        answers.Indices = indices.ToArray();
        return answers;
    }

    private sealed class MapFileAnswers
    {
        public IReadOnlyList<NestedFile>? Files;
        public bool[] Matches = [];
        public int[] Indices = [];
    }

    private void DrawMapFiles(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var grid = folder.FileGrid;
        var answers = MapFiles(folder);
        if (grid.IsEmpty || answers.Indices.Length == 0) return;
        var tileWidth = grid.TileWidth * w;
        var tileHeight = grid.TileHeight * w;
        var wash = tileWidth * _scaleX < _minTile;
        if (wash && w * _scaleX < 12) return;
        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(
            -x / w, -y / w, (_viewWidth - x) / w, (_viewHeight - y) / w);
        var filtering = _filter is not null;
        var filterAnswers = filtering ? FileFilterAnswersOf(folder) : null;
        var visibleSlots = (long)Math.Max(0, lastColumn - firstColumn + 1) * Math.Max(0, lastRow - firstRow + 1);
        var lastWashLeft = int.MinValue;
        var lastWashTop = int.MinValue;
        var lastWashRight = int.MinValue;
        var lastWashBottom = int.MinValue;
        if (visibleSlots < answers.Indices.Length)
        {
            // Zoomed into a dense match set: visit only visible grid slots,
            // exactly as the unmasked scene does, instead of all 50k names.
            for (var row = firstRow; row <= lastRow; row++)
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var index = grid.IndexOf(row, column);
                    if (index < answers.Matches.Length && answers.Matches[index]) Paint(index);
                }
        }
        else
        {
            foreach (var index in answers.Indices)
            {
                var (row, column) = grid.PlaceOf(index);
                if (row >= firstRow && row <= lastRow && column >= firstColumn && column <= lastColumn) Paint(index);
            }
        }

        void Paint(int index)
        {
            PlaceFile(folder, index, x, y, w, out var fx, out var fy, out var fw, out var fh);
            if (wash || _tilesDrawn >= MaximumTilesPerFrame)
            {
                _sceneBudgetSpent |= !wash;
                var left = NestedRaster.Px(fx * _scaleX);
                var top = NestedRaster.Px(fy * _scaleY);
                var right = Math.Max(left + 1, NestedRaster.Px((fx + fw) * _scaleX));
                var bottom = Math.Max(top + 1, NestedRaster.Px((fy + fh) * _scaleY));
                if (left != lastWashLeft || top != lastWashTop || right != lastWashRight || bottom != lastWashBottom)
                {
                    _sink.Fill(left, top, right, bottom, NestedRaster.Mix(folder.BodyColour, 0xFF8A8F96, 0.1));
                    (lastWashLeft, lastWashTop, lastWashRight, lastWashBottom) = (left, top, right, bottom);
                }
                return;
            }
            DrawnCellCount++;
            _tilesDrawn++;
            PaintFile(folder.Files[index], fx, fy, fw, fh, filterAnswers is not null && filterAnswers.Takes(index, _filter!), filtering);
            if (labelsAllowed && tileHeight >= FileLabelPixels) _fileLabels.Add(new FileLabelJob(folder, index, fx, fy, fw, fh));
        }
    }
}
