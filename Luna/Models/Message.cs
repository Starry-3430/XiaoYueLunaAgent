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

    /// <summary>是否为当前激活的版本（1/0，SQLite 布尔）。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>版本分组 ID；同一次提问的不同回复共享。</summary>
    public string? VersionGroupId { get; set; }

    /// <summary>版本序号（同一次提问的第几个回答，从 1 开始；0 表示旧数据尚未写入）。</summary>
    public int VersionIndex { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string LogicalDate { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}