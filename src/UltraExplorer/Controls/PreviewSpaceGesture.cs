namespace UltraExplorer.Controls;

/// <summary>
/// Classifies one Space press as a short preview tap or a hold. The caller can
/// arm canvas panning immediately, then remove the tap candidate on any pan or
/// other interaction. No timer, file read, selection mutation or UI work occurs.
/// </summary>
internal sealed class PreviewSpaceGesture
{
    internal const int HoldMilliseconds = 200;
    private readonly Func<long> _milliseconds;
    private string? _candidate;
    private long _started;

    internal PreviewSpaceGesture(Func<long>? milliseconds = null)
        => _milliseconds = milliseconds ?? (() => Environment.TickCount64);

    internal bool IsPressed { get; private set; }

    /// <summary>
    /// Starts only the first physical press. Key auto-repeat preserves its
    /// original time and exact selected path, including an absent selection.
    /// </summary>
    internal bool Begin(string? path)
    {
        if (IsPressed) return false;
        _started = _milliseconds();
        _candidate = string.IsNullOrWhiteSpace(path) ? null : path;
        IsPressed = true;
        return true;
    }

    /// <summary>Suppresses preview while preserving the held Space pan gesture.</summary>
    internal void CancelPreview() => _candidate = null;

    /// <summary>
    /// Consumes the press. Exactly 200 ms is already a hold. The caller must
    /// still confirm that the selection, pane and focus match its initial state.
    /// </summary>
    internal string? Release()
    {
        if (!IsPressed) return null;
        var path = _candidate;
        var started = _started;
        Cancel();
        if (path is null) return null;
        // TickCount64 crosses the signed boundary only after centuries. The
        // unsigned difference nevertheless handles that wrap without turning
        // a long hold or a backward clock jump into an accidental short tap.
        var elapsed = unchecked((ulong)(_milliseconds() - started));
        return elapsed < HoldMilliseconds ? path : null;
    }

    /// <summary>Clears the whole gesture on lost focus, deactivation or another key.</summary>
    internal void Cancel()
    {
        IsPressed = false;
        _candidate = null;
        _started = 0;
    }
}
