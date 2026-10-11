namespace Luna.Models;

/// <summary>
/// 某个逻辑日的日记生成状态（Pending / Done / Failed）
/// </summary>
public class DiaryDay
{
    public string LogicalDate { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}