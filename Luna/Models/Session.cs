namespace Luna.Models;

public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Title { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
    public string? Preview { get; set; }
}