using UsageDock.Services;

namespace UsageDock.Native;

/// <summary>
/// One display, in physical pixels, with its effective DPI scale.
/// </summary>
public sealed record MonitorInfo(
    string DeviceName,
    NativeMethods.RECT Bounds,
    NativeMethods.RECT WorkArea,
    double Scale,
    bool IsPrimary)
{
    /// <summary>Converts a logical (96 dpi) length into physical pixels on this monitor.</summary>
    public int ToPhysical(double logical) => (int)Math.Round(logical * Scale, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Enumerates monitors and resolves which one the dock should sit on. All values are physical
/// pixels in virtual screen space, which is the coordinate system SetWindowPos expects, so
/// nothing here depends on the DPI of the primary display.
/// </summary>
public static class MonitorLayout
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            NativeMethods.EnumDisplayMonitors(
                nint.Zero,
                nint.Zero,
                (nint handle, nint _, ref NativeMethods.RECT _, nint _) =>
                {
                    if (Describe(handle) is { } info)
                    {
                        monitors.Add(info);
                    }

                    return true;
                },
                nint.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Monitor enumeration failed", ex);
        }

        return monitors;
    }

    public static MonitorInfo? Describe(nint monitorHandle)
    {
        if (monitorHandle == nint.Zero)
        {
            return null;
        }

        var info = new NativeMethods.MONITORINFOEXW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEXW>()
        };

        if (!NativeMethods.GetMonitorInfoW(monitorHandle, ref info))
        {
            return null;
        }

        var scale = GetScale(monitorHandle);
        const uint MONITORINFOF_PRIMARY = 0x00000001;

        return new MonitorInfo(
            info.szDevice ?? string.Empty,
            info.rcMonitor,
            info.rcWork,
            scale,
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    private static double GetScale(nint monitorHandle)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(
                    monitorHandle, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96d;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore is present on every supported Windows version, but never crash on it.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1d;
    }

    public static MonitorInfo? Primary() =>
        All().FirstOrDefault(static m => m.IsPrimary) ?? All().FirstOrDefault();

    /// <summary>
    /// Picks the configured monitor, falling back to the primary one when that display has been
    /// unplugged or renamed.
    /// </summary>
    public static MonitorInfo? Resolve(string? preferredDeviceName)
    {
        var monitors = All();
        if (monitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredDeviceName))
        {
            var match = monitors.FirstOrDefault(
                m => string.Equals(m.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return monitors.FirstOrDefault(static m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorInfo? UnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return null;
        }

        return Describe(NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST));
    }
}
