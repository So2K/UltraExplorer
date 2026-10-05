using System.Text.Json;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker;

/// <summary>Where one caller was last time it opened the picker.</summary>
public sealed record FileDialogClientState(
    string Folder,
    int FileTypeIndex,
    List<string>? RecentNames = null);

/// <summary>
/// What <c>IFileDialog::SetClientGuid</c> is for: two programs asking for a
/// file should not fight over one "last folder".  A caller that identifies
/// itself gets its own, and <c>ClearClientData</c> throws it away.
/// </summary>
public sealed class FileDialogClientStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly string _mutexName;

    public FileDialogClientStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? AppPaths.State("picker-clients.json"));
        _mutexName = @"Local\UltraExplorer.PickerClients." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
    }

    public string StatePath => _path;

    public FileDialogClientState? Load(Guid client)
    {
        if (client == Guid.Empty)
        {
            return null;
        }

        var all = ReadAll();
        return all.TryGetValue(client.ToString("D"), out var state) ? state : null;
    }

    public void Save(Guid client, FileDialogClientState state)
    {
        if (client == Guid.Empty)
        {
            return;
        }

        Mutate(all => { all[client.ToString("D")] = state; return true; });
    }

    public void Clear(Guid client)
    {
        if (client == Guid.Empty)
        {
            return;
        }

        Mutate(all => all.Remove(client.ToString("D")));
    }

    private void Mutate(Func<Dictionary<string, FileDialogClientState>, bool> change)
    {
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var entered = false;
            try
            {
                try { entered = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) return;
                var all = ReadAll(preserveOnReadFailure: true);
                if (change(all)) WriteAll(all);
            }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Remembering a folder must never fail the file dialog itself.
        }
    }

    private Dictionary<string, FileDialogClientState> ReadAll(bool preserveOnReadFailure = false)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
            }

            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, FileDialogClientState>>(file, JsonOptions)
                ?? new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException
            || !preserveOnReadFailure && (exception is IOException or UnauthorizedAccessException))
        {
            return new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteAll(Dictionary<string, FileDialogClientState> all)
    {
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the last folder is not worth failing a file dialog over.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
