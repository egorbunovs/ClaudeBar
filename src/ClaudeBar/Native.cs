using System.Runtime.InteropServices;

namespace ClaudeBar;

internal static class Native
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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
}
