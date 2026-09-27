using System.Windows;

namespace UltraExplorer;

// The window's own messages, taken before WPF sees them: the caption's hit
// testing and the maximised bounds that the borderless window has to do for
// itself, and the messages WPF turns into no event at all, handed to the
// parts of the window that deal with them.
public partial class MainWindow
{
    private const int WmDeviceChange = 0x0219;
    private const int WmMouseHorizontalWheel = 0x020E;

    /// <summary>
    /// Reports the maximize button as HTMAXBUTTON so Windows 11 shows its Snap
    /// Layouts flyout, and turns the resulting non-client clicks back into a
    /// normal maximize toggle.  The messages WPF makes no event of - a device
    /// arriving or leaving, the horizontal wheel - go to the parts of the
    /// window that handle them.
    /// </summary>
    private IntPtr HandleWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // Left to the rest of the chain: WPF applies MinWidth/MinHeight to
            // the same structure afterwards and re-stores it whole, so what is
            // written here survives.
            case WmGetMinMaxInfo:
                ClampMaximizedBounds(hwnd, lParam);
                return IntPtr.Zero;

            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    SetMaximizeHover(true);
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }

                SetMaximizeHover(false);

                // Maximized, the top edge of the window is the top edge of the
                // screen - the easiest place there is to throw the pointer at.
                // WindowChrome still called that band a resize border, and a
                // resize border on a maximized window does nothing, so every
                // quick grab at the very top missed.  There it is the caption,
                // except over the caption buttons, whose top pixel must still
                // close the window.
                if (WindowState == WindowState.Maximized && TopBandHit(lParam) is { } code)
                {
                    handled = true;
                    return new IntPtr(code);
                }

                return IntPtr.Zero;

            case WmNcLeftButtonDown when wParam.ToInt32() == HtMaxButton:
                handled = true;
                return IntPtr.Zero;

            case WmNcLeftButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                ToggleMaximized();
                return IntPtr.Zero;

            case WmNcMouseLeave:
                SetMaximizeHover(false);
                return IntPtr.Zero;

            case WmDeviceChange:
            {
                var result = IntPtr.Zero;
                OnDeviceChangeMessage(wParam, lParam, ref handled, ref result);
                return result;
            }

            case WmMouseHorizontalWheel:
                OnHorizontalWheelMessage(wParam, lParam, ref handled);
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// WM_DEVICECHANGE: a volume arrived or left, or a device the window asked
    /// to hear about is about to go.  Left unhandled, Windows' default answer
    /// grants whatever is asked; <paramref name="result"/> is the answer when
    /// <paramref name="handled"/> is set.
    /// </summary>
    partial void OnDeviceChangeMessage(IntPtr wParam, IntPtr lParam, ref bool handled, ref IntPtr result);

    /// <summary>
    /// WM_MOUSEHWHEEL: a tilt wheel or a touchpad's sideways swipe, which WPF
    /// has no event for.  Set <paramref name="handled"/> once it has moved something.
    /// </summary>
    partial void OnHorizontalWheelMessage(IntPtr wParam, IntPtr lParam, ref bool handled);
}
