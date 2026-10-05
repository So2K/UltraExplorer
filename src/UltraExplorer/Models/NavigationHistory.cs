namespace UltraExplorer.Models;

/// <summary>
/// Where one pane has been, for Back and Forward: the places it went to, in
/// order, and which of them it is at.  Each pane of a split view keeps its
/// own, as each Explorer window does, and the window's Back and Forward step
/// through the one of the pane being worked with.
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>How many places are kept; the oldest go first.</summary>
    private const int Limit = 100;

    private readonly List<string> _places = [];
    private int _index = -1;

    /// <summary>How many places are kept.</summary>
    public int Count => _places.Count;

    /// <summary>The place being looked at, or null before the first.</summary>
    public string? Current => _index >= 0 && _index < _places.Count ? _places[_index] : null;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index < _places.Count - 1;

    /// <summary>
    /// Going to <paramref name="path"/>: whatever was ahead of the place
    /// being looked at is let go, as a browser lets it go.  The place it is
    /// at already is not a new step.
    /// </summary>
    public void Record(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Current is { } current && ViewAllPath.Equals(current, path))
        {
            return;
        }

        if (_index < _places.Count - 1)
        {
            _places.RemoveRange(_index + 1, _places.Count - _index - 1);
        }

        _places.Add(path);
        if (_places.Count > Limit)
        {
            _places.RemoveAt(0);
        }

        _index = _places.Count - 1;
    }

    /// <summary>Forgets every place: a prepared picker bound to a new dialog starts its Back and Forward afresh.</summary>
    public void Clear()
    {
        _places.Clear();
        _index = -1;
    }

    /// <summary>One step back: the place to go to, or null at the first.</summary>
    public string? Back() => CanGoBack ? _places[--_index] : null;

    /// <summary>One step forward: the place to go to, or null at the last.</summary>
    public string? Forward() => CanGoForward ? _places[++_index] : null;

    /// <summary>Where this pane has already been, newest first and each place once.</summary>
    public IReadOnlyList<string> Recent()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recent = new List<string>();
        for (var index = _places.Count - 1; index >= 0; index--)
        {
            var path = _places[index];
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                recent.Add(path);
            }
        }

        return recent;
    }
}
