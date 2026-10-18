// 工具：截取屏幕/当前窗口（Win32 原生 API 声明）。
using System.Runtime.InteropServices;

namespace Luna.Services.Tools;

internal static class Win32Api
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // 获取真实的窗口边界（排除 Win10/11 的窗口阴影）
    public static RECT GetWindowBounds(IntPtr hWnd)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS = 9
        if (DwmGetWindowAttribute(hWnd, 9, out var rect, Marshal.SizeOf(typeof(RECT))) == 0)
            return rect;

        GetWindowRect(hWnd, out rect);
        return rect;
    }
}
