using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public sealed class WindowManagerTool : ITool
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{"action":{"type":"string","enum":["list","minimize","maximize","restore","move","resize","close"]},"title":{"type":"string","description":"窗口标题，支持包含匹配"},"hwnd":{"type":"string","description":"窗口句柄，支持十进制或 0x 十六进制"},"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}},"required":[]}""");
    private readonly ILogger<WindowManagerTool> _logger;
    public WindowManagerTool(ILogger<WindowManagerTool> logger) => _logger = logger;
    public string Name => "window_manager";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.High;

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = GetString(args, "action").ToLowerInvariant(); if (string.IsNullOrEmpty(action)) action = "list";
        if (action == "list") return Task.FromResult(ListWindows());
        if (action is not ("minimize" or "maximize" or "restore" or "move" or "resize" or "close")) return Task.FromResult(ToolResult.Error("action 必须是 list、minimize、maximize、restore、move、resize 或 close。"));
        var hwnd = FindWindow(args); if (hwnd == IntPtr.Zero) return Task.FromResult(ToolResult.Error("未找到匹配的可见窗口，请提供 title 或 hwnd。"));
        try
        {
            switch (action)
            {
                case "minimize": ShowWindow(hwnd, 6); break;
                case "maximize": ShowWindow(hwnd, 3); break;
                case "restore": ShowWindow(hwnd, 9); break;
                case "close": PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); break;
                case "move": if (!TryGetInt(args, "x", out var x) || !TryGetInt(args, "y", out var y)) return Task.FromResult(ToolResult.Error("move 需要 x 和 y。")); if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoZOrder | SwpNoActivate)) throw LastError(); break;
                case "resize": if (!TryGetInt(args, "width", out var width) || !TryGetInt(args, "height", out var height) || width < 100 || height < 80) return Task.FromResult(ToolResult.Error("resize 需要至少 100x80 的 width 和 height。")); if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate)) throw LastError(); break;
            }
            _logger.LogInformation("Window operation {Action} executed on {Hwnd}", action, hwnd);
            return Task.FromResult(ToolResult.Ok($"已对窗口执行 {action} 操作。"));
        }
        catch (Exception ex) { _logger.LogError(ex, "Window operation failed"); return Task.FromResult(ToolResult.Error($"窗口操作失败：{ex.Message}")); }
    }

    private static ToolResult ListWindows()
    {
        var rows = new List<string>(); EnumWindows((hwnd, _) => { if (!IsWindowVisible(hwnd)) return true; var title = GetTitle(hwnd); if (string.IsNullOrWhiteSpace(title)) return true; if (GetWindowRect(hwnd, out var rect)) rows.Add($"0x{hwnd.ToInt64():X} {title} [{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}]"); return true; }, IntPtr.Zero);
        return ToolResult.Ok(rows.Count == 0 ? "没有找到可见窗口。" : "可见窗口：\n" + string.Join('\n', rows));
    }
    private static IntPtr FindWindow(JsonElement args)
    {
        if (TryGetString(args, "hwnd", out var raw) && TryParseHandle(raw, out var parsed) && IsWindowVisible(parsed)) return parsed;
        var title = GetString(args, "title"); if (string.IsNullOrWhiteSpace(title)) return IntPtr.Zero; IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, _) => { if (IsWindowVisible(hwnd) && GetTitle(hwnd).Contains(title, StringComparison.OrdinalIgnoreCase)) { result = hwnd; return false; } return true; }, IntPtr.Zero); return result;
    }
    private static string GetTitle(IntPtr hwnd) { var sb = new StringBuilder(512); GetWindowText(hwnd, sb, sb.Capacity); return sb.ToString(); }
    private static bool TryParseHandle(string raw, out IntPtr hwnd) { hwnd = IntPtr.Zero; try { var s = raw.Trim(); hwnd = new IntPtr(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(s[2..], 16) : Convert.ToInt64(s)); return hwnd != IntPtr.Zero; } catch { return false; } }
    private static string GetString(JsonElement args, string name) => TryGetString(args, name, out var value) ? value : string.Empty;
    private static bool TryGetString(JsonElement args, string name, out string value) { value = string.Empty; if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return false; value = v.GetString()?.Trim() ?? string.Empty; return value.Length > 0; }
    private static bool TryGetInt(JsonElement args, string name, out int value) { value = 0; return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out value); }
    private static Exception LastError() => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}
