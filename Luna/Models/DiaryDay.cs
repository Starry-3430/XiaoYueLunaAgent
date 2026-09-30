namespace Luna.Models;

public class DiaryDay
{
    public string LogicalDate { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}