namespace Luna.Models;

/// <summary>
/// 日记全文检索（FTS5）表模型
/// </summary>
public class DiaryFts
{
    public string LogicalDate { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}