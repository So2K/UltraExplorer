using System.Text;

namespace UltraExplorer.Services.Search;

/// <summary>
/// What someone typed into the search box, read once: the words the names
/// are measured against, and whether the text is plain words or uses
/// Everything's syntax - quotes, wildcards, <c>|</c>, <c>!</c>,
/// <c>ext:</c> and the like.
///
/// <para>The text itself goes to Everything as it was typed: whatever its
/// syntax means there, it means here.  The folder walk that stands in for
/// Everything understands the common part of it (<see cref="Matches"/>).</para>
/// </summary>
internal sealed class SearchQuery
{
    private static readonly char[] Wildcards = ['*', '?'];

    private readonly List<Term> _terms = [];

    private SearchQuery(string text)
    {
        Text = text;
        Parse();
    }

    /// <summary>The text as typed, trimmed.</summary>
    public string Text { get; }

    /// <summary>Whether there is anything to look for.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// Plain words and nothing else - no quotes, wildcards, operators,
    /// functions or paths - so more queries can be built from it
    /// (<see cref="PrefixSearch"/>).
    /// </summary>
    public bool IsPlain { get; private set; } = true;

    /// <summary>
    /// The words a name is measured against for the order of the results:
    /// every term that names part of a name, without its wildcards.  The
    /// longest first.
    /// </summary>
    public IReadOnlyList<string> Words { get; private set; } = [];

    /// <summary>
    /// For one plain word, the Everything query for the names that start
    /// with it - "fspy" is <c>fspy*</c> - asked separately so an exact name
    /// is never lost among thousands that merely contain it.  Null otherwise.
    /// </summary>
    public string? PrefixSearch => IsPlain && _terms.Count == 1 && _terms[0].Kind == TermKind.Contains
        ? _terms[0].Value + "*"
        : null;

    public static SearchQuery Parse(string? text) => new((text ?? string.Empty).Trim());

    /// <summary>
    /// Everything's query for the text restricted to <paramref name="folder"/>
    /// and everything under it: a term with a separator is matched against
    /// the whole path, so <c>path:"D:\Games\"</c> keeps what is in D:\Games
    /// and not what is in D:\Games2.
    /// </summary>
    public string Under(string folder) => $"{PathTerm(folder)} {Text}";

    /// <summary>
    /// Everything's query for the text, leaving out what is under each of
    /// <paramref name="excluded"/>: <c>!path:"L:\"</c>.  A quote still open at
    /// the end of the text - "my doc, typed on the way to "my doc" - is closed
    /// first, as Everything would close it: left open, it took the folders
    /// left out into the phrase, and nothing was found until it was closed.
    /// </summary>
    public string Excluding(IReadOnlyCollection<string> excluded)
    {
        if (excluded.Count == 0)
        {
            return Text;
        }

        var builder = new StringBuilder(Text);
        if (Text.Count(character => character == '"') % 2 != 0)
        {
            builder.Append('"');
        }

        foreach (var folder in excluded)
        {
            builder.Append(" !").Append(PathTerm(folder));
        }

        return builder.ToString();
    }

    /// <summary>The same for <see cref="PrefixSearch"/>.</summary>
    public string? PrefixExcluding(IReadOnlyCollection<string> excluded)
    {
        if (PrefixSearch is not { } prefix)
        {
            return null;
        }

        var builder = new StringBuilder(prefix);
        foreach (var folder in excluded)
        {
            builder.Append(" !").Append(PathTerm(folder));
        }

        return builder.ToString();
    }

    private static string PathTerm(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        return $"path:\"{trimmed}\\\"";
    }

    /// <summary>
    /// Whether an entry matches, for the folder walk: every term that is not
    /// excluded matches, and no excluded one does.  Plain words look inside
    /// the name, wildcards match the whole name, a term with a separator
    /// looks inside the full path, <c>ext:</c> takes a list of extensions,
    /// and <c>file:</c> and <c>folder:</c> keep one kind.  Anything else
    /// Everything would understand - a size, a date - is not judged here,
    /// and lets everything through.
    /// </summary>
    public bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> directory, bool isFolder)
    {
        foreach (var term in _terms)
        {
            if (term.Matches(name, directory, isFolder) == term.Excluded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// How well <paramref name="name"/> matches: 0 it is one of the words
    /// (with or without its extension), 1 it starts with one, 2 one starts a
    /// word inside it, 3 one is somewhere inside it, 4 none is (it matched
    /// by path, or by syntax).  The best over the words.
    /// </summary>
    public int Quality(string name)
    {
        var best = 4;
        foreach (var word in Words)
        {
            var quality = QualityOf(name, word);
            if (quality < best)
            {
                best = quality;
                if (best == 0)
                {
                    break;
                }
            }
        }

        return best;
    }

    internal static int QualityOf(string name, string word)
    {
        if (word.Length == 0)
        {
            return 4;
        }

        var at = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return 4;
        }

        if (at == 0)
        {
            if (name.Length == word.Length)
            {
                return 0;
            }

            // "fspy.exe" for "fspy": the name itself, with an extension.
            var dot = name.LastIndexOf('.');
            return dot == word.Length ? 0 : 1;
        }

        // Anywhere a word can start: after a separator, or where lower case
        // turns to upper (fSpyBlender).
        for (; at >= 0; at = NextIndex(name, word, at))
        {
            var before = name[at - 1];
            if (!char.IsLetterOrDigit(before) || (char.IsLower(before) && char.IsUpper(name[at])))
            {
                return 2;
            }
        }

        return 3;

        static int NextIndex(string name, string word, int after)
        {
            if (after + 1 >= name.Length)
            {
                return -1;
            }

            var next = name.IndexOf(word, after + 1, StringComparison.OrdinalIgnoreCase);
            return next;
        }
    }

    /// <summary>
    /// The parts of <paramref name="name"/> to highlight: every place one of
    /// the words occurs, merged where they touch.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Highlights(string name)
    {
        var marks = new bool[name.Length];
        var any = false;
        foreach (var word in Words)
        {
            if (word.Length == 0)
            {
                continue;
            }

            for (var at = name.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = at + word.Length < name.Length ? name.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase) : -1)
            {
                marks.AsSpan(at, word.Length).Fill(true);
                any = true;
            }
        }

        if (!any)
        {
            return [];
        }

        var spans = new List<(int, int)>();
        for (var index = 0; index < marks.Length;)
        {
            if (!marks[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < marks.Length && marks[index])
            {
                index++;
            }

            spans.Add((start, index - start));
        }

        return spans;
    }

    private void Parse()
    {
        var words = new List<string>();
        foreach (var token in JoinedAtBars(Tokens(Text)))
        {
            var raw = token.Text;
            var excluded = false;
            while (raw.StartsWith('!'))
            {
                excluded = true;
                raw = raw[1..];
                IsPlain = false;
            }

            if (token.Quoted || raw.IndexOfAny(['|', '<', '>', '"']) >= 0)
            {
                IsPlain = false;
            }

            raw = raw.Trim('<', '>');
            if (raw.Length == 0)
            {
                continue;
            }

            // A function's argument may be quoted - path:"C:\Program Files\" -
            // where a quote that opens before the colon makes the colon part
            // of a phrase.
            var colon = raw.IndexOf(':');
            if (colon > 0 && (token.QuoteAt < 0 || token.QuoteAt > token.Text.IndexOf(':')) && !IsDrivePath(raw))
            {
                IsPlain = false;
                var function = raw[..colon].ToLowerInvariant();
                var argument = raw[(colon + 1)..].Trim('"');
                switch (function)
                {
                    case "ext":
                        _terms.Add(new Term(TermKind.Extensions, argument, excluded));
                        break;
                    case "file" or "files":
                        _terms.Add(new Term(TermKind.FilesOnly, string.Empty, excluded));
                        AddPlain(argument, excluded, words);
                        break;
                    case "folder" or "folders":
                        _terms.Add(new Term(TermKind.FoldersOnly, string.Empty, excluded));
                        AddPlain(argument, excluded, words);
                        break;
                    case "path":
                        if (argument.Length > 0)
                        {
                            _terms.Add(new Term(TermKind.PathContains, argument, excluded));
                        }

                        break;
                    case "wfn" or "wholefilename":
                        if (argument.Length > 0)
                        {
                            _terms.Add(new Term(TermKind.WholeName, argument, excluded));
                            if (!excluded)
                            {
                                words.Add(argument);
                            }
                        }

                        break;
                    default:
                        // A size, a date, a count: Everything's to judge.
                        break;
                }

                continue;
            }

            if (raw.Contains('|'))
            {
                // A bar with nothing on either side - typed on its own, or not
                // yet followed by anything - is no term at all: as an OR of
                // nothing it matched nothing, and every result went.
                var alternatives = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (alternatives.Length == 0)
                {
                    continue;
                }

                _terms.Add(new Term(TermKind.AnyOf, raw, excluded, alternatives));
                if (!excluded)
                {
                    words.AddRange(alternatives.Select(StripWildcards).Where(word => word.Length > 0));
                }

                continue;
            }

            AddPlain(raw, excluded, words);
        }

        Words = [.. words.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(word => word.Length)];
    }

    private void AddPlain(string raw, bool excluded, List<string> words)
    {
        if (raw.Length == 0)
        {
            return;
        }

        if (raw.Contains('\\') || raw.Contains('/'))
        {
            IsPlain = false;
            _terms.Add(new Term(TermKind.PathContains, raw.Replace('/', '\\'), excluded));
            if (!excluded)
            {
                var leaf = raw[(raw.LastIndexOfAny(['\\', '/']) + 1)..];
                if (StripWildcards(leaf) is { Length: > 0 } word)
                {
                    words.Add(word);
                }
            }

            return;
        }

        if (raw.IndexOfAny(Wildcards) >= 0)
        {
            IsPlain = false;
            _terms.Add(new Term(TermKind.Wildcard, raw, excluded));
            if (!excluded)
            {
                foreach (var part in raw.Split(Wildcards, StringSplitOptions.RemoveEmptyEntries))
                {
                    words.Add(part);
                }
            }

            return;
        }

        _terms.Add(new Term(TermKind.Contains, raw, excluded));
        if (!excluded)
        {
            words.Add(raw);
        }
    }

    private static string StripWildcards(string text) => text.Replace("*", string.Empty).Replace("?", string.Empty);

    private static bool IsDrivePath(string text) => text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0]) && (text.Length == 2 || text[2] is '\\' or '/');

    /// <summary>
    /// The text split at spaces outside quotes; a quoted run keeps its spaces
    /// and loses its quotes.  <c>QuoteAt</c> is where in the token's text the
    /// first quote was, -1 for none.
    /// </summary>
    private static IEnumerable<(string Text, bool Quoted, int QuoteAt)> Tokens(string text)
    {
        var builder = new StringBuilder();
        var quoteAt = -1;
        var inQuotes = false;
        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                if (quoteAt < 0)
                {
                    quoteAt = builder.Length;
                }

                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    yield return (builder.ToString(), quoteAt >= 0, quoteAt);
                }

                builder.Clear();
                quoteAt = -1;
                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            yield return (builder.ToString(), quoteAt >= 0, quoteAt);
        }
    }

    /// <summary>
    /// The tokens with Everything's OR put back together across the spaces
    /// around it: to Everything <c>a | b</c>, <c>a |b</c> and <c>a| b</c> are
    /// all <c>a|b</c>, an OR binding tighter than the space between words.
    /// Read token by token, a bar standing alone was an OR of nothing that no
    /// name matched, and a bar at one end of a word cut it off from its other
    /// side, so the two sides had both to match.
    /// </summary>
    private static IEnumerable<(string Text, bool Quoted, int QuoteAt)> JoinedAtBars(IEnumerable<(string Text, bool Quoted, int QuoteAt)> tokens)
    {
        (string Text, bool Quoted, int QuoteAt)? pending = null;
        foreach (var token in tokens)
        {
            if (pending is { } left && (left.Text.EndsWith('|') || token.Text.StartsWith('|')))
            {
                var quoteAt = left.QuoteAt >= 0 ? left.QuoteAt
                    : token.QuoteAt >= 0 ? left.Text.Length + token.QuoteAt
                    : -1;
                pending = (left.Text + token.Text, left.Quoted || token.Quoted, quoteAt);
                continue;
            }

            if (pending is { } done)
            {
                yield return done;
            }

            pending = token;
        }

        if (pending is { } last)
        {
            yield return last;
        }
    }

    private enum TermKind
    {
        Contains,
        Wildcard,
        WholeName,
        PathContains,
        Extensions,
        FilesOnly,
        FoldersOnly,
        AnyOf,
    }

    private sealed class Term(TermKind kind, string value, bool excluded, string[]? alternatives = null)
    {
        private readonly string[] _extensions = kind == TermKind.Extensions
            ? value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(extension => extension.TrimStart('.', '*')).ToArray()
            : [];

        public TermKind Kind => kind;

        public string Value => value;

        public bool Excluded => excluded;

        public bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> directory, bool isFolder) => kind switch
        {
            TermKind.Contains => name.Contains(value, StringComparison.OrdinalIgnoreCase),
            TermKind.Wildcard => WildcardMatches(name, value),
            TermKind.WholeName => name.Equals(value, StringComparison.OrdinalIgnoreCase),
            TermKind.PathContains => PathContains(directory, name, value),
            TermKind.Extensions => !isFolder && HasExtension(name),
            TermKind.FilesOnly => !isFolder,
            TermKind.FoldersOnly => isFolder,
            TermKind.AnyOf => AnyMatches(name),
            _ => true,
        };

        private bool AnyMatches(ReadOnlySpan<char> name)
        {
            foreach (var alternative in alternatives ?? [])
            {
                if (alternative.IndexOfAny(Wildcards) >= 0
                    ? WildcardMatches(name, alternative)
                    : name.Contains(alternative, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasExtension(ReadOnlySpan<char> name)
        {
            var dot = name.LastIndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            var extension = name[(dot + 1)..];
            foreach (var wanted in _extensions)
            {
                if (extension.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PathContains(ReadOnlySpan<char> directory, ReadOnlySpan<char> name, string value)
        {
            // The full path is the folder, a separator and the name; built
            // only for the few entries that get this far.
            var length = directory.Length + 1 + name.Length;
            Span<char> rented = length <= 512 ? stackalloc char[length] : new char[length];
            directory.CopyTo(rented);
            rented[directory.Length] = '\\';
            name.CopyTo(rented[(directory.Length + 1)..]);
            return rented.Contains(value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A name against a pattern of <c>*</c> and <c>?</c>, the whole name, ignoring case.</summary>
    internal static bool WildcardMatches(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
