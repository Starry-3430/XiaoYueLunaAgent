using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Luna.Services;

public static class WindowPositioner
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public static void PlaceTopCenter(Window window, double topMarginDip = 8)
    {
        if (!window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var hMonitor = GetTargetMonitor(hwnd);
        if (hMonitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return;
        }

        var scale = GetScale(hMonitor);

        var widthDip = window.ActualWidth > 0
            ? window.ActualWidth
            : double.IsNaN(window.Width) ? 0 : window.Width;

        if (widthDip <= 0)
        {
            return;
        }

        var widthPx = (int)Math.Round(widthDip * scale);
        var marginPx = (int)Math.Round(topMarginDip * scale);

        var work = info.RcWork;
        var x = work.Left + (work.Width - widthPx) / 2;
        var y = work.Top + marginPx;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static IntPtr GetTargetMonitor(IntPtr hwnd)
    {
        if (GetCursorPos(out var cursor))
        {
            var fromCursor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            if (fromCursor != IntPtr.Zero)
            {
                return fromCursor;
            }
        }

        return MonitorFromWindow(hwnd, MonitorDefaultToPrimary);
    }

    private static double GetScale(IntPtr hMonitor)
    {
        return GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;
    }
}
