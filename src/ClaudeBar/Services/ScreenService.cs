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
    /// Where the pill sits for a given anchor. The working area already excludes the taskbar,
    /// so a bottom anchor snaps to the taskbar's edge, and to the screen edge without one.
    /// </summary>
    public static (int X, int Y) Place(MonitorInfo m, SnapAnchor anchor, int width, int height,
        int padding, int freeOffsetX, int freeOffsetY)
    {
        var (x, y) = anchor switch
        {
            SnapAnchor.BottomRight  => (m.Right - width - padding, m.Bottom - height - padding),
            SnapAnchor.BottomLeft   => (m.Left + padding,          m.Bottom - height - padding),
            SnapAnchor.TopRight     => (m.Right - width - padding, m.Top + padding),
            SnapAnchor.TopLeft      => (m.Left + padding,          m.Top + padding),
            SnapAnchor.BottomCentre => (Centre(m, width),          m.Bottom - height - padding),
            SnapAnchor.TopCentre    => (Centre(m, width),          m.Top + padding),
            _                       => (m.Right - width - freeOffsetX, m.Bottom - height - freeOffsetY)
        };

        return Clamp(m, x, y, width, height);
    }

    private static int Centre(MonitorInfo m, int width) => m.Left + (m.Right - m.Left - width) / 2;

    /// <summary>
    /// Works out which anchor a dropped pill should take: whichever corner or edge it landed
    /// within <paramref name="threshold"/> physical pixels of. Returns Free when it was
    /// dropped out in the open.
    /// </summary>
    public static SnapAnchor AnchorForDrop(MonitorInfo m, int left, int top, int width, int height,
        int threshold)
    {
        var nearLeft = left - m.Left <= threshold;
        var nearRight = m.Right - (left + width) <= threshold;
        var nearTop = top - m.Top <= threshold;
        var nearBottom = m.Bottom - (top + height) <= threshold;

        // Horizontally centred, within threshold of the midline.
        var centreX = m.Left + (m.Right - m.Left) / 2;
        var nearCentre = Math.Abs(left + width / 2 - centreX) <= threshold;

        return (nearBottom, nearTop, nearLeft, nearRight, nearCentre) switch
        {
            (true, _, true, _, _) => SnapAnchor.BottomLeft,
            (true, _, _, true, _) => SnapAnchor.BottomRight,
            (_, true, true, _, _) => SnapAnchor.TopLeft,
            (_, true, _, true, _) => SnapAnchor.TopRight,
            (true, _, _, _, true) => SnapAnchor.BottomCentre,
            (_, true, _, _, true) => SnapAnchor.TopCentre,
            _ => SnapAnchor.Free
        };
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
