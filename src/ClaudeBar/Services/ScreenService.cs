namespace ClaudeBar.Services;

/// <summary>
/// Monitor enumeration and placement, entirely in PHYSICAL pixels.
///
/// No DPI conversion happens here by design: monitors come from Win32 (MonitorApi) and the
/// window is moved with SetWindowPos, both of which speak physical pixels. Keeping one unit
/// end to end is what makes the pill land correctly on a mixed-DPI, multi-monitor desk.
/// </summary>
public static class ScreenService
{
    public sealed record MonitorInfo(
        string DeviceName,
        string Label,
        bool IsPrimary,
        int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    public static IReadOnlyList<MonitorInfo> All()
    {
        var monitors = MonitorApi.Enumerate();
        var list = new List<MonitorInfo>(monitors.Count);
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var label = $"{i + 1}: {m.BoundsWidth}×{m.BoundsHeight}{(m.IsPrimary ? " (primary)" : "")}";
            list.Add(new MonitorInfo(m.DeviceName, label, m.IsPrimary,
                m.WorkLeft, m.WorkTop, m.WorkRight, m.WorkBottom));
        }

        // A desk with no monitors is not a real state, but never hand back an empty list.
        if (list.Count == 0)
            list.Add(new MonitorInfo(@"\\.\DISPLAY1", "1: unknown (primary)", true, 0, 0, 1920, 1040));

        return list;
    }

    public static MonitorInfo Resolve(string? deviceName)
    {
        var all = All();
        return all.FirstOrDefault(m => m.DeviceName == deviceName)
               ?? all.FirstOrDefault(m => m.IsPrimary)
               ?? all[0];
    }

    /// <summary>
    /// Bottom-right of the chosen monitor's working area — directly above the clock,
    /// since the working area already excludes the taskbar.
    /// </summary>
    public static (int X, int Y) AboveClock(MonitorInfo monitor, int width, int height,
        int offsetX, int offsetY)
    {
        var x = monitor.Right - width - offsetX;
        var y = monitor.Bottom - height - offsetY;
        return Clamp(monitor, x, y, width, height);
    }

    public static (int X, int Y) Clamp(MonitorInfo monitor, int x, int y, int width, int height)
    {
        var maxX = Math.Max(monitor.Left, monitor.Right - width);
        var maxY = Math.Max(monitor.Top, monitor.Bottom - height);
        return (Math.Clamp(x, monitor.Left, maxX), Math.Clamp(y, monitor.Top, maxY));
    }

    /// <summary>Which monitor holds this physical point — for remembering a dragged position.</summary>
    public static MonitorInfo FromPoint(int x, int y) =>
        All().FirstOrDefault(m => m.Contains(x, y)) ?? Resolve(null);
}
