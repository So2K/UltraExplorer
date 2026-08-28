using System.Text.Json;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>Atomic, separate persistence for the View All graph.</summary>
public sealed class ViewAllWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ViewAllWorkspaceStore(string? statePath = null)
    {
        StatePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraExplorer",
            "view-all.workspace.json");
    }

    public string StatePath { get; }

    public async Task<ViewAllWorkspaceState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 32 * 1024,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<ViewAllWorkspaceState>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ViewAllWorkspaceState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(StatePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale temp file is harmless and can be overwritten next run.
            }
        }
    }
}
