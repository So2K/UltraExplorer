namespace UltraExplorer.Models;

public sealed class FolderConnectionViewModel(FolderNodeViewModel source, FolderNodeViewModel target)
{
    public FolderNodeViewModel Source { get; } = source;
    public FolderNodeViewModel Target { get; } = target;
}
