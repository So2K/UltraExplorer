using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task SearchBuilderChecks()
    {
        Section("search builder: readable conditions produce the same query in both search engines");

        var empty = new SearchBuilderViewModel(null, @"Q:\Photos 2026");
        Check("an empty constructor asks for a condition rather than applying a search of everything",
            !empty.CanApply && empty.QueryText.Length == 0 && empty.ValidationMessage.Length > 0);
        Check("the current absolute folder can be used without opening a folder picker", empty.HasCurrentFolder);
        empty.UseCurrentFolder();
        Check("using it emits a quoted path with a separator boundary", empty.CanApply
            && empty.FolderPath == @"Q:\Photos 2026"
            && SearchQuery.Parse(empty.QueryText).Matches("image.png", @"Q:\Photos 2026\nested", false)
            && !SearchQuery.Parse(empty.QueryText).Matches("image.png", @"Q:\Photos 20260", false));
        var noFolder = new SearchBuilderViewModel(null, "relative");
        noFolder.UseCurrentFolder();
        Check("an unavailable or relative current folder changes no condition", !noFolder.HasCurrentFolder && noFolder.FolderPath.Length == 0);

        var phrase = new SearchBuilderViewModel(null) { NameText = "annual report" };
        var phraseQuery = SearchQuery.Parse(phrase.QueryText);
        Check("name contains keeps a phrase together and accepts surrounding text", phrase.CanApply
            && phraseQuery.SupportsLocalMatching
            && phraseQuery.Matches("final annual report.png", @"Q:\docs", false)
            && !phraseQuery.Matches("annual-report.png", @"Q:\docs", false));

        phrase.NameMatchMode = SearchNameMatchMode.Exact;
        phrase.NameText = "annual report.png";
        var exact = SearchQuery.Parse(phrase.QueryText);
        Check("complete name matches the name exactly, ignoring case", phrase.CanApply
            && exact.Matches("ANNUAL REPORT.PNG", @"Q:\docs", false)
            && !exact.Matches("final annual report.png", @"Q:\docs", false)
            && !exact.Matches("annual report.png.bak", @"Q:\docs", false));
        phrase.NameText = "!report.png";
        Check("an exact name beginning with ! remains a literal name", phrase.CanApply
            && SearchQuery.Parse(phrase.QueryText).Matches("!report.png", @"Q:\docs", false)
            && !SearchQuery.Parse(phrase.QueryText).Matches("report.png", @"Q:\docs", false));

        var pattern = new SearchBuilderViewModel(null)
        {
            NameText = "IMG_??*.png", NameMatchMode = SearchNameMatchMode.Pattern, ObjectKind = SearchObjectKind.Files,
        };
        var patternQuery = SearchQuery.Parse(pattern.QueryText);
        Check("wildcard conditions keep * and ? and restrict the complete filename", pattern.CanApply
            && patternQuery.Matches("IMG_01-final.PNG", @"Q:\photos", false)
            && !patternQuery.Matches("IMG_1.png", @"Q:\photos", false)
            && !patternQuery.Matches("xIMG_01.png", @"Q:\photos", false)
            && !patternQuery.Matches("IMG_01.png", @"Q:\photos", true));

        var extensions = new SearchBuilderViewModel(null)
        {
            ExtensionsText = ".PNG; *.jpg, png JPG\r\nwebp", ExcludedExtensionsText = ".jpg; *.JPG",
            ExcludedNamesText = "draft copy\r\n*backup*", FolderPath = @"Q:\Photos 2026", ObjectKind = SearchObjectKind.Files,
        };
        var extensionQuery = SearchQuery.Parse(extensions.QueryText);
        Check("extension lists normalize dots, wildcard prefixes, case and duplicates", extensions.CanApply
            && extensions.QueryText.Contains("ext:png;jpg;webp", StringComparison.Ordinal)
            && extensions.QueryText.Contains("!ext:jpg", StringComparison.Ordinal));
        Check("all conditions combine: type, extensions, path and exclusions", extensionQuery.SupportsLocalMatching
            && extensionQuery.Matches("photo.PNG", @"Q:\Photos 2026\trip", false)
            && extensionQuery.Matches("photo.webp", @"Q:\Photos 2026", false)
            && !extensionQuery.Matches("photo.jpg", @"Q:\Photos 2026", false)
            && !extensionQuery.Matches("draft copy.png", @"Q:\Photos 2026", false)
            && !extensionQuery.Matches("photo-backup.png", @"Q:\Photos 2026", false)
            && !extensionQuery.Matches("photo.png", @"Q:\Photos 20260", false)
            && !extensionQuery.Matches("photo.png", @"Q:\Photos 2026", true));
        extensions.ObjectKind = SearchObjectKind.Folders;
        Check("a contradictory folders-only extension search explains the problem", !extensions.CanApply && extensions.HasValidationError);
        var folders = new SearchBuilderViewModel(null) { NameText = "reports", ObjectKind = SearchObjectKind.Folders };
        Check("folders-only is otherwise a valid condition", folders.CanApply
            && SearchQuery.Parse(folders.QueryText).Matches("annual reports", @"Q:\", true)
            && !SearchQuery.Parse(folders.QueryText).Matches("annual reports", @"Q:\", false));

        const string original = "  size:>10mb \"annual report\"  ";
        var advanced = new SearchBuilderViewModel(original);
        Check("opening the constructor preserves a freehand query byte for byte until Apply",
            advanced.OriginalQuery == original && advanced.QueryText == original && advanced.KeepExistingQuery);
        advanced.ExtensionsText = "png";
        Check("adding a condition preserves the original text as the explicit base", advanced.CanApply
            && advanced.QueryText.StartsWith(original + " ", StringComparison.Ordinal)
            && advanced.QueryText.EndsWith("ext:png", StringComparison.Ordinal));
        advanced.KeepExistingQuery = false;
        Check("replace mode generates only the chosen conditions", advanced.CanApply && advanced.QueryText == "ext:png");
        Check("a builder's draft never modifies its original query", advanced.OriginalQuery == original);

        foreach (var invalid in new[] { "bad\"name", "folder\\name", "path:name", "a|b", "a\nname", "bad<name" })
        {
            var invalidName = new SearchBuilderViewModel(null) { NameText = invalid };
            Check($"name condition refuses query injection: {invalid.Replace('\n', ' ')}",
                !invalidName.CanApply && invalidName.HasValidationError && invalidName.QueryText.Length == 0);
        }
        foreach (var invalid in new[] { "png|jpg", "png !folder:", "*", "*.p?g", "png/path", "." })
        {
            var invalidExtension = new SearchBuilderViewModel(null) { ExtensionsText = invalid };
            Check($"extension condition refuses an invalid extension: {invalid}", !invalidExtension.CanApply && invalidExtension.HasValidationError);
        }
        foreach (var invalid in new[] { "relative\\folder", @"Q:\Photos*", "Q:\\Photos\"", @"Q:\Photos|other" })
        {
            var invalidFolder = new SearchBuilderViewModel(null) { FolderPath = invalid };
            Check($"folder condition refuses a relative or injected path: {invalid}", !invalidFolder.CanApply && invalidFolder.HasValidationError);
        }

        var wrongMode = new SearchBuilderViewModel(null) { NameText = "*.png" };
        Check("wildcards in contains mode require the explicit pattern choice", !wrongMode.CanApply && wrongMode.HasValidationError);
        wrongMode.NameMatchMode = SearchNameMatchMode.Pattern;
        Check("choosing pattern resolves the error", wrongMode.CanApply && !wrongMode.HasValidationError);
        wrongMode.NameText = "!report*";
        Check("a leading ! cannot silently turn a positive pattern into an exclusion", !wrongMode.CanApply);

        foreach (var unfinished in new[] { "\"annual report", "report |", "report !", "|report", "report\nother" })
        {
            var pending = new SearchBuilderViewModel(unfinished) { ExtensionsText = "png" };
            Check($"unfinished existing syntax must be completed before adding terms: {unfinished.Replace('\n', ' ')}", !pending.CanApply);
            pending.KeepExistingQuery = false;
            Check("replace mode can recover without inheriting unfinished syntax", pending.CanApply && pending.QueryText == "ext:png");
        }
        var tooLong = new SearchBuilderViewModel(new string('x', 4097));
        Check("an oversized query has a clear validation error", !tooLong.CanApply && tooLong.HasValidationError);
        var invalidEnums = new SearchBuilderViewModel(null) { NameText = "report", ObjectKind = (SearchObjectKind)99 };
        Check("an invalid object choice cannot silently generate another kind", !invalidEnums.CanApply);

        var changes = new HashSet<string>(StringComparer.Ordinal);
        var observed = new SearchBuilderViewModel(null);
        observed.PropertyChanged += (_, args) => { if (args.PropertyName is { } property) changes.Add(property); };
        observed.NameText = "report";
        Check("live edits notify both preview and Apply/error state", changes.Contains(nameof(SearchBuilderViewModel.QueryText))
            && changes.Contains(nameof(SearchBuilderViewModel.CanApply)) && changes.Contains(nameof(SearchBuilderViewModel.ValidationMessage))
            && changes.Contains(nameof(SearchBuilderViewModel.HasValidationError)));

        return Task.CompletedTask;
    }
}
