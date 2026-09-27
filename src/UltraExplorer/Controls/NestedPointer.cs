using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Controls;

/// <summary>What a left drag on the nested canvas does when it does not start on something that can be picked up.</summary>
public enum NestedLeftDrag
{
    /// <summary>
    /// Draws a selection rectangle over the folder it starts in, as a drag on
    /// the empty part of an Explorer window does.  The right or middle button,
    /// Space and the wheel pan.
    /// </summary>
    SelectArea,

    /// <summary>Pans, as the canvas always did; Shift+drag draws a selection rectangle.</summary>
    Pan
}

/// <summary>
/// The mouse, for tests and the bench: presses, moves, releases and the wheel
/// handed to the canvas exactly where WPF's mouse events hand them, with the
/// modifiers given rather than read from the keyboard.  Nothing here decides
/// anything - the canvas's own handlers do, the same ones a real mouse goes
/// through - so a check that draws a rectangle through this draws it the way
/// the user does.  The real mouse is left alone - not captured, and not
/// listened to while a press made here is under way - so a test copy of the
/// window can be driven this way whatever the user's mouse is doing;
/// <see cref="LoseCapture"/> stands in for capture being lost.
/// </summary>
internal sealed class NestedPointer(NestedCanvas canvas)
{
    public void Down(MouseButton button, Point at, ModifierKeys modifiers = ModifierKeys.None, int clickCount = 1) =>
        canvas.PointerDown(button, at, modifiers, clickCount, synthetic: true);

    public void Move(Point at) => canvas.PointerMove(at);

    public void Up(MouseButton button, Point at) => canvas.PointerUp(button, at);

    public void Wheel(Point at, int delta, ModifierKeys modifiers = ModifierKeys.None) => canvas.PointerWheel(at, delta, modifiers);

    /// <summary>Another window took the mouse: Alt+Tab mid-drag, say.</summary>
    public void LoseCapture() => canvas.PointerLost();

    /// <summary>A left click: down and up in one place.</summary>
    public void Click(Point at, ModifierKeys modifiers = ModifierKeys.None)
    {
        Down(MouseButton.Left, at, modifiers);
        Up(MouseButton.Left, at);
    }

    /// <summary>A left drag in steps, from one point to another, let go at the end.</summary>
    public void Drag(Point from, Point to, ModifierKeys modifiers = ModifierKeys.None, int steps = 8, bool release = true)
    {
        Down(MouseButton.Left, from, modifiers);
        for (var step = 1; step <= steps; step++)
        {
            Move(from + (to - from) * step / steps);
        }

        if (release)
        {
            Up(MouseButton.Left, to);
        }
    }

    /// <summary>Whether a selection rectangle is being drawn.</summary>
    public bool IsMarqueeActive => canvas.Marquee is not null;

    /// <summary>What the press under way is doing, by name, for a check's message.</summary>
    public string State => canvas.PressState;
}
