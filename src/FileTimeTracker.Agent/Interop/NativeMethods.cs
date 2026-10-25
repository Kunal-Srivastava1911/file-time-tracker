using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FileTimeTracker.Agent.Interop;

/// <summary>
/// The user32 surface from design.md §4.1: foreground window, owning process, window text, last
/// input time, and the input-desktop check used to detect a locked workstation.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    internal const int DesktopReadObjects = 0x0001;
    internal const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // ---- Locked-workstation detection -------------------------------------------------------
    // Session notifications can be missed (agent started while already locked, event storm during
    // fast user switching), so the sampler also verifies the input desktop directly each tick.

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(
        IntPtr hObj, int nIndex, [Out] byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);

    /// <summary>Reads a window's title, returning empty rather than throwing on any failure.</summary>
    internal static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowText(hWnd, buffer, buffer.Capacity);

        return copied > 0 ? buffer.ToString(0, copied) : string.Empty;
    }

    /// <summary>
    /// Milliseconds since the last keyboard or mouse input. Unsigned arithmetic is deliberate:
    /// the tick counter wraps every ~49.7 days and a signed subtraction would report a wildly
    /// negative idle time on a laptop that has been up that long.
    /// </summary>
    internal static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>
    /// True when the workstation is locked. The input desktop is named <c>Winlogon</c> while
    /// locked and <c>Default</c> otherwise; if it cannot be opened at all we are not on the input
    /// desktop, which also means "not counted" under §3.
    /// </summary>
    internal static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            var buffer = new byte[256];
            if (!GetUserObjectInformation(desktop, UoiName, buffer, (uint)buffer.Length, out var needed))
            {
                return true;
            }

            var chars = (int)Math.Min(needed, (uint)buffer.Length) / sizeof(char);
            var name = Encoding.Unicode.GetString(buffer, 0, chars * sizeof(char)).TrimEnd('\0');

            return !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
}
