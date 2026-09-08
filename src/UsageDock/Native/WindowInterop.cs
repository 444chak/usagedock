namespace UsageDock.Native;

/// <summary>
/// Window level Win32 tweaks that WPF does not expose: no taskbar entry, never take focus,
/// always on top, and Windows 11 rounded corners.
/// </summary>
public static class WindowInterop
{
    /// <summary>
    /// Applies the dock window styles.
    ///
    /// WS_EX_NOACTIVATE is what stops the window stealing focus from the app the user is typing
    /// in; WS_EX_TOOLWINDOW keeps it out of the taskbar and out of Alt+Tab.
    /// </summary>
    public static void ApplyDockStyles(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        var updated = style
                      | NativeMethods.WS_EX_TOOLWINDOW
                      | NativeMethods.WS_EX_NOACTIVATE
                      | NativeMethods.WS_EX_TOPMOST;

        NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, updated);

        EnableDarkMode(hwnd);
        RoundCorners(hwnd, NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND);
    }

    public static void EnableDarkMode(nint hwnd)
    {
        var enabled = 1;
        try
        {
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    public static void RoundCorners(nint hwnd, NativeMethods.DWM_WINDOW_CORNER_PREFERENCE preference)
    {
        var value = (int)preference;
        try
        {
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Pre Windows 11: square corners, no functional loss.
        }
    }

    /// <summary>
    /// Moves and resizes using physical pixels, without activating the window. Going through
    /// SetWindowPos rather than WPF Left/Top keeps multi monitor mixed DPI placement exact.
    /// </summary>
    public static void MoveResize(nint hwnd, int x, int y, int width, int height)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Re-asserts topmost without moving or resizing.</summary>
    public static void BringToTop(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static double GetDpiScale(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return 1d;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1d : dpi / 96d;
    }

    public static MonitorInfo? MonitorFor(nint hwnd) =>
        hwnd == nint.Zero
            ? null
            : MonitorLayout.Describe(
                NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
}
