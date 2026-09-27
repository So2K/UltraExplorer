namespace UltraExplorer.Models;

/// <summary>What the folders and files on the canvas, the tree and the list are ordered by.</summary>
public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size
}

/// <summary>
/// The one order every view of the drives shares, chosen the way Explorer's
/// column headers choose it: a column, and which way round.  A first click on
/// a column takes that column's natural direction - names and types from A,
/// dates newest first, sizes largest first - and a second click on the same
/// column turns it round.
///
/// Whatever the column, ties are broken by name from A, so two files of the
/// same size or type always come out the same way round; and folders have no
/// size and all share one type, so under those two columns they simply stay
/// in name order.  The default value is the default order, names from A,
/// which is how every folder was shown before there was a choice.
/// </summary>
/// <param name="Column">The column ordered by.</param>
/// <param name="Descending">Largest, newest or last first.</param>
public readonly record struct ItemSort(SortColumn Column, bool Descending)
{
    private const string DescendingSuffix = "-desc";
    private const string AscendingSuffix = "-asc";

    /// <summary>Names from A: the order the reader lists folders in.</summary>
    public static ItemSort Default { get; } = new(SortColumn.Name, false);

    /// <summary>Whether this is names from A, which asks nothing of any view beyond what it does anyway.</summary>
    public bool IsDefault => Column == SortColumn.Name && !Descending;

    /// <summary>
    /// Whether a first click on the column orders it largest first.  The
    /// newest file and the biggest one are what a person sorting by date or
    /// size is looking for, so Explorer starts those two from the top.
    /// </summary>
    public static bool DescendsFirst(SortColumn column) => column is SortColumn.Modified or SortColumn.Size;

    /// <summary>The order after a click on a column header: the same column turns round, another one starts its own way.</summary>
    public ItemSort Click(SortColumn column) =>
        column == Column
            ? this with { Descending = !Descending }
            : new ItemSort(column, DescendsFirst(column));

    /// <summary>The column's name as a header or a menu shows it.</summary>
    public static string Describe(SortColumn column) => column switch
    {
        SortColumn.Modified => "Date modified",
        SortColumn.Type => "Type",
        SortColumn.Size => "Size",
        _ => "Name"
    };

    /// <summary>Which way an order runs, in the words its column would use: "newest first", "A to Z".</summary>
    public static string DescribeDirection(SortColumn column, bool descending) => column switch
    {
        SortColumn.Modified => descending ? "newest first" : "oldest first",
        SortColumn.Size => descending ? "largest first" : "smallest first",
        _ => descending ? "Z to A" : "A to Z"
    };

    /// <summary>
    /// The order as stable text for the workspace file: the column's
    /// identifier, with "-desc" when it runs backwards.  Plain words rather
    /// than numbers, so a file written by this version still reads right if
    /// the columns are ever reordered or added to.
    /// </summary>
    public string ToSetting() => Descending ? Column + DescendingSuffix : Column.ToString();

    /// <summary>
    /// The order a setting names; the default for anything empty or not
    /// understood, since a sort that cannot be read is no reason to lose the
    /// rest of the workspace.
    /// </summary>
    public static ItemSort FromSetting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var trimmed = text.Trim();
        var descending = false;
        if (trimmed.EndsWith(DescendingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            descending = true;
            trimmed = trimmed[..^DescendingSuffix.Length];
        }
        else if (trimmed.EndsWith(AscendingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^AscendingSuffix.Length];
        }

        // Matched by name only: Enum.TryParse would also take "2" or "7",
        // and a number is not something this file ever writes.
        foreach (var column in Enum.GetValues<SortColumn>())
        {
            if (string.Equals(column.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return new ItemSort(column, descending);
            }
        }

        return Default;
    }
}
