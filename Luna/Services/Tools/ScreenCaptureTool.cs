// 工具：截取屏幕/当前窗口（Win32 原生 API 声明）。
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

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

/// <summary>截取当前屏幕画面。归属“截图”开关（screenshot），执行前需用户确认。</summary>
public class ReadScreenTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse("""
    {
        "type": "object",
        "properties": {
            "mode": {
                "type": "string",
                "enum": ["active_window", "fullscreen"],
                "description": "${mode}"
            }
        }
    }
    """.Replace("${mode}", Param("Tool_ReadScreen_Param_Mode")));

    private readonly ILogger<ReadScreenTool> _logger;

    public string Name => "read_screen";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;

    // 隐私级别极高，必须弹窗确认
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;
    public string SettingsKey => "screenshot";

    public ReadScreenTool(ILogger<ReadScreenTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var mode = "active_window";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mode", out var m))
            mode = m.GetString() ?? "active_window";

        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // 1. 截图并压缩
                using var bitmap = ScreenCaptureCore.Capture(mode);

                // 2. 保存到临时目录
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Luna", "cache", "screenshots");
                Directory.CreateDirectory(cacheDir);

                var filePath = Path.Combine(cacheDir, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");

                // 3. 以 JPEG 格式保存，质量 80%
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                var jpegCodec = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                    ?? throw new InvalidOperationException("未找到 JPEG 编码器");

                bitmap.Save(filePath, jpegCodec, encoderParams);
                _logger.LogInformation("屏幕截图已保存: {Path} (模式: {Mode})", filePath, mode);

                return ToolResult.Ok(Text("Tool_ReadScreen_Msg_Captured", mode, filePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "屏幕截图失败");
                return ToolResult.Error(Text("Tool_ReadScreen_Msg_Failed", ex.Message));
            }
        }, ct);
    }
}
