// 工具：发送 Windows 系统通知。
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>发送系统通知（托盘气泡）。风险 Low，直接执行。</summary>
public class SendNotificationTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"title":{"type":"string","description":"$title$"},"message":{"type":"string","description":"$message$"},"timeout_ms":{"type":"integer","description":"$timeoutMs$"}},"required":["message"]}""";

    private readonly NotificationService _notifications;
    private readonly ILogger<SendNotificationTool> _logger;

    public string Name => "send_notification";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$title$", Param("Tool_SendNotification_Param_Title"))
        .Replace("$message$", Param("Tool_SendNotification_Param_Message"))
        .Replace("$timeoutMs$", Param("Tool_SendNotification_Param_TimeoutMs")));
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
            return Task.FromResult(ToolResult.Error(Text("Tool_Msg_MissingParam", "message")));

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
            return Task.FromResult(ToolResult.Ok(Text("Tool_SendNotification_Msg_Sent")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送系统通知失败");
            return Task.FromResult(ToolResult.Error(Text("Tool_SendNotification_Msg_Failed", ex.Message)));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
