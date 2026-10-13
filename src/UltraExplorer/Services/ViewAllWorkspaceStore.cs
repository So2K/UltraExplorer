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

    // A debounced save may still be writing a large canvas when closing
    // captures its newer state. Unique temp files prevent collisions, but
    // without taking turns the older snapshot can replace the newer one last.
    private readonly SemaphoreSlim _saving = new(1, 1);

    public ViewAllWorkspaceStore(string? statePath = null)
    {
        StatePath = statePath ?? AppPaths.State("view-all.workspace.json");
    }

    public string StatePath { get; }

    /// <summary>
    /// The file is there but could not be read (another program holding it,
    /// no access), or damaged and could not be set aside: the canvas starts
    /// afresh for this session, and its saves leave the file as it is - a
    /// save would otherwise write the empty start over the user's whole
    /// arrangement.
    /// </summary>
    public bool SavesHeld { get; private set; }

    public async Task<ViewAllWorkspaceState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        StateFiles.SweepTemporaries(StatePath, hidden: true);
        for (var attempt = 0; ; attempt++)
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
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 32 * 1024,
                    useAsync: true);
                return await JsonSerializer.DeserializeAsync<ViewAllWorkspaceState>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // The canvas starts afresh, and the first save of the session would
                // write that over the damaged file; it is set aside first, so the
                // arrangement in it can still be recovered by hand.
                if (!StateFiles.Quarantine(StatePath))
                {
                    SavesHeld = true;
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A scanner or a backup holding it for a moment: asked again.
                if (attempt >= 3)
                {
                    SavesHeld = true;
                    return null;
                }

                await Task.Delay(150 * (attempt + 1), cancellationToken);
            }
        }
    }

    public async Task SaveAsync(ViewAllWorkspaceState state, CancellationToken cancellationToken = default)
    {
        if (SavesHeld)
        {
            return;
        }

        await _saving.WaitAsync(cancellationToken);
        try
        {
            // Off the caller's thread, which is the UI thread after every
            // rest of the camera: the open, the flush and the move each wait
            // for the disk, and the move for anything else that has the file
            // - the search indexer, a virus scanner - so a save froze every
            // window for tens to hundreds of milliseconds, often just as the
            // next gesture began.  The state is the caller's own copy, taken
            // before the save, so nothing else touches it meanwhile.
            await Task.Run(() => SaveSnapshotAsync(state, cancellationToken), cancellationToken);
        }
        finally
        {
            _saving.Release();
        }
    }

    private async Task SaveSnapshotAsync(ViewAllWorkspaceState state, CancellationToken cancellationToken)
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
    /// <summary>How old a store's temporary file must be before it counts as left behind; no write takes this long.</summary>
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Swept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes the temporary files a store's writes left beside
    /// <paramref name="statePath"/> - <c>.name.guid.tmp</c> when
    /// <paramref name="hidden"/>, otherwise <c>name.guid.tmp</c> - when a
    /// process ended half way through one: a picker answering its caller, a
    /// worker that was ended.  Each is as large as the state file and was
    /// never cleared.  Only those of this store's own pattern, and only those
    /// untouched for <see cref="StaleTemporaryAge"/>, so that a write still
    /// under way, in this process or another, is never touched.  Once per
    /// file per process, off the calling thread; one that cannot be deleted
    /// is left for the next start.
    /// </summary>
    public static void SweepTemporaries(string statePath, bool hidden)
    {
        if (!Swept.TryAdd(statePath, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var directory = Path.GetDirectoryName(statePath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                var prefix = (hidden ? "." : string.Empty) + Path.GetFileName(statePath) + ".";
                var cutoff = DateTime.UtcNow - StaleTemporaryAge;
                foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
                {
                    var name = Path.GetFileName(path);
                    if (name.Length != prefix.Length + 32 + ".tmp".Length
                        || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _)
                        || File.GetLastWriteTimeUtc(path) > cutoff)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        });
    }

    /// <summary>
    /// Moves a state file that could not be understood out of the way, to
    /// <c>&lt;name&gt;.corrupt-yyyyMMdd-HHmmss</c> beside it.  Starting empty
    /// is right - a damaged file must never stop the app from opening - but the
    /// next save would then replace it, and whatever the user kept in it would
    /// be lost for good.  Set aside, it can still be mended by hand.  True
    /// when it was moved: a caller whose next save would replace the damaged
    /// file holds its saves when it was not.  A short unique tail keeps two
    /// files set aside in the same second apart.
    /// </summary>
    public static bool Quarantine(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return !File.Exists(path);
        }
    }
}
