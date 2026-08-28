using System.Text.Json;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statePath;

    public WorkspaceStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var stateDirectory = Path.Combine(appData, "UltraExplorer");
        Directory.CreateDirectory(stateDirectory);
        _statePath = Path.Combine(stateDirectory, "workspace.json");
    }

    public async Task<WorkspaceState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default)
    {
        var tempPath = _statePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _statePath, true);
    }
}
