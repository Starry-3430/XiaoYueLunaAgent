// 待办事项实体（截止时间、提醒时间、状态、提醒次数）

namespace Luna.Models;

/// <summary>待办事项。所有时间以 UTC 存储。</summary>
public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? RemindAtUtc { get; set; }

    /// <summary>pending / done / cancelled</summary>
    public string Status { get; set; } = "pending";

    public int Priority { get; set; }
    public int ReminderCount { get; set; }
    public DateTime? LastRemindedAtUtc { get; set; }

    /// <summary>最后一次主动跟进（催办）的 UTC 时间，用于按天去重。</summary>
    public DateTime? LastFollowUpUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
