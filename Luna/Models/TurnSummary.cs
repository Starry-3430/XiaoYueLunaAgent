namespace Luna.Models;

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