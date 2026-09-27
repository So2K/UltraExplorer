using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;

namespace UltraExplorer.Controls;

/// <summary>The three faces the names on the canvas are set in.</summary>
internal enum LabelFace : byte
{
    /// <summary>Segoe UI Variable Text (Segoe UI where it is missing): names, sizes, counts.</summary>
    Regular,

    /// <summary>The same at semibold: the names of drives and of This PC.</summary>
    SemiBold,

    /// <summary>Segoe Fluent Icons (Segoe MDL2 Assets where it is missing): the folder glyph and the badges.</summary>
    Icons
}

/// <summary>
/// A text a <see cref="LabelTarget"/> has made ready to draw, and how much
/// room it takes, in DIPs.  The default value is the empty text: nothing to
/// draw, no room taken - what a target hands back for a text it cannot set
/// this frame.
///
/// <para>The fields past the size belong to the target that made it and mean
/// whatever that target needs them to: the WPF target keeps its laid-out
/// <see cref="FormattedText"/> in <see cref="Handle"/> and the factor it is
/// drawn at in <see cref="Scale"/>; a target of its own glyph runs can keep
/// the shaped run, the font size, how many glyphs survive trimming and the
/// colour.  The canvas only ever reads <see cref="Width"/> and
/// <see cref="Height"/>, and hands the text back to the target it came from
/// to draw.</para>
/// </summary>
internal readonly record struct LabelText(
    object? Handle,
    double Width,
    double Height,
    double Scale = 1,
    double Size = 0,
    int Count = 0,
    uint Ink = 0)
{
    public bool IsEmpty => Handle is null;
}

/// <summary>
/// Where the names on the cells go: folder titles, their badges and counts,
/// the pills on folders too small for a title, the notes in empty folders,
/// and file names with their icons and sizes.
///
/// The canvas decides everything about a label - which texts it has, how big,
/// in what colour, where each one starts, what is left out when there is no
/// room, and which parts can be grabbed - and says it in DIPs through these
/// few calls, so the same decisions reach any way of drawing them: WPF's text
/// and images on a drawing context, as the canvas has always drawn them, or
/// glyph and icon instances for a GPU.  Nothing a target does can change the
/// layout or the hit-testing; both are the canvas's.
///
/// <para>Calls arrive in painting order, and a later one covers an earlier
/// one where they overlap: a pill before its name, a folder's title before
/// the folders inside it.  A target must keep that order.  Colours are
/// straight, not premultiplied, and not always opaque: the pill behind a
/// name lets the cell show through, and a mark's colour may carry its own
/// alpha.</para>
/// </summary>
internal abstract class LabelTarget
{
    /// <summary>
    /// Makes a text ready to draw, and says how wide and tall it is.
    /// </summary>
    /// <param name="text">What to write.</param>
    /// <param name="size">The font size in DIPs.</param>
    /// <param name="ink">The colour to write it in.</param>
    /// <param name="maxWidth">
    /// The room there is, in DIPs.  A text wider than that is cut short with
    /// an ellipsis at a character; at 10 000 or more, or infinite, it is never
    /// cut.
    /// </param>
    /// <param name="face">Which of the canvas's faces to set it in.</param>
    /// <param name="scaled">
    /// Whether the size follows the zoom, so the same text is asked for at a
    /// slightly different size every frame of a zoom - a name on a cell - as
    /// against a fixed size, such as a pill's.  A target may prepare a scaled
    /// text once and draw it at any size.
    /// </param>
    /// <returns>The text, or the empty text when it cannot be set this frame.</returns>
    public abstract LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled);

    /// <summary>Draws a text made by <see cref="Text"/> with its top-left at <paramref name="origin"/>, in DIPs.</summary>
    public abstract void DrawText(in LabelText text, Point origin);

    /// <summary>Fills a rectangle, in DIPs: the colour mark down a file's edge.</summary>
    public abstract void FillRect(Rect bounds, Color colour);

    /// <summary>Fills a rectangle with rounded corners, in DIPs: the pill behind a small folder's name.</summary>
    public abstract void FillRounded(Rect bounds, double radius, Color colour);

    /// <summary>
    /// Draws a file's icon into <paramref name="bounds"/>, in DIPs, if it is
    /// known yet; true when it was drawn.  A file whose icon has not arrived
    /// is drawn without it - its name moves left into the room - and drawn
    /// again when the icon comes.
    /// </summary>
    /// <param name="folder">The folder the file is in.</param>
    /// <param name="fileIndex">
    /// The file's index among <paramref name="folder"/>'s shown files.  With
    /// the folder it names the file without a path: a target that needs one,
    /// for the kinds of file whose icon differs from file to file, makes it
    /// from the folder's path and the file's name, and only for those.
    /// </param>
    /// <param name="file">The file itself; its <see cref="NestedFile.Extension"/> is lower case and without the dot, or empty.</param>
    public abstract bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds);

    /// <summary>
    /// The calls that follow, up to the next <see cref="BeginLabel"/> or the
    /// end of the frame, are one label's: a folder's title, pill or note
    /// (<paramref name="fileIndex"/> -1), or one file's name with its icon,
    /// mark and size.  Every label is begun this way, in painting order.
    ///
    /// <para>A target may keep what it drew for a label and, when a later
    /// frame draws the same label with the same calls - a frame at rest that
    /// only an arriving icon asked for, say - draw it again from what it kept
    /// rather than record it anew.  The canvas makes the calls either way, in
    /// the same order, so a target can always tell.  One that keeps nothing
    /// ignores this.</para>
    /// </summary>
    /// <param name="folder">The folder whose title it is, or whose file.</param>
    /// <param name="fileIndex">The file's index among the folder's shown files, or -1 for the folder's own label.</param>
    public virtual void BeginLabel(NestedFolder folder, int fileIndex)
    {
    }
}
