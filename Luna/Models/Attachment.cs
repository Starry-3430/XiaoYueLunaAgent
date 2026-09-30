namespace Luna.Models;

public class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int MessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public int? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? StoredPath { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}