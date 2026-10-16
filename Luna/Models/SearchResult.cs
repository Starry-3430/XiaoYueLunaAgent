namespace Luna.Models;

public class SearchResult
{
    public string SessionId { get; init; } = string.Empty;
    public string SessionTitle { get; init; } = string.Empty;
    public long MessageId { get; init; }
    public string Role { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
}
