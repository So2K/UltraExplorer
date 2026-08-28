using System.Text;
using System.Text.RegularExpressions;
using UltraExplorer.Models;

namespace UltraExplorer.Picker;

/// <summary>
/// One entry of the "Files of type" list, compiled for matching.
///
/// A filter runs once per enumerated entry, so a folder with fifty thousand
/// files runs it fifty thousand times.  The two shapes that make up almost
/// every real filter — <c>*.*</c> and <c>*.ext</c> — are answered without a
/// regular expression at all; only genuinely irregular patterns
/// (<c>data??.log</c>, <c>report*2026*.csv</c>) fall back to one.
/// </summary>
public sealed class FileDialogFilter : IEntryNameFilter
{
    private enum RuleKind
    {
        Everything,
        Suffix,
        Exact,
        Pattern
    }

    private readonly record struct Rule(RuleKind Kind, string Text, Regex? Expression);

    private static readonly char[] PatternSeparators = [';', ','];

    private readonly Rule[] _rules;

    private FileDialogFilter(string pattern, Rule[] rules, string? preferredExtension)
    {
        Pattern = pattern;
        _rules = rules;
        PreferredExtension = preferredExtension;
        MatchesEverything = rules.Length == 0 || Array.Exists(rules, rule => rule.Kind == RuleKind.Everything);
    }

    /// <summary>The filter used when the caller supplied none.</summary>
    public static FileDialogFilter MatchAll { get; } =
        new("*.*", [new Rule(RuleKind.Everything, "*.*", null)], null);

    /// <summary>The pattern text as the caller wrote it.</summary>
    public string Pattern { get; }

    /// <summary>True when nothing is actually filtered out.</summary>
    public bool MatchesEverything { get; }

    /// <summary>
    /// The extension a Save dialog should append when the user types a bare
    /// name: the first literal <c>*.ext</c> in the pattern, without the dot.
    /// </summary>
    public string? PreferredExtension { get; }

    public static FileDialogFilter Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return MatchAll;
        }

        var rules = new List<Rule>();
        string? preferred = null;

        foreach (var raw in pattern.Split(PatternSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = raw.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            if (piece is "*" or "*.*")
            {
                rules.Add(new Rule(RuleKind.Everything, piece, null));
                continue;
            }

            var wildcards = piece.AsSpan().IndexOfAny('*', '?');
            if (wildcards < 0)
            {
                rules.Add(new Rule(RuleKind.Exact, piece, null));
                preferred ??= ExtensionOf(piece);
                continue;
            }

            // "*.ext", and nothing else wild: a suffix test is enough.
            if (piece.StartsWith("*.", StringComparison.Ordinal)
                && piece.AsSpan(2).IndexOfAny('*', '?') < 0)
            {
                var suffix = piece[1..];
                rules.Add(new Rule(RuleKind.Suffix, suffix, null));
                preferred ??= suffix.TrimStart('.');
                continue;
            }

            rules.Add(new Rule(RuleKind.Pattern, piece, Compile(piece)));
        }

        return rules.Count == 0
            ? MatchAll
            : new FileDialogFilter(pattern, [.. rules], preferred);
    }

    /// <summary>
    /// Builds the filter a caller passes as one flat string, either the
    /// <c>OPENFILENAME</c> double-null form ("Text\0*.txt\0\0") or the pipe form
    /// Qt, Java and most scripts use ("Text|*.txt|All|*.*").
    /// </summary>
    public static IReadOnlyList<FileDialogFilterSpec> ParseSpecs(string? flat)
    {
        if (string.IsNullOrWhiteSpace(flat))
        {
            return [];
        }

        var separator = flat.Contains('\0') ? '\0' : '|';
        var parts = flat.Split(separator);
        var specs = new List<FileDialogFilterSpec>();

        for (var index = 0; index + 1 < parts.Length; index += 2)
        {
            var name = parts[index].Trim();
            var pattern = parts[index + 1].Trim();
            if (name.Length == 0 && pattern.Length == 0)
            {
                continue;
            }

            specs.Add(new FileDialogFilterSpec(
                name.Length == 0 ? pattern : name,
                pattern.Length == 0 ? "*.*" : pattern));
        }

        return specs;
    }

    public bool Matches(string fileName) => Matches(fileName.AsSpan());

    public bool Matches(ReadOnlySpan<char> fileName)
    {
        if (MatchesEverything)
        {
            return true;
        }

        foreach (var rule in _rules)
        {
            var matched = rule.Kind switch
            {
                RuleKind.Everything => true,
                RuleKind.Suffix => fileName.EndsWith(rule.Text, StringComparison.OrdinalIgnoreCase),
                RuleKind.Exact => fileName.Equals(rule.Text, StringComparison.OrdinalIgnoreCase),
                _ => rule.Expression!.IsMatch(fileName)
            };

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the text looks like a pattern rather than a name, which is how
    /// the standard dialog decides that typing <c>*.log</c> into the file name
    /// box should re-filter the view instead of opening a file called "*.log".
    /// </summary>
    public static bool LooksLikePattern(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.AsSpan().IndexOfAny('*', '?') >= 0;

    private static Regex Compile(string piece)
    {
        var builder = new StringBuilder(piece.Length * 2 + 4);
        builder.Append('^');
        foreach (var character in piece)
        {
            switch (character)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static string? ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : null;
    }
}
