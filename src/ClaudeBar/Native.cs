using System.Runtime.InteropServices;

namespace ClaudeBar;

internal static class Native
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointL lpPoint);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// Marks the pill as a tool window: no Alt-Tab entry, no taskbar button.
    /// It still takes clicks, so the context menu and dragging keep working.
    /// </summary>
    public static void MakeToolWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        style |= WsExToolWindow;
        style &= ~WsExAppWindow;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)style);
    }

    /// <summary>
    /// Makes a window click-through and non-activating: the drag ghost must never take
    /// the mouse away from the pill being dragged, nor steal focus from the app behind.
    /// </summary>
    public static void MakeGhost(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        style |= WsExToolWindow | WsExTransparent | WsExNoActivate;
        style &= ~WsExAppWindow;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)style);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    /// <summary>A WinExe has no console; borrow the caller's so --selftest can be read.</summary>
    public static void AttachConsole()
    {
        if (!AttachConsole(-1)) AllocConsole();
    }

    /// <summary>Cursor position in physical pixels, matching everything else in placement.</summary>
    public static (int X, int Y) CursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>The window's real rectangle, in physical pixels.</summary>
    public static Rect GetBounds(IntPtr handle) =>
        handle != IntPtr.Zero && GetWindowRect(handle, out var r) ? r : default;

    /// <summary>
    /// Moves the window in PHYSICAL pixels. Window.Left/Top are device-independent units
    /// whose meaning shifts with the window's current monitor under PerMonitorV2, so any
    /// arithmetic mixing them with Screen bounds lands the pill on the wrong monitor.
    /// Win32 coordinates are unambiguous, so placement goes through here.
    /// </summary>
    public static void MoveTo(IntPtr handle, int x, int y)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static bool IsTopmost(IntPtr handle) =>
        handle != IntPtr.Zero && ((long)GetWindowLongPtr(handle, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>
    /// Back to the top of the topmost band, without taking focus. Window.Topmost cannot do
    /// this: WPF skips setting a property to the value it already holds, and Windows can take
    /// the flag away while the property still says true.
    /// </summary>
    public static void BringToTopmost(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Why the window is no longer on top, or null while it is. Either its topmost flag is
    /// gone, or an ordinary window from another program is above it and over <paramref name="area"/>
    /// anyway. A TOPMOST window over it does not count: that is a menu, a tooltip or some other
    /// always-on-top program, and climbing over those would be a fight, not a fix. Our own
    /// windows - menus, toast, settings - do not count either.
    /// </summary>
    public static string? Buried(IntPtr handle, Rect area)
    {
        if (handle == IntPtr.Zero) return null;
        if (!IsTopmost(handle)) return "lost its topmost flag";

        // EnumWindows walks the z-order from the top. Everything it passes before reaching
        // this window is above it. (A GetWindow(GW_HWNDPREV) loop can spin forever while the
        // z-order is changing under it; EnumWindows takes a snapshot.)
        var self = (uint)Environment.ProcessId;
        string? cover = null;
        EnumWindows((w, _) =>
        {
            if (w == handle) return false;
            if (!IsWindowVisible(w) || IsTopmost(w) || IsCloaked(w)) return true;
            GetWindowThreadProcessId(w, out var pid);
            if (pid == self) return true;
            if (!GetWindowRect(w, out var r) || !Overlaps(r, area)) return true;

            var cls = new System.Text.StringBuilder(128);
            GetClassName(w, cls, cls.Capacity);
            cover = $"under {ProcessName(pid)} ({cls})";
            return false;
        }, IntPtr.Zero);
        return cover;
    }

    /// <summary>
    /// Windows that are "visible" but not drawn: suspended Store apps, and every window on
    /// another virtual desktop. They sit in the z-order like anything else and cover nothing.
    /// </summary>
    private static bool IsCloaked(IntPtr handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return "pid " + pid; }
    }
}
