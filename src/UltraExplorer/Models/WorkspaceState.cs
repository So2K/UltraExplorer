namespace UltraExplorer.Models;

public sealed class WorkspaceState
{
    public List<NodeState> Nodes { get; set; } = [];
    public List<ConnectionState> Connections { get; set; } = [];
    public List<FavoriteState> Favorites { get; set; } = [];
    public double ViewportX { get; set; }
    public double ViewportY { get; set; }
    public double ViewportZoom { get; set; } = 1;
}

public sealed record NodeState(
    Guid Id,
    string Path,
    double X,
    double Y,
    string AccentHex,
    string Note);

public sealed record ConnectionState(Guid SourceId, Guid TargetId);

public sealed record FavoriteState(string Name, string Path, string Glyph, string AccentHex);
