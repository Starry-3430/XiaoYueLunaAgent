namespace Luna.Models;

/// <summary>
/// 轮次摘要实体（JSON 摘要、逻辑日期、模型、Token 数）
/// </summary>
public class TurnSummary
{
    public string TurnId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public int TurnIndex { get; set; }
    public string LogicalDate { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int? TokenCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}