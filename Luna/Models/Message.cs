namespace Luna.Models;

/// <summary>
/// 消息实体（角色、内容、思考内容、工具调用、逻辑日期等）
/// </summary>
public class Message
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ReasoningContent { get; set; } = string.Empty;
    public string ToolCallsJson { get; set; } = string.Empty;
    public string ToolCallId { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string LogicalDate { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}