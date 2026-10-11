using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// Arrival feedback follows the picture, not the filesystem clock: an item
// outside the viewport keeps its signal until its own readable tile is drawn.
public sealed partial class NestedCanvas
{
    internal const double ArrivalPulseMilliseconds = 1200;
    private const int MaximumPendingArrivals = 65_536;
    private const int MaximumVisibleArrivals = 1024;
    private static readonly Brush ArrivalFill = Frozen(Color.FromArgb(40, 0xFF, 0xD6, 0x6B));
    private static readonly Pen ArrivalPen = FrozenPen(Color.FromRgb(0xFF, 0xD6, 0x6B), 2);
    private readonly Dictionary<NestedFolder, Dictionary<string, ArrivalPulse>> _arrivalGroups = [];
    private readonly HashSet<ArrivalPulse> _activeArrivals = [];
    private readonly List<ArrivalPulse> _finishedArrivals = [];
    private readonly List<NestedFolder> _detachedArrivalGroups = [];
    private readonly List<ArrivalHighlight> _lastArrivalHighlights = [];
    private int _pendingArrivalCount;
    private bool _arrivalsVisible;
    private TimeSpan? _arrivalStepTime;
    private long _arrivalPruneVersion = -1;

    internal readonly record struct ArrivalHighlight(string Path, Rect Bounds, double Opacity);
    internal IReadOnlyList<ArrivalHighlight> LastArrivalHighlights => _lastArrivalHighlights;
    internal int PendingArrivalCount => _pendingArrivalCount;
    internal int ActiveArrivalCount => _activeArrivals.Count(pulse => pulse.Visible);

    private sealed class ArrivalPulse(NestedFolder parent, string name, bool directory)
    {
        public readonly NestedFolder Parent = parent;
        public string Name = name;
        public readonly bool IsDirectory = directory;
        public double Elapsed;
        public bool Visible;
        public bool Started;
        public string? Path;
        public string? ParentPath;
    }

    private void OnItemsAdded(IReadOnlyList<NestedArrival> arrivals)
    {
        foreach (var arrival in arrivals)
        {
            if (NestedTree.IsDetached(arrival.Parent)) continue;
            if (_arrivalGroups.TryGetValue(arrival.Parent, out var group)
                && group.Remove(arrival.Name, out var previous))
            {
                _activeArrivals.Remove(previous);
                _pendingArrivalCount--;
            }
            if (_pendingArrivalCount >= MaximumPendingArrivals)
                RemoveArrivalGroup(_arrivalGroups.First().Key);
            if (!_arrivalGroups.TryGetValue(arrival.Parent, out group))
                _arrivalGroups.Add(arrival.Parent, group = new(StringComparer.Ordinal));
            group.Add(arrival.Name, new ArrivalPulse(arrival.Parent, arrival.Name, arrival.IsDirectory));
            _pendingArrivalCount++;
        }
        RequestFrame(Layers.Overlay);
    }

    private void OnItemsRenamed(IReadOnlyList<NestedArrivalRename> renames)
    {
        foreach (var rename in renames)
        {
            if (!_arrivalGroups.TryGetValue(rename.Parent, out var group)
                || !RemoveRenamedArrival(group, rename.OldName, out var pulse)) continue;
            if (group.Remove(rename.NewName, out var replaced))
            {
                _activeArrivals.Remove(replaced);
                _pendingArrivalCount--;
            }
            pulse.Name = rename.NewName;
            pulse.Path = null;
            group.Add(pulse.Name, pulse);
        }
        if (_pendingArrivalCount > 0) RequestFrame(Layers.Overlay);
    }

    private static bool RemoveRenamedArrival(Dictionary<string, ArrivalPulse> group, string name, out ArrivalPulse pulse)
    {
        if (group.Remove(name, out pulse!)) return true;
        // A Windows watcher may spell an existing name with different case.
        // Exact matches still distinguish case-sensitive file-system twins.
        string? matched = null;
        foreach (var key in group.Keys)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (matched is not null) { pulse = null!; return false; }
            matched = key;
        }
        if (matched is not null) return group.Remove(matched, out pulse!);
        pulse = null!;
        return false;
    }

    private void RemoveArrivalGroup(NestedFolder parent)
    {
        if (!_arrivalGroups.Remove(parent, out var group)) return;
        foreach (var pulse in group.Values) _activeArrivals.Remove(pulse);
        _pendingArrivalCount -= group.Count;
    }

    private void ClearArrivalPulses()
    {
        _arrivalGroups.Clear();
        _activeArrivals.Clear();
        _finishedArrivals.Clear();
        _detachedArrivalGroups.Clear();
        _lastArrivalHighlights.Clear();
        _pendingArrivalCount = 0;
        _arrivalsVisible = false;
        _arrivalStepTime = null;
        _arrivalPruneVersion = -1;
    }

    private void PauseArrivalPulses()
    {
        foreach (var pulse in _activeArrivals) pulse.Visible = false;
        _arrivalsVisible = false;
        _arrivalStepTime = null;
    }

    private void AdvanceArrivalPulses(TimeSpan now)
    {
        var elapsed = _arrivalStepTime is { } previous ? (now - previous).TotalMilliseconds : 0;
        _arrivalStepTime = now;
        // A minimized or stalled window did not display those missing frames.
        if (elapsed < 0 || elapsed > 1000) elapsed = 0;
        _finishedArrivals.Clear();
        foreach (var pulse in _activeArrivals)
        {
            if (!pulse.Visible) continue;
            pulse.Elapsed += elapsed;
            if (pulse.Elapsed >= ArrivalPulseMilliseconds) _finishedArrivals.Add(pulse);
        }
        foreach (var pulse in _finishedArrivals)
        {
            _activeArrivals.Remove(pulse);
            if (_arrivalGroups.TryGetValue(pulse.Parent, out var group) && group.Remove(pulse.Name))
            {
                _pendingArrivalCount--;
                if (group.Count == 0) _arrivalGroups.Remove(pulse.Parent);
            }
        }
        if (_arrivalsVisible || _finishedArrivals.Count > 0) RequestFrame(Layers.Overlay);
    }

    private void DrawArrivalPulses(DrawingContext dc)
    {
        _lastArrivalHighlights.Clear();
        foreach (var pulse in _activeArrivals) pulse.Visible = false;
        _activeArrivals.Clear();
        _arrivalsVisible = false;
        if (_pendingArrivalCount == 0 || _tree is null || (!IsVisible && !FramesByHandForTests)) return;

        if (_arrivalPruneVersion != _tree.Version)
        {
            _arrivalPruneVersion = _tree.Version;
            _detachedArrivalGroups.Clear();
            foreach (var parent in _arrivalGroups.Keys)
                if (NestedTree.IsDetached(parent)) _detachedArrivalGroups.Add(parent);
            foreach (var parent in _detachedArrivalGroups) RemoveArrivalGroup(parent);
        }

        var visible = 0;
        // These lists are the current scene's actual, individually drawn
        // folders/files. Tiny aggregate washes and hidden layers never occur
        // here, so waiting feedback is not consumed by an invisible object.
        foreach (var job in _labels)
        {
            var folder = job.Folder;
            if (folder.Parent is not { } parent || job.W < 40 || IsFilteredOut(folder)) continue;
            DrawArrival(parent, folder.Name, true,
                new Rect(job.X, job.Y, job.W, job.W * NestedLayout.CellHeight), dc, ref visible);
        }
        if (Shows(CanvasLayer.Files))
        {
            foreach (var job in _fileLabels)
            {
                if (job.H < FileLabelPixels || job.W < 30 || job.Index >= job.Folder.Files.Count
                    || _filter is not null && !FilterTakesFile(job.Folder, job.Index)) continue;
                DrawArrival(job.Folder, job.Folder.Files[job.Index].Name, false,
                    new Rect(job.X, job.Y, job.W, job.H), dc, ref visible);
            }
        }
    }

    private void DrawArrival(NestedFolder parent, string name, bool directory, Rect bounds, DrawingContext dc, ref int visible)
    {
        if (visible >= MaximumVisibleArrivals || !_arrivalGroups.TryGetValue(parent, out var group)
            || !group.TryGetValue(name, out var pulse) || pulse.IsDirectory != directory) return;
        var seen = Rect.Intersect(bounds, new Rect(0, 0, _viewWidth, _viewHeight));
        if (seen.IsEmpty || seen.Width < Math.Min(24, bounds.Width) || seen.Height < Math.Min(8, bounds.Height)) return;
        if (!pulse.Started)
        {
            pulse.Started = true;
            _arrivalStepTime = _clock.Now;
        }
        _activeArrivals.Add(pulse);
        pulse.Visible = true;
        _arrivalsVisible = true;
        visible++;
        var wave = Math.Sin(Math.PI * pulse.Elapsed / (ArrivalPulseMilliseconds / 2));
        var opacity = wave * wave;
        if (opacity <= 0.001) return;
        var parentPath = parent.FullPath;
        if (pulse.Path is null || !ReferenceEquals(parentPath, pulse.ParentPath))
        {
            pulse.ParentPath = parentPath;
            pulse.Path = System.IO.Path.Combine(parentPath, name);
        }
        _lastArrivalHighlights.Add(new ArrivalHighlight(pulse.Path, bounds, opacity));
        dc.PushOpacity(opacity);
        var radius = directory ? Math.Min(6, bounds.Width * 0.03) : Math.Min(3, bounds.Height * 0.2);
        dc.DrawRoundedRectangle(ArrivalFill, ArrivalPen, bounds, radius, radius);
        dc.Pop();
        RequestFrame(Layers.Overlay);
    }
}
