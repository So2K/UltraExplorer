using System.Text.Json;
using UltraExplorer.Models;
using UltraExplorer.Infrastructure;

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
        StatePath = statePath ?? AppPaths.State("view-all.workspace.json");
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
        catch (JsonException)
        {
            // The canvas starts afresh, and the first save of the session would
            // write that over the damaged file; it is set aside first, so the
            // arrangement in it can still be recovered by hand.
            StateFiles.Quarantine(StatePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

/// <summary>What the state files kept in the user's profile have in common.</summary>
internal static class StateFiles
{
    /// <summary>
    /// Moves a state file that could not be understood out of the way, to
    /// <c>&lt;name&gt;.corrupt-yyyyMMdd-HHmmss</c> beside it.  Starting empty
    /// is right - a damaged file must never stop the app from opening - but the
    /// next save would then replace it, and whatever the user kept in it would
    /// be lost for good.  Set aside, it can still be mended by hand.  A move
    /// that fails is let go: this is a precaution, not a step anything
    /// depends on.
    /// </summary>
    public static void Quarantine(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
