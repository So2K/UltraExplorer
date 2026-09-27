using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraExplorer.Picker;

/// <summary>
/// The on-disk shape of a request and its result.  A caller that cannot build a
/// command line — or that has a filter containing quotes, semicolons and
/// parentheses, which most real filters do — writes one of these instead.
/// </summary>
public sealed class FileDialogRequestDocument
{
    /// <summary>open, save or folder; open when the document names none.</summary>
    public string? Mode { get; set; }

    /// <summary>Raw <c>FOS_</c> mask: a JSON number, or a string in decimal or "0x...".</summary>
    [JsonConverter(typeof(FileDialogMaskConverter))]
    public string? Options { get; set; }

    /// <summary>Named flags, merged into <see cref="Options"/>.</summary>
    public List<string>? Flags { get; set; }

    [JsonConverter(typeof(FileDialogMaskConverter))]
    public string? OpenFileNameFlags { get; set; }

    [JsonConverter(typeof(FileDialogMaskConverter))]
    public string? BrowseInfoFlags { get; set; }

    public List<FileDialogFilterDocument>? Filters { get; set; }

    /// <summary>Flat filter string, either pipe- or null-separated.</summary>
    public string? Filter { get; set; }

    /// <summary>One-based; the first file type when the document names none.</summary>
    public int? FileTypeIndex { get; set; }

    public string? Title { get; set; }

    public string? OkButtonLabel { get; set; }

    public string? FileNameLabel { get; set; }

    public string? FileName { get; set; }

    public string? DefaultExtension { get; set; }

    public string? Folder { get; set; }

    public string? DefaultFolder { get; set; }

    public string? SaveAsItem { get; set; }

    public List<FileDialogPlaceDocument>? Places { get; set; }

    public string? ClientGuid { get; set; }

    public long OwnerHandle { get; set; }

    public bool ShowFilesWhilePickingFolders { get; set; }

    public bool HideNewFolderButton { get; set; }
}

public sealed class FileDialogFilterDocument
{
    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = "*.*";
}

public sealed class FileDialogPlaceDocument
{
    public string Path { get; set; } = string.Empty;

    public bool Top { get; set; }
}

public sealed class FileDialogResultDocument
{
    public bool Accepted { get; set; }

    public List<string> Paths { get; set; } = [];

    public string? Path { get; set; }

    public int FileTypeIndex { get; set; } = 1;

    public string? Error { get; set; }
}

/// <summary>Reads requests and writes results.</summary>
public static class FileDialogJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static FileDialogRequest FromDocument(FileDialogRequestDocument document) =>
        Build(document).Normalize();

    /// <summary>
    /// The request a document describes, not yet normalized: nothing the
    /// document leaves out is filled in, so it can be merged into another.
    /// </summary>
    public static FileDialogRequest Build(FileDialogRequestDocument document)
    {
        var request = new FileDialogRequest
        {
            Mode = ParseMode(document.Mode),
            Options = ParseOptions(document.Options, document.Flags),
            FileTypeIndex = document.FileTypeIndex ?? 1,
            Title = document.Title ?? string.Empty,
            OkButtonLabel = document.OkButtonLabel ?? string.Empty,
            FileNameLabel = document.FileNameLabel ?? string.Empty,
            FileName = document.FileName ?? string.Empty,
            DefaultExtension = document.DefaultExtension ?? string.Empty,
            InitialFolder = document.Folder ?? string.Empty,
            DefaultFolder = document.DefaultFolder ?? string.Empty,
            SaveAsItem = document.SaveAsItem ?? string.Empty,
            OwnerHandle = (nint)document.OwnerHandle,
            ShowFilesWhilePickingFolders = document.ShowFilesWhilePickingFolders,
            HideNewFolderButton = document.HideNewFolderButton
        };

        if (Guid.TryParse(document.ClientGuid, out var clientGuid))
        {
            request.ClientGuid = clientGuid;
        }

        if (TryParseMask(document.OpenFileNameFlags, out var ofn))
        {
            FileDialogLegacyFlags.ApplyOpenFileName(request, (OpenFileNameFlags)ofn);
        }

        if (TryParseMask(document.BrowseInfoFlags, out var bif))
        {
            FileDialogLegacyFlags.ApplyBrowseInfo(request, (BrowseInfoFlags)bif);
        }

        foreach (var spec in FileDialogFilter.ParseSpecs(document.Filter))
        {
            request.Filters.Add(spec);
        }

        // A null entry, or "name": null, is a malformed request, not a
        // reason for the file-type box to throw while it draws.
        foreach (var filter in document.Filters ?? [])
        {
            if (filter is null)
            {
                continue;
            }

            request.Filters.Add(new FileDialogFilterSpec(
                filter.Name ?? string.Empty,
                string.IsNullOrWhiteSpace(filter.Pattern) ? "*.*" : filter.Pattern));
        }

        foreach (var place in document.Places ?? [])
        {
            if (!string.IsNullOrWhiteSpace(place?.Path))
            {
                request.Places.Add(new FileDialogPlace(place.Path, place.Top));
            }
        }

        return request;
    }

    public static FileDialogRequestDocument ReadRequestDocument(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FileDialogRequestDocument>(text, Options)
            ?? throw new InvalidDataException($"Not a picker request: {path}");
    }

    public static FileDialogRequest ReadRequestFile(string path) =>
        FromDocument(ReadRequestDocument(path));

    public static string WriteResult(FileDialogResult result)
    {
        var document = new FileDialogResultDocument
        {
            Accepted = result.Accepted,
            Paths = [.. result.Paths],
            Path = result.FirstPath,
            FileTypeIndex = result.FileTypeIndex,
            Error = result.Error
        };

        return JsonSerializer.Serialize(document, Options);
    }

    public static FileDialogMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "save" or "saveas" or "save-as" => FileDialogMode.Save,
        "folder" or "pickfolder" or "pick-folder" or "directory" or "dir" => FileDialogMode.PickFolder,
        _ => FileDialogMode.Open
    };

    /// <summary>
    /// Accepts a raw mask, a list of flag names, or both.  Names are matched
    /// against <see cref="FileDialogOptions"/> and against the <c>FOS_</c>
    /// spelling the SDK uses, so pasted C++ reads the same as C#.
    /// </summary>
    public static FileDialogOptions ParseOptions(string? mask, IEnumerable<string>? names)
    {
        var options = FileDialogOptions.None;
        if (TryParseMask(mask, out var raw))
        {
            options = (FileDialogOptions)raw;
        }

        foreach (var name in names ?? [])
        {
            if (TryParseFlagName(name, out var flag))
            {
                options |= flag;
            }
        }

        return options;
    }

    public static bool TryParseFlagName(string? name, out FileDialogOptions flag)
    {
        flag = FileDialogOptions.None;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var cleaned = name.Trim().Replace("_", string.Empty).Replace("-", string.Empty);
        if (cleaned.StartsWith("FOS", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[3..];
        }

        return Enum.TryParse(cleaned, ignoreCase: true, out flag);
    }

    public static bool TryParseMask(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim();
        return cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(cleaned[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
            : uint.TryParse(cleaned, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// A flag mask written either way a caller would write it: a JSON number
/// (<c>"options": 4096</c>) or a string (<c>"options": "0x1000"</c>).
/// </summary>
internal sealed class FileDialogMaskConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetUInt32(out var mask) =>
                mask.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("A flag mask is a non-negative number or a string such as \"0x1000\".")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
