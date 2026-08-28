using System.Text.Json;

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

    public FileDialogClientStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraExplorer",
            "picker-clients.json");
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

        var all = ReadAll();
        all[client.ToString("D")] = state;
        WriteAll(all);
    }

    public void Clear(Guid client)
    {
        if (client == Guid.Empty)
        {
            return;
        }

        var all = ReadAll();
        if (all.Remove(client.ToString("D")))
        {
            WriteAll(all);
        }
    }

    private Dictionary<string, FileDialogClientState> ReadAll()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
            }

            var text = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, FileDialogClientState>>(text, JsonOptions)
                ?? new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, FileDialogClientState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteAll(Dictionary<string, FileDialogClientState> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the last folder is not worth failing a file dialog over.
        }
    }
}
