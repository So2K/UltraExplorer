using System.Collections;
using System.Windows.Controls;

namespace UltraExplorer.Controls;

/// <summary>
/// The folder list's list box, able to be handed a whole selection at once.
/// A list box told its selection row by row does work for every row, and a
/// selection of thousands made on the canvas would be thousands of changes;
/// handed over in one go it is one change and one <c>SelectionChanged</c>.
///
/// <para>The list's rows are recycled as they scroll, so which rows are
/// selected is never bound to the rows themselves - a row not on screen
/// would miss a Select-all or a Shift range - but kept here, whole.</para>
/// </summary>
public sealed class SelectionListBox : ListBox
{
    /// <summary>
    /// The row a Shift+click in the list runs from, shared with the canvas:
    /// a Shift+click after a click on the canvas extends from what was
    /// clicked there.
    /// </summary>
    public object? AnchorRow => AnchorItem;

    /// <summary>
    /// Selects exactly <paramref name="rows"/>, in one change, and makes
    /// <paramref name="anchor"/> - when it is one of them - the row Shift
    /// ranges run from.
    /// </summary>
    public void ApplySelection(IList rows, object? anchor)
    {
        if (SelectionMode == SelectionMode.Single)
        {
            SelectedItem = rows.Count > 0 ? rows[0] : null;
            return;
        }

        SetSelectedItems(rows);

        // The list box takes as its anchor only a row it has made a container
        // for - one on screen, or near it - and throws otherwise; a row
        // scrolled far away keeps the list's own anchor.
        if (anchor is not null && rows.Contains(anchor) && ItemContainerGenerator.ContainerFromItem(anchor) is not null)
        {
            try
            {
                AnchorItem = anchor;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
