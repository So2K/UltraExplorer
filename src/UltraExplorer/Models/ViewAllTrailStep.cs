using System.Windows.Media;

namespace UltraExplorer.Models;

/// <summary>
/// One folder in the chain of blocks the viewport is currently inside.
///
/// A folder writes its name above its own block, which answers "whose is this?"
/// while the top of the block is on screen and nothing at all once it is not -
/// and a block of four thousand files is many screens tall.  Zoomed in far
/// enough to read a file name, the canvas otherwise gives no clue which of nine
/// open folders it is showing.
/// </summary>
/// <param name="Name">The folder's display name.</param>
/// <param name="Brush">Its branch colour, so the chain matches the frames.</param>
/// <param name="Separator">Written before the name; empty for the first step.</param>
public sealed record ViewAllTrailStep(string Name, Brush Brush, string Separator);
