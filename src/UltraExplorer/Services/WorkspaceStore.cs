using System.Text.Json;
using UltraExplorer.Models;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statePath;

    /// <summary>
    /// One save at a time.  Every save goes through the same temporary file,
    /// and two in flight at once - a save put off until the window is idle,
    /// and the one made on closing - would have the second fail on the first's
    /// open file and be dropped, although it holds the newer state.  Taking
    /// turns, the one started last is also the one written last.
    /// </summary>
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <param name="statePath">Where the workspace is kept; the user's state folder unless a test says otherwise.</param>
    public WorkspaceStore(string? statePath = null)
    {
        _statePath = statePath ?? AppPaths.State("workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    /// <summary>
    /// The saved renderer setting (<see cref="WorkspaceState.CanvasRenderer"/>)
    /// read on its own, synchronously, at the application's start: the GPU is
    /// prepared from the first moment, before the workspace is loaded, and a
    /// saved choice of the processor must stop that.  Null when there is no
    /// workspace, it cannot be read, or it holds no such setting.  Only the
    /// one property is looked at; nothing else of the file is parsed into
    /// objects.
    /// </summary>
    public static string? PeekCanvasRenderer(string? statePath = null)
    {
        var path = statePath ?? AppPaths.State("workspace.json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(WorkspaceState.CanvasRenderer), StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
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
        await _saving.WaitAsync(cancellationToken);
        try
        {
            var tempPath = _statePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, _statePath, true);
        }
        finally
        {
            _saving.Release();
        }
    }
}
