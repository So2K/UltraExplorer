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
    /// One save at a time.  Two in flight at once - a save put off until the
    /// window is idle, and the one made on closing - could land in either
    /// order; taking turns, the one started last is also the one written last.
    /// </summary>
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <summary>
    /// The file's turn among every store over it, in this process and in
    /// others: the same name for the same file.  Two saves moved onto one
    /// name at the same moment fail, one of them, with access denied.
    /// </summary>
    private readonly string _turnName;

    /// <param name="statePath">Where the workspace is kept; the user's state folder unless a test says otherwise.</param>
    /// <remarks>
    /// The folder is made by the first save, not here: this runs while the
    /// window is being built, and a state folder that cannot be made (a
    /// <c>ULTRAEXPLORER_STATE_DIR</c> on a drive that is gone) must cost the
    /// saves, not the window.
    /// </remarks>
    public WorkspaceStore(string? statePath = null)
    {
        _statePath = statePath ?? AppPaths.State("workspace.json");
        _turnName = @"Local\UltraExplorer.Workspace." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_statePath.ToUpperInvariant())));
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
        StateFiles.SweepTemporaries(_statePath, hidden: false);
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saving.Release();
        }
    }

    private async Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken)
    {
        // Unknown until the read finishes. Cancellation during a retry must
        // not leave a later save free to replace the original with defaults.
        _unreadable = true;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = File.OpenRead(_statePath);
                var state = await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                _unreadable = false;
                return state;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _unreadable = false;
                return null;
            }
            catch (JsonException)
            {
                // The window starts with its defaults, and its first save would
                // write them over the damaged file - the pinned folders and every
                // folder's order with it.  Set aside, it can still be mended.
                _unreadable = true;
                QuarantineUnreadableState();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _unreadable = true;
                // Held by a scanner or a sync client for a moment: tried again.
                // Still unreadable, it is there but unknown - and a save of the
                // defaults would write over the pins and orders it holds, so
                // nothing is saved this session.
                if (ex is IOException && attempt < 4)
                {
                    await Task.Delay(150 * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return null;
            }
        }
    }

    /// <summary>The workspace is on disk but could not be read: saving would lose it.</summary>
    private bool _unreadable;

    private void QuarantineUnreadableState()
    {
        try
        {
            // A collision or a denied rename must not be mistaken for a
            // successful backup. Only a completed move permits a new save.
            File.Move(_statePath, $"{_statePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            _unreadable = false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _unreadable = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private void RequireReadableState()
    {
        if (_unreadable)
            throw new IOException("The saved workspace could not be read or backed up; it has been kept unchanged. Reopen the window when the file is accessible to save changes.");
    }

    public async Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default)
    {
        await _saving.WaitAsync(cancellationToken);

        // A temporary file of this save's own: another store over the same
        // workspace - a picker's in this process, another process's - saves
        // at the same moment too, and through one shared name the second
        // failed on the first's open file, or deleted it on the way out, and
        // both saves were lost.
        var tempPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RequireReadableState();
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

            // On disk before the move, not only in the cache: a power cut
            // after a rename of unwritten data leaves an empty workspace,
            // which loads as none at all.  The flush waits for the disk, which
            // can take tens of milliseconds, so it is waited for off the
            // interface thread: saves start from a toggle or a pin, and the
            // frame right after one must not stall on it.
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                await Task.Run(() => stream.Flush(flushToDisk: true), cancellationToken);
            }

            Replace(tempPath);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next save writes over it.
            }

            throw;
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Reads the workspace as it is now, hands it to <paramref name="merge"/>
    /// and writes what that gives back, all in the file's turn among every
    /// store over it, in this process and in others: another window's save
    /// cannot land between this one's reading and its writing, to be written
    /// over with the file as it was before it.  <paramref name="merge"/> is
    /// given null for a workspace that is not there or was safely quarantined,
    /// and gives back null to write nothing. An inaccessible workspace or a
    /// failed quarantine stops the save before the merge is called. It runs on a thread of its own -
    /// the turn is a mutex, which belongs to the thread that took it - so it
    /// must touch nothing of the window's.  A turn not had within three
    /// seconds is gone ahead without, as a save does (see <see cref="Replace"/>).
    /// </summary>
    public async Task UpdateAsync(Func<WorkspaceState?, WorkspaceState?> merge, CancellationToken cancellationToken = default)
    {
        await _saving.WaitAsync(cancellationToken);
        try
        {
            RequireReadableState();
            await Task.Run(() => Update(merge, cancellationToken), cancellationToken);
        }
        finally
        {
            _saving.Release();
        }
    }

    private void Update(Func<WorkspaceState?, WorkspaceState?> merge, CancellationToken cancellationToken)
    {
        Mutex? turn = null;
        var entered = false;
        try
        {
            try
            {
                turn = new Mutex(false, _turnName);
                entered = turn.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException) { entered = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

            // The mutex belongs to this worker thread. ReadAsync may resume
            // elsewhere while the worker waits, so the read/merge/write still
            // holds one turn without moving mutex ownership to a continuation.
            var current = ReadAsync(cancellationToken).GetAwaiter().GetResult();
            RequireReadableState();
            cancellationToken.ThrowIfCancellationRequested();
            if (merge(current) is not { } state)
            {
                return;
            }

            var tempPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

                // On disk before the move, as a save is (see SaveAsync).
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024))
                {
                    JsonSerializer.Serialize(stream, state, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _statePath, true);
            }
            catch
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The next save writes over it.
                }

                throw;
            }
        }
        finally
        {
            if (entered)
            {
                turn!.ReleaseMutex();
            }

            turn?.Dispose();
        }
    }

    /// <summary>
    /// Moves a written save onto the workspace in the file's turn.  The turn
    /// is held only for the move; a store that hangs while it holds it is
    /// waited for three seconds at most, and one made by an elevated copy
    /// cannot be opened from here - either way the move then goes ahead as
    /// it always did.
    /// </summary>
    private void Replace(string tempPath)
    {
        Mutex? turn = null;
        var entered = false;
        try
        {
            try
            {
                turn = new Mutex(false, _turnName);
                entered = turn.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException) { entered = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

            File.Move(tempPath, _statePath, true);
        }
        finally
        {
            if (entered)
            {
                turn!.ReleaseMutex();
            }

            turn?.Dispose();
        }
    }
}
