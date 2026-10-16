// 长期事实实体：AI 从对话中提炼的稳定事实（键值对，按 Key 去重）

namespace Luna.Models;

/// <summary>长期事实。所有时间以 UTC 文本存储；Confidence 预留给未来，当前恒为 1.0。</summary>
public class Fact
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public double Confidence { get; set; } = 1.0;
    public string? SourceTurnId { get; set; }
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
