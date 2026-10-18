// 工具：截取屏幕/当前窗口（Win32 原生 API 声明）。
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

public static class ScreenCaptureCore
{
    public static Bitmap Capture(string mode)
    {
        Rectangle bounds;

        if (mode == "active_window")
        {
            var hwnd = Win32Api.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("无法获取当前活动窗口");

            var rect = Win32Api.GetWindowBounds(hwnd);
            bounds = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
        }
        else
        {
            // 全屏：获取所有显示器的并集区域（多屏兼容）
            bounds = SystemInformation.VirtualScreen;
        }

        // 创建画布并截图
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        // 缩放限制（最大宽度 1920，防止 AI 拒绝请求）
        return ResizeIfNeeded(bitmap, 1920);
    }

    private static Bitmap ResizeIfNeeded(Bitmap original, int maxWidth)
    {
        if (original.Width <= maxWidth) return original;

        double ratio = (double)maxWidth / original.Width;
        int newHeight = (int)(original.Height * ratio);

        var resized = new Bitmap(maxWidth, newHeight);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(original, 0, 0, maxWidth, newHeight);

        original.Dispose();
        return resized;
    }
}
