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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>WDA_NONE：取消截图排除。</summary>
    public const uint WdaNone = 0x00000000;

    /// <summary>WDA_EXCLUDEFROMCAPTURE：窗口照常显示，但不进入任何截屏结果（Win10 2004+）。</summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>阻塞到 DWM 完成下一次桌面合成，用于确保窗口移动/隐藏已生效。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    /// <summary>指定窗口是否属于当前 Luna 进程。</summary>
    public static bool IsOwnProcessWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

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
    public static Bitmap Capture(string mode) => ResizeIfNeeded(CaptureRaw(mode), 1920);

    /// <summary>只做屏幕复制，不做缩放；缩放可以等 Luna 窗口恢复后再执行。</summary>
    public static Bitmap CaptureRaw(string mode)
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

                if (Win32Api.IsOwnProcessWindow(hwnd))
                {
                    // 前台仍是 Luna（可能只是被排除在截图外，窗口本身还在屏幕上）：
                    // active_window 没有意义，改为整屏，才能看到 Luna 后面的内容。
                    bounds = virtualScreen;
                }
                else
                {
                    var rect = Win32Api.GetWindowBounds(hwnd);
                    bounds = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);

                    // 截图前 Luna 窗口可能被移出屏幕；若边界落在虚拟屏幕之外，退化为全屏，
                    // 避免以负坐标截到黑图/空图。
                    bounds = Rectangle.Intersect(bounds, virtualScreen);
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        bounds = virtualScreen;
                }
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
            return bitmap;
        }
        finally
        {
            Win32Api.RestoreDpiAwareness(previousDpi);
        }
    }

    public static Bitmap ResizeIfNeeded(Bitmap original, int maxWidth)
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

    /// <summary>
    /// 用户接受截图指令（若需授权，则从授权完成）后、真正截图前的等待时间。
    /// 工具执行入口本身就在授权完成之后，所以在这里等待即可。
    /// </summary>
    private const int UserInstructionDelayMs = 500;

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
        var excludedHandles = new List<IntPtr>();
        Bitmap? rawBitmap = null;

        try
        {
            // 1. 用户指令已接受（需要授权时，授权已在进入本方法前完成）→ 先等 0.5 秒。
            //    这段时间 Luna 仍然可见，不做任何窗口移动。
            await Task.Delay(UserInstructionDelayMs, ct);

            // 2. 优先用 WDA_EXCLUDEFROMCAPTURE 让 Luna 完全不消失；
            //    仅当系统/窗口不支持（如 Win10 2004 以前）时才回退到移屏。
            excludedHandles = await ExcludeLunaWindowsFromCaptureAsync();
            if (excludedHandles.Count == 0)
                movedWindows = await MoveLunaWindowsOffScreenAsync();

            // 3. 等一帧合成后立刻 CopyFromScreen，随后马上恢复。
            rawBitmap = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                Win32Api.DwmFlush();
                return ScreenCaptureCore.CaptureRaw(mode);
            }, ct);

            // 4. 像素已在内存中，立刻恢复；缩放和 JPEG 编码都放到恢复之后。
            RestoreCaptureExclusion(excludedHandles);
            await RestoreLunaWindowsAsync(movedWindows);
            movedWindows.Clear();

            var filePath = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Luna", "cache", "screenshots");
                Directory.CreateDirectory(cacheDir);

                var path = Path.Combine(cacheDir, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");

                using var bitmap = ScreenCaptureCore.ResizeIfNeeded(rawBitmap, 1920);

                // 以 JPEG 格式保存，质量 80%
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                var jpegCodec = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                    ?? throw new InvalidOperationException("未找到 JPEG 编码器");

                bitmap.Save(path, jpegCodec, encoderParams);
                return path;
            }, ct);

            _logger.LogInformation("屏幕截图已保存: {Path} (模式: {Mode})", filePath, mode);
            return ToolResult.OkWithImage(Text("Tool_ReadScreen_Msg_Captured", mode, filePath), filePath);
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
            rawBitmap?.Dispose();
            // 5. 【必须】无论成功、失败还是取消，都恢复截图排除与窗口位置
            RestoreCaptureExclusion(excludedHandles);
            await RestoreLunaWindowsAsync(movedWindows);
        }
    }

    /// <summary>
    /// 给所有可见的 Luna 窗口设置 WDA_EXCLUDEFROMCAPTURE，使窗口照常显示但不进入截图。
    /// 任一窗口不支持（或系统早于 Win10 2004）时回滚并返回空列表，由调用方改用移屏兜底。
    /// </summary>
    private async Task<List<IntPtr>> ExcludeLunaWindowsFromCaptureAsync()
    {
        var handles = new List<IntPtr>();
        var app = WpfApplication.Current;
        var dispatcher = app?.Dispatcher;
        if (app is null || dispatcher is null || dispatcher.HasShutdownStarted)
            return handles;

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                var windowHandles = new List<IntPtr>();
                foreach (var window in app.Windows.OfType<Window>().ToList())
                {
                    if (!window.IsVisible || window.WindowState == WindowState.Minimized)
                        continue;

                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero)
                        return;

                    windowHandles.Add(hwnd);
                }

                if (windowHandles.Count == 0)
                    return;

                foreach (var hwnd in windowHandles)
                {
                    if (!Win32Api.SetWindowDisplayAffinity(hwnd, Win32Api.WdaExcludeFromCapture))
                    {
                        RollbackExclusion(handles);
                        return;
                    }

                    handles.Add(hwnd);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设置截图排除失败，改用移屏兜底");
            RollbackExclusion(handles);
        }

        return handles;

        void RollbackExclusion(List<IntPtr> applied)
        {
            foreach (var hwnd in applied)
            {
                try { Win32Api.SetWindowDisplayAffinity(hwnd, Win32Api.WdaNone); }
                catch { /* 忽略回滚失败 */ }
            }

            applied.Clear();
        }
    }

    /// <summary>取消所有窗口的截图排除设置。</summary>
    private void RestoreCaptureExclusion(List<IntPtr> handles)
    {
        if (handles.Count == 0) return;

        foreach (var hwnd in handles)
        {
            try
            {
                Win32Api.SetWindowDisplayAffinity(hwnd, Win32Api.WdaNone);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "恢复截图排除设置失败");
            }
        }

        handles.Clear();
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
