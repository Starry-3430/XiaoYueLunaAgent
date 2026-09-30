namespace Luna.Models;

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