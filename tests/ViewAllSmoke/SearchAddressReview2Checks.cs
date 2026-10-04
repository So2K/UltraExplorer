using UltraExplorer.Services.Search;

namespace ViewAllSmoke;

/// <summary>
/// The second review's issues with the search and the address bar (see
/// coordination/review/20261004-claude-review2-full.md), each replayed on its
/// own, as it went wrong.
/// </summary>
internal static partial class Program
{
    private static Task SearchAddressReview2Checks()
    {
        SearchOpenQuoteExclusionChecks();
        SearchSpacedOrChecks();
        return Task.CompletedTask;
    }

    /// <summary>
    /// J113: with a subst letter, Everything is asked for the text with the
    /// folders it stands for left out - <c>!path:"L:\"</c> appended.  A quote
    /// still open while the phrase is typed took that into the phrase, and
    /// nothing was found until the quote was closed.
    /// </summary>
    private static void SearchOpenQuoteExclusionChecks()
    {
        Section("search: an open quote does not take in the folders left out (J113)");
        var open = SearchQuery.Parse("\"my doc").Excluding([@"L:\"]);
        Check($"the quote is closed before the folders left out ({open})", open == @"""my doc"" !path:""L:\""");
        var closed = SearchQuery.Parse("\"my doc\" x").Excluding([@"L:\"]);
        Check($"a closed one is sent as typed ({closed})", closed == @"""my doc"" x !path:""L:\""");
        var none = SearchQuery.Parse("\"my doc").Excluding([]);
        Check($"with nothing left out, the text goes as typed ({none})", none == "\"my doc");
    }

    /// <summary>
    /// J112: to Everything <c>a | b</c>, <c>a |b</c> and <c>a| b</c> are all
    /// <c>a|b</c>.  The walk that stands in for it read the bar standing alone
    /// as an OR of nothing, which no name matched - so it found nothing - and
    /// a bar at one end of a word cut that word off from its other side, so
    /// both sides had to match.
    /// </summary>
    private static void SearchSpacedOrChecks()
    {
        Section("search: the walk reads an OR with spaces around it (J112)");
        const string folder = @"C:\x";
        var spaced = SearchQuery.Parse("*.mp3 | *.wav");
        Check("'*.mp3 | *.wav' finds the one and the other, and nothing else",
            spaced.Matches("song.mp3", folder, false) && spaced.Matches("take.wav", folder, false) && !spaced.Matches("notes.txt", folder, false));
        var before = SearchQuery.Parse("foo |bar");
        Check("'foo |bar' is foo or bar, not both",
            before.Matches("foo.txt", folder, false) && before.Matches("bar.txt", folder, false) && !before.Matches("baz.txt", folder, false));
        var after = SearchQuery.Parse("foo| bar");
        Check("'foo| bar' too",
            after.Matches("foo.txt", folder, false) && after.Matches("bar.txt", folder, false) && !after.Matches("baz.txt", folder, false));
        var typing = SearchQuery.Parse("report |");
        Check("a bar typed last, with nothing after it yet, keeps what the word before it finds",
            typing.Matches("report.docx", folder, false) && !typing.Matches("notes.docx", folder, false));
        var words = SearchQuery.Parse("foo bar");
        Check("words with no bar between them must still both match",
            words.Matches("foo bar.txt", folder, false) && !words.Matches("foo.txt", folder, false));
        Check($"and both sides of the OR are lit in a name ({string.Join(",", spaced.Words)})",
            spaced.Words.Contains(".mp3") && spaced.Words.Contains(".wav"));
    }
}
