using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>发送系统通知（托盘气泡）。风险 Low，直接执行。</summary>
public class SendNotificationTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"title":{"type":"string","description":"通知标题"},"message":{"type":"string","description":"通知正文"},"timeout_ms":{"type":"integer","description":"显示时长（毫秒），默认 5000"}},"required":["message"]}""");

    private readonly NotificationService _notifications;
    private readonly ILogger<SendNotificationTool> _logger;

    public string Name => "send_notification";
    public string DisplayName => "系统通知";
    public string Description => "发送一条 Windows 系统通知";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public SendNotificationTool(NotificationService notifications, ILogger<SendNotificationTool> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var message = GetString(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult(ToolResult.Error("缺少参数 message。"));

        var title = GetString(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            title = "Luna";

        var timeout = 5000;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms))
            timeout = Math.Clamp(ms, 1000, 60000);

        try
        {
            _notifications.Notify(title, message, timeout);
            return Task.FromResult(ToolResult.Ok("通知已发送。"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送系统通知失败");
            return Task.FromResult(ToolResult.Error($"发送通知失败：{ex.Message}"));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
