using System.Collections;
using System.Reflection;
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
    /// Where the row is inside what the list box hands over as its anchor:
    /// a type of WPF's own that holds the row together with its place in
    /// the list.
    /// </summary>
    private static readonly PropertyInfo? RowInAnchor =
        typeof(ItemsControl).GetNestedType("ItemInfo", BindingFlags.NonPublic)?.GetProperty("Item", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The row a Shift+click in the list runs from, shared with the canvas:
    /// a Shift+click after a click on the canvas extends from what was
    /// clicked there.  The list box hands its anchor over wrapped with the
    /// row's place in the list; this is the row itself.
    /// </summary>
    public object? AnchorRow =>
        AnchorItem is { } anchor && RowInAnchor?.DeclaringType?.IsInstanceOfType(anchor) == true
            ? RowInAnchor.GetValue(anchor)
            : AnchorItem;

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
        // for - one on screen, or near it - and throws otherwise.  A new
        // anchor scrolled far away leaves the list with no anchor rather than
        // its own old one, so a Shift+click runs from the rows selected now,
        // not from a row picked before them.
        if (anchor is not null && rows.Contains(anchor) && !Equals(AnchorRow, anchor))
        {
            try
            {
                AnchorItem = ItemContainerGenerator.ContainerFromItem(anchor) is not null ? anchor : null;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
