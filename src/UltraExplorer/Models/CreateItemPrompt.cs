using System.IO;

namespace UltraExplorer.Models;

/// <summary>The destination captured when New was requested, before a dialog can change the selection.</summary>
public sealed record CreateItemPrompt(string TargetDirectory, bool IsFolder, string? ExistingName = null)
{
    public string Title => ExistingName is null
        ? IsFolder ? "Create folder" : "Create text file"
        : IsFolder ? "New folder" : "New file";
    public string Prompt => IsFolder ? "Enter a folder name" : "Enter a file name";
    public string InitialValue => ExistingName ?? (IsFolder ? "New folder" : "New note.txt");
    public bool SelectStem => !IsFolder;
    public string ConfirmLabel => ExistingName is null ? "Create" : "Save";

    internal string? ValidationError(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return "Enter a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A name cannot contain \\ / : * ? \" < > | or control characters.";
        if (name is "." or ".." || name.EndsWith('.'))
            return "A name cannot end with a period.";

        if (name.Length > 255)
            return "A name can be at most 255 characters long.";
        if (name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return "This name is reserved by Windows. Choose another name.";

        var stem = name.Split('.')[0].TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(stem[3])))
            return "This name is reserved by Windows. Choose another name.";
        return null;
    }
}
