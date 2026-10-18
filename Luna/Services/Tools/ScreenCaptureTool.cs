// 工具：截取屏幕/当前窗口（Win32 原生 API 声明）。
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;
using WpfApplication = System.Windows.Application;

namespace Luna.Services.Tools;

internal static class Win32Api
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SwHide = 0;
    public const int SwShow = 5;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2（-4）。</summary>
    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);

    /// <summary>把当前线程切到 Per-Monitor V2，返回原上下文（失败时返回 IntPtr.Zero）。</summary>
    public static IntPtr EnterPerMonitorDpiAwareness() =>
        SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorV2);

    /// <summary>恢复线程原有的 DPI 上下文。</summary>
    public static void RestoreDpiAwareness(IntPtr previous)
    {
        if (previous != IntPtr.Zero)
            SetThreadDpiAwarenessContext(previous);
    }

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
        // 在截图线程上临时切到 Per-Monitor V2，保证窗口边界、虚拟屏幕与 CopyFromScreen
        // 使用同一套物理像素坐标（避免高 DPI 下只截到左上角或图像被拉伸）。
        var previousDpi = Win32Api.EnterPerMonitorDpiAwareness();
        try
        {
            Rectangle bounds;
            var virtualScreen = SystemInformation.VirtualScreen;

            if (mode == "active_window")
            {
                var hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("无法获取当前活动窗口");

                var rect = Win32Api.GetWindowBounds(hwnd);
                bounds = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);

                // 截图前 Luna 窗口会被移出屏幕；若它仍是前台窗口，边界会落在虚拟屏幕之外。
                // 此时退化为全屏，避免以负坐标截到黑图/空图。
                bounds = Rectangle.Intersect(bounds, virtualScreen);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    bounds = virtualScreen;
            }
            else
            {
                // 全屏：获取所有显示器的并集区域（多屏兼容）
                bounds = virtualScreen;
            }

            // 创建画布并截图
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            // 缩放限制（最大宽度 1920，防止 AI 拒绝请求）
            return ResizeIfNeeded(bitmap, 1920);
        }
        finally
        {
            Win32Api.RestoreDpiAwareness(previousDpi);
        }
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
    /// <summary>把 Luna 窗口移出所有屏幕的坐标（足够覆盖多屏向左扩展的情况）。</summary>
    private const double OffScreenCoordinate = -10000d;

    /// <summary>移走窗口后等待 DWM 重新合成一帧、并让下方窗口重绘的时间。</summary>
    private const int ScreenshotDelayMs = 500;

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

        var movedWindows = new Dictionary<Window, WindowSnapshot>();

        try
        {
            // 1. 把 Luna 自己的窗口临时移出屏幕（最大化窗口退化为隐藏）
            movedWindows = await MoveLunaWindowsOffScreenAsync();

            // 2. 等待 DWM 合成新一帧，并让下方窗口获得焦点/重绘
            await Task.Delay(ScreenshotDelayMs, ct);

            // 3. 在后台线程截图并保存，避免大屏幕截图阻塞 UI
            return await Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    using var bitmap = ScreenCaptureCore.Capture(mode);

                    var cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Luna", "cache", "screenshots");
                    Directory.CreateDirectory(cacheDir);

                    var filePath = Path.Combine(cacheDir, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");

                    // 以 JPEG 格式保存，质量 80%
                    using var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                    var jpegCodec = ImageCodecInfo.GetImageEncoders()
                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                        ?? throw new InvalidOperationException("未找到 JPEG 编码器");

                    bitmap.Save(filePath, jpegCodec, encoderParams);
                    _logger.LogInformation("屏幕截图已保存: {Path} (模式: {Mode})", filePath, mode);

                    return ToolResult.OkWithImage(Text("Tool_ReadScreen_Msg_Captured", mode, filePath), filePath);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "屏幕截图失败");
                    return ToolResult.Error(Text("Tool_ReadScreen_Msg_Failed", ex.Message));
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error(Text("Tool_Msg_Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "屏幕截图失败");
            return ToolResult.Error(Text("Tool_ReadScreen_Msg_Failed", ex.Message));
        }
        finally
        {
            // 4. 【必须】无论成功、失败还是取消，都恢复 Luna 窗口
            await RestoreLunaWindowsAsync(movedWindows);
        }
    }

    /// <summary>记录窗口原状态，便于截图后恢复。</summary>
    private readonly record struct WindowSnapshot(
        WindowState State, double Left, double Top, bool Hidden);

    /// <summary>在 UI 线程上把所有可见的 Luna 窗口移出屏幕；返回恢复所需的状态。</summary>
    private async Task<Dictionary<Window, WindowSnapshot>> MoveLunaWindowsOffScreenAsync()
    {
        var saved = new Dictionary<Window, WindowSnapshot>();
        var app = WpfApplication.Current;
        var dispatcher = app?.Dispatcher;
        if (app is null || dispatcher is null || dispatcher.HasShutdownStarted)
            return saved;

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                foreach (var window in app.Windows.OfType<Window>().ToList())
                {
                    try
                    {
                        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
                            continue;

                        if (window.WindowState == WindowState.Maximized)
                        {
                            // 最大化窗口不移动，直接隐藏：移动会污染 RestoreBounds，
                            // 导致用户以后取消最大化时窗口跑到屏幕外。
                            var hwnd = new WindowInteropHelper(window).Handle;
                            if (hwnd == IntPtr.Zero) continue;

                            saved[window] = new WindowSnapshot(window.WindowState, window.Left, window.Top, Hidden: true);
                            Win32Api.ShowWindow(hwnd, Win32Api.SwHide);
                        }
                        else
                        {
                            saved[window] = new WindowSnapshot(window.WindowState, window.Left, window.Top, Hidden: false);
                            window.Left = OffScreenCoordinate;
                            window.Top = OffScreenCoordinate;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "移出 Luna 窗口失败");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移出 Luna 窗口失败");
        }

        return saved;
    }

    /// <summary>在 UI 线程上恢复所有被移动/隐藏的 Luna 窗口。</summary>
    private async Task RestoreLunaWindowsAsync(IReadOnlyDictionary<Window, WindowSnapshot> saved)
    {
        if (saved.Count == 0) return;

        var app = WpfApplication.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                foreach (var (window, snapshot) in saved)
                {
                    try
                    {
                        if (snapshot.Hidden)
                        {
                            var hwnd = new WindowInteropHelper(window).Handle;
                            if (hwnd != IntPtr.Zero)
                                Win32Api.ShowWindow(hwnd, Win32Api.SwShow);
                            continue;
                        }

                        window.Left = snapshot.Left;
                        window.Top = snapshot.Top;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "恢复 Luna 窗口位置失败");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复 Luna 窗口位置失败");
        }
    }
}
