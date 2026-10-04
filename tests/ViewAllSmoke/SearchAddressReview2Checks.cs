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
}
