using System.Globalization;
using System.Text.RegularExpressions;

namespace UltraExplorer.Picker.Integration;

internal enum NativeOptionKind { CheckBox, RadioButton, ComboBox, TextBox, Label }

internal sealed record NativeOptionSnapshot(string Key, NativeOptionKind Kind, string Label, bool Enabled,
    bool? Checked = null, string Text = "", string[]? Choices = null, int SelectedIndex = -1);

internal sealed record NativeDialogSnapshot(nint Handle, uint ProcessId, string ApplicationPath,
    FileDialogMode Mode, string Title, string Folder, string FileName, string OkLabel,
    string NameLabel, FileDialogFilterSpec[] Filters, int FilterIndex, bool MultiSelect,
    NativeOptionSnapshot[] Options);

/// <summary>A window or monitor rectangle in physical pixels (Win32 RECT).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(NativeRect other) => other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}

internal static partial class NativeDialogRules
{
    /// <summary>The smallest replacement, in device-independent pixels: room
    /// for the sidebar, a readable canvas and the footer with the caller's
    /// options. A Windows file dialog is usually smaller than this.</summary>
    internal const double ProxyMinimumWidth = 1280, ProxyMinimumHeight = 860;

    /// <summary>
    /// Where the replacement opens: centred over the original dialog, on the
    /// monitor it is on, at least as large as it and as large as the minimum
    /// above, but always inside that monitor's work area with a small margin.
    /// </summary>
    public static NativeRect ProxyBounds(NativeRect original, NativeRect work, double scale)
    {
        scale = scale is > 0.5 and < 8 ? scale : 1;
        var margin = (int)Math.Round(12 * scale);
        var maxWidth = Math.Max(1, work.Width - 2 * margin);
        var maxHeight = Math.Max(1, work.Height - 2 * margin);
        var width = Math.Min(maxWidth, Math.Max(original.Width, (int)Math.Round(ProxyMinimumWidth * scale)));
        var height = Math.Min(maxHeight, Math.Max(original.Height, (int)Math.Round(ProxyMinimumHeight * scale)));
        // A dialog wholly off this monitor (or with no size) centres on it instead.
        var overlaps = original.Width > 0 && original.Height > 0 && original.Left < work.Right && work.Left < original.Right
            && original.Top < work.Bottom && work.Top < original.Bottom;
        var centreX = overlaps ? original.Left + original.Width / 2 : work.Left + work.Width / 2;
        var centreY = overlaps ? original.Top + original.Height / 2 : work.Top + work.Height / 2;
        var left = Math.Clamp(centreX - width / 2, work.Left + margin, Math.Max(work.Left + margin, work.Right - margin - width));
        var top = Math.Clamp(centreY - height / 2, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - height));
        return new(left, top, left + width, top + height);
    }

    // A filter label is not necessarily its pattern. Never invent an extension
    // from "Photoshop image": an unreadable filter must keep the native dialog.
    /// <summary>What a dialog with no file-type list offers: every file.</summary>
    public static readonly FileDialogFilterSpec AllFiles = new("All files (*.*)", "*.*");

    /// <summary>
    /// A type's pattern from its label: the last group in parentheses that is
    /// all wildcards. Windows appends the real pattern to a label that may
    /// already name one of its own, narrower - "JPEG (*.jpg) (*.jpg;*.jpeg;*.jpe)"
    /// - and the first group would hide the .jpeg files the type offers.
    /// </summary>
    public static FileDialogFilterSpec? ReadFilter(string label)
    {
        FileDialogFilterSpec? found = null;
        foreach (Match match in FilterPatterns().Matches(label))
        {
            // Windows separates the patterns with semicolons; Qt programs
            // (Telegram, OBS, VLC) with spaces, "Images (*.png *.jpg)", and
            // some with commas. Each is a pattern of its own, and every one
            // must be a wildcard.
            var parts = PatternSeparators().Split(match.Groups[1].Value).Where(part => part.Length > 0).ToArray();
            if (parts.Length > 0 && parts.All(part => part.StartsWith('*')
                    && part.IndexOfAny(['\\', '/', ':', '"', '\0']) < 0))
                found = new(label, string.Join(';', parts));
        }
        return found;
    }

    [GeneratedRegex(@"\(([^()]*(?:\*|\?)[^()]*)\)")]
    private static partial Regex FilterPatterns();

    [GeneratedRegex(@"[;,\s]+")]
    private static partial Regex PatternSeparators();

    public static string? ReadFolder(string address)
    {
        // Address toolbar names have a translated prefix. The path itself is
        // invariant: find a drive or UNC path, rather than parsing English.
        for (var i = 0; i < address.Length; i++)
        {
            if ((i + 2 < address.Length && char.IsAsciiLetter(address[i]) && address[i + 1] == ':'
                    && address[i + 2] == '\\') || (i + 1 < address.Length && address[i] == '\\' && address[i + 1] == '\\'))
            {
                var path = address[i..].Trim(' ', '\u200e', '\u200f');
                if (WindowsReadsAsAnother(path)) return null;
                if (Directory.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether Windows, given this path, would go to another folder: a name
    /// ending in a dot ("pair." beside "pair"), which every ordinary path call
    /// drops, so that the picker would show - and a Save would go to - the
    /// neighbour. Only such a name is asked about; the dialog stays with
    /// Windows, which shows the folder it means.
    /// </summary>
    internal static bool WindowsReadsAsAnother(string path)
    {
        if (!path.Split('\\').Any(part => part.EndsWith('.'))) return false;
        try { return !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal); }
        catch (ArgumentException) { return true; }
    }

    private static readonly Dictionary<string, Guid> KnownFolderRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Downloads"] = new("374DE290-123F-4565-9164-39C4925E467B"),
        ["Загрузки"] = new("374DE290-123F-4565-9164-39C4925E467B"),
        ["Desktop"] = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        ["Рабочий стол"] = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        ["Documents"] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["Документы"] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["Pictures"] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        ["Изображения"] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        ["Music"] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        ["Музыка"] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        ["Videos"] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        ["Видео"] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC")
    };

    public static string? ReadKnownFolderBreadcrumbs(string address, IReadOnlyList<string> crumbs, bool fromShellRoot)
    {
        // A common dialog can show "Address: Downloads" instead of its
        // redirected filesystem path. Only resolve a *rooted* breadcrumb;
        // a random D:\Downloads must never be confused with this known folder.
        if (!fromShellRoot || crumbs.Count is < 1 or > 8 || !KnownFolderRoots.TryGetValue(crumbs[0].Trim(' ', '\u200e', '\u200f'), out var id)) return null;
        var last = crumbs[^1].Trim(' ', '\u200e', '\u200f');
        if (!address.TrimEnd(' ', '\u200e', '\u200f').EndsWith(last, StringComparison.OrdinalIgnoreCase)) return null;
        var path = DialogNative.KnownFolderPath(id);
        if (path is null) return null;
        foreach (var part in crumbs.Skip(1))
        {
            if (part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            path = Path.Combine(path, part);
            if (!Directory.Exists(path)) return null;
        }
        return path;
    }

    public static FileDialogRequest RequestFor(NativeDialogSnapshot snapshot)
    {
        var request = new FileDialogRequest
        {
            Mode = snapshot.Mode, Title = snapshot.Title, InitialFolder = snapshot.Folder,
            FileName = snapshot.FileName, OkButtonLabel = snapshot.OkLabel.Replace("&", ""),
            FileNameLabel = snapshot.NameLabel.Replace("&", ""), FileTypeIndex = snapshot.FilterIndex,
            OwnerHandle = snapshot.Handle, IsNativeProxy = true,
            // The original application's dialog remains the final validator.
            // In particular it owns overwrite, sharing and export warnings.
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
        };
        if (snapshot.MultiSelect && snapshot.Mode != FileDialogMode.Save) request.Options |= FileDialogOptions.AllowMultiSelect;
        request.Filters.AddRange(snapshot.Filters);
        return request.Normalize();
    }

    /// <summary>
    /// Whether a picker shown from the Win32 read already has the contract the
    /// full read found: the same kind of dialog, folder, labels, file types
    /// and selection rule. Anything else is bound again before OK is enabled.
    /// </summary>
    public static bool SameContract(FileDialogRequest shown, FileDialogRequest full) =>
        shown.Mode == full.Mode && shown.Options == full.Options && shown.FileTypeIndex == full.FileTypeIndex
        && Models.ViewAllPath.Equals(shown.InitialFolder, full.InitialFolder)
        && string.Equals(shown.EffectiveTitle, full.EffectiveTitle, StringComparison.Ordinal)
        && string.Equals(shown.EffectiveOkLabel, full.EffectiveOkLabel, StringComparison.Ordinal)
        && string.Equals(shown.EffectiveFileNameLabel, full.EffectiveFileNameLabel, StringComparison.Ordinal)
        && SameFilters(shown, full);

    public static bool SameFilters(FileDialogRequest shown, FileDialogRequest full) =>
        shown.Filters.Count == full.Filters.Count
        && shown.Filters.Zip(full.Filters).All(pair => string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && string.Equals(pair.First.Pattern, pair.Second.Pattern, StringComparison.Ordinal));

    /// <summary>
    /// Whether a selection carried over to a session bound again (the same
    /// dialog, fully read) should name the file: yes while the name box is
    /// empty or still shows exactly what that selection put there; no once
    /// the user has typed another name, which stays.
    /// </summary>
    public static bool SelectionNamesFile(FileDialogSession session, IReadOnlyList<Models.SelectionItem> selected)
    {
        var usable = selected.Where(item => session.PicksFolders == item.IsDirectory).Select(item => item.Path).ToArray();
        if (usable.Length == 0) return false;
        var typed = session.FileNameText.Trim();
        return typed.Length == 0
            || string.Equals(typed, FileDialogNaming.Describe(session.AllowsMultipleSelection ? usable : [usable[0]]).Trim(), StringComparison.Ordinal);
    }

    public static string TypedResult(FileDialogResult result)
    {
        if (!result.Accepted || result.Paths.Count == 0) throw new ArgumentException("No selection to deliver.");
        foreach (var path in result.Paths)
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\0', '\r', '\n']) >= 0)
                throw new ArgumentException("The selected path cannot be delivered to Windows.");
        return result.Paths.Count == 1 ? result.Paths[0] : string.Join(" ", result.Paths.Select(path => '"' + path + '"'));
    }

    /// <summary>What the calling program is called in the replacement's footer:
    /// its own description ("Notepad") when it has a short one, otherwise its
    /// file name without the extension.</summary>
    public static string ApplicationName(string path, string? description)
    {
        var text = description?.Trim();
        if (text is { Length: > 0 and <= 48 } && text.IndexOfAny(['\r', '\n', '\t']) < 0) return text;
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "the application" : name;
    }

    public static bool IsSaveLabel(string label) => label.Replace("&", "").Trim().ToLowerInvariant() is
        "save" or "save as" or "сохранить" or "сохранить как" or "speichern" or "enregistrer" or "guardar" or "salva";
}
