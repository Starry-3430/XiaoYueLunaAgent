using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public sealed class ScreenshotTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Empty;
    private readonly ILogger<ScreenshotTool> _logger;
    public ScreenshotTool(ILogger<ScreenshotTool> logger) => _logger = logger;
    public string Name => "screenshot";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(ToolResult.Error(ToolLocalization.Text("Tool_Screenshot_Msg_Cancelled")));
        IntPtr bitmap = IntPtr.Zero;
        IntPtr screen = IntPtr.Zero;
        IntPtr memory = IntPtr.Zero;
        try
        {
            var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
                return Task.FromResult(ToolResult.Error(ToolLocalization.Text("Tool_Screenshot_Msg_InvalidSize")));
            screen = GetDC(IntPtr.Zero); memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero || SelectObject(memory, bitmap) == IntPtr.Zero || !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); source.Freeze();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luna", "screenshots"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            using (var stream = File.Create(path)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream); }
            _logger.LogInformation("Screenshot saved to {Path}", path);
            return Task.FromResult(ToolResult.Ok(ToolLocalization.Text("Tool_Screenshot_Msg_Saved", path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "Screenshot capture failed"); return Task.FromResult(ToolResult.Error(ToolLocalization.Text("Tool_Screenshot_Msg_Failed", ex.Message))); }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (memory != IntPtr.Zero) DeleteDC(memory); if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen); }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr objectHandle);
}
