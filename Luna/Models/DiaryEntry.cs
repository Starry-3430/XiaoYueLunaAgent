namespace Luna.Models;

/// <summary>
/// 日记正文实体（内容、模型、来源会话/轮次、Token 数）
/// </summary>
public class DiaryEntry
{
    public string LogicalDate { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? SourceSessionIds { get; set; }
    public string? SourceTurnIds { get; set; }
    public int? TokenCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}