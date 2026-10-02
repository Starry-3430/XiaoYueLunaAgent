namespace Luna.Models;

public class Message
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ReasoningContent { get; set; } = string.Empty;
    public string ToolCallsJson { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string LogicalDate { get; set; } = string.Empty;
}