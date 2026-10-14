using UltraExplorer.Infrastructure;

namespace UltraExplorer.ViewModels;

public enum SearchNameMatchMode { Contains, Pattern, Exact }
public enum SearchObjectKind { Any, Files, Folders }

/// <summary>Builds the common query syntax understood by Everything and the local walk.
/// Existing freehand text stays an explicit, immutable base until Apply.</summary>
public sealed class SearchBuilderViewModel : ObservableObject
{
    private string _nameText = string.Empty, _extensionsText = string.Empty, _folderPath = string.Empty;
    private string _excludedNamesText = string.Empty, _excludedExtensionsText = string.Empty;
    private bool _keepExistingQuery = true;
    private SearchNameMatchMode _nameMatchMode;
    private SearchObjectKind _objectKind;

    public SearchBuilderViewModel(string? existingQuery, string? currentFolder = null)
    {
        OriginalQuery = existingQuery ?? string.Empty;
        CurrentFolder = currentFolder;
    }

    public string OriginalQuery { get; }
    public string? CurrentFolder { get; }
    public bool HasExistingQuery => !string.IsNullOrWhiteSpace(OriginalQuery);
    public bool HasCurrentFolder => !string.IsNullOrWhiteSpace(CurrentFolder) && Path.IsPathFullyQualified(CurrentFolder);
    public bool KeepExistingQuery { get => _keepExistingQuery; set { if (SetProperty(ref _keepExistingQuery, value)) Changed(); } }
    public string NameText { get => _nameText; set { if (SetProperty(ref _nameText, value ?? string.Empty)) Changed(); } }
    public SearchNameMatchMode NameMatchMode { get => _nameMatchMode; set { if (SetProperty(ref _nameMatchMode, value)) Changed(); } }
    public SearchObjectKind ObjectKind { get => _objectKind; set { if (SetProperty(ref _objectKind, value)) Changed(); } }
    public string ExtensionsText { get => _extensionsText; set { if (SetProperty(ref _extensionsText, value ?? string.Empty)) Changed(); } }
    public string FolderPath { get => _folderPath; set { if (SetProperty(ref _folderPath, value ?? string.Empty)) Changed(); } }
    /// <summary>One name or wildcard pattern per line; punctuation in names stays literal.</summary>
    public string ExcludedNamesText { get => _excludedNamesText; set { if (SetProperty(ref _excludedNamesText, value ?? string.Empty)) Changed(); } }
    public string ExcludedExtensionsText { get => _excludedExtensionsText; set { if (SetProperty(ref _excludedExtensionsText, value ?? string.Empty)) Changed(); } }
    public string QueryText => Build().Query;
    public string ValidationMessage => Build().Error;
    public bool CanApply => Build().Error.Length == 0 && !string.IsNullOrWhiteSpace(Build().Query);
    public bool HasValidationError => ValidationMessage.Length > 0;
    public string NameHint => NameMatchMode switch
    {
        SearchNameMatchMode.Pattern => "Use * for any text and ? for one character. Example: *.png",
        SearchNameMatchMode.Exact => "Enter the complete name, including its extension. Example: logo.png",
        _ => "Find this text anywhere in a name. Example: holiday"
    };

    public void UseCurrentFolder() { if (HasCurrentFolder) FolderPath = CurrentFolder!; }

    private void Changed()
    {
        foreach (var property in new[] { nameof(QueryText), nameof(ValidationMessage), nameof(CanApply), nameof(HasValidationError), nameof(NameHint) })
            OnPropertyChanged(property);
    }

    private (string Query, string Error) Build()
    {
        var terms = new List<string>();
        var name = NameMatchMode == SearchNameMatchMode.Exact ? NameText : NameText.Trim();
        if (!Enum.IsDefined(NameMatchMode) || !Enum.IsDefined(ObjectKind)) return Invalid("Choose a valid name match and object type.");
        if (name.Length > 0)
        {
            if (InvalidName(name)) return Invalid("Names cannot contain quotes, paths, : < > or |. Enter a name or pattern only.");
            if (NameMatchMode != SearchNameMatchMode.Pattern && name.IndexOfAny(['*', '?']) >= 0)
                return Invalid("Choose Wildcard pattern to use * or ? in a name.");
            if (NameMatchMode == SearchNameMatchMode.Pattern && name.StartsWith('!') && name.IndexOfAny(['*', '?']) >= 0)
                return Invalid("A pattern cannot start with !. Use Exact name for a literal name beginning with !.");
            terms.Add(NameMatchMode switch
            {
                SearchNameMatchMode.Exact => "wfn:" + Quote(name),
                SearchNameMatchMode.Pattern => name.IndexOfAny(['*', '?']) >= 0 ? Quote(name) : "wfn:" + Quote(name),
                _ => Quote("*" + name + "*")
            });
        }
        if (!Extensions(ExtensionsText, out var extensions) || !Extensions(ExcludedExtensionsText, out var excludedExtensions))
            return Invalid("Use extensions such as png; jpg or *.png. Quotes, paths and operators do not belong here.");
        if (ObjectKind == SearchObjectKind.Folders && (extensions.Count > 0 || excludedExtensions.Count > 0))
            return Invalid("Extensions describe files. Choose Files or clear the extension conditions.");
        if (extensions.Count > 0) terms.Add("ext:" + string.Join(';', extensions));
        if (ObjectKind != SearchObjectKind.Any) terms.Add(ObjectKind == SearchObjectKind.Files ? "file:" : "folder:");
        var folder = FolderPath.Trim();
        if (folder.Length > 0)
        {
            if (!Path.IsPathFullyQualified(folder) || folder.IndexOfAny(['"', '*', '?', '|', '<', '>', '\r', '\n', '\0']) >= 0
                || folder.Any(char.IsControl)) return Invalid("Enter an absolute folder path, for example C:\\Photos. Wildcards and quotes are not folder paths.");
            terms.Add("path:" + Quote(folder.Replace('/', '\\').TrimEnd('\\') + "\\"));
        }
        foreach (var excluded in ExcludedNamesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (InvalidName(excluded) || excluded.StartsWith('!'))
                return Invalid("Enter one excluded name or wildcard per line, without quotes, paths or a leading !.");
            terms.Add("!" + Quote(excluded.IndexOfAny(['*', '?']) >= 0 ? excluded : "*" + excluded + "*"));
        }
        if (excludedExtensions.Count > 0) terms.Add("!ext:" + string.Join(';', excludedExtensions));
        var original = KeepExistingQuery ? OriginalQuery : string.Empty;
        if (terms.Count > 0 && !string.IsNullOrWhiteSpace(original))
        {
            if (original.Count(character => character == '"') % 2 != 0 || DanglingOperator(original))
                return Invalid("Finish the existing query's quote or operator before adding conditions, or untick Keep existing query.");
            if (original.Contains('\r') || original.Contains('\n'))
                return Invalid("The existing query contains a line break. Finish it in search or untick Keep existing query.");
        }
        var query = terms.Count == 0 ? original : original.Length == 0 ? string.Join(' ', terms) : original + " " + string.Join(' ', terms);
        if (query.Length > 4096) return Invalid("The search is too long. Keep the query under 4,096 characters.");
        return string.IsNullOrWhiteSpace(query) ? Invalid("Add a name, extension, folder or another condition.") : (query, string.Empty);
    }

    private static (string Query, string Error) Invalid(string error) => (string.Empty, error);
    private static string Quote(string value) => '"' + value + '"';
    private static bool InvalidName(string value) => value.IndexOfAny(['"', '\\', '/', ':', '<', '>', '|']) >= 0 || value.Any(char.IsControl);
    private static bool DanglingOperator(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && (trimmed[0] == '|' || trimmed[^1] is '|' or '!');
    }
    private static bool Extensions(string text, out List<string> extensions)
    {
        extensions = [];
        foreach (var token in text.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = token.StartsWith("*.", StringComparison.Ordinal) ? token[2..] : token.TrimStart('.');
            if (extension.Length == 0 || extension.IndexOfAny(['*', '?', '"', '\\', '/', ':', '|', '<', '>', '!', '.']) >= 0 || extension.Any(char.IsControl)) return false;
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) extensions.Add(extension.ToLowerInvariant());
        }
        return true;
    }
}
