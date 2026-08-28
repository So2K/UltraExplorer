namespace UltraExplorer.Models;

/// <summary>One clickable crumb in the address bar.</summary>
public sealed record BreadcrumbSegment(string Name, string FullPath, bool IsLast);
