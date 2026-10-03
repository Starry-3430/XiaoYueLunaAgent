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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
