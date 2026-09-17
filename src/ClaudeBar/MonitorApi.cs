using System.Runtime.InteropServices;

namespace ClaudeBar;

/// <summary>
/// Monitor enumeration straight from Win32.
///
/// WinForms' Screen class was tried first and reported a different coordinate space than
/// SetWindowPos used, which parked the pill past the edge of the primary monitor and onto
/// the next one. EnumDisplayMonitors/GetMonitorInfo return true physical pixels in the same
/// space SetWindowPos takes, so placement and enumeration cannot drift apart.
/// </summary>
internal static class MonitorApi
{
    private const int MonitorinfofPrimary = 0x00000001;
    private const int CchDevicename = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RectL rcMonitor;
        public RectL rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDevicename)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    public sealed record Info(
        string DeviceName,
        bool IsPrimary,
        int BoundsWidth, int BoundsHeight,
        int WorkLeft, int WorkTop, int WorkRight, int WorkBottom);

    public static List<Info> Enumerate()
    {
        var list = new List<Info>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(handle, ref mi))
            {
                list.Add(new Info(
                    mi.szDevice,
                    (mi.dwFlags & MonitorinfofPrimary) != 0,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom));
            }
            return true;
        }, IntPtr.Zero);

        // Primary first, then left-to-right, so the menu order matches the desk.
        return list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.WorkLeft)
            .ToList();
    }
}
