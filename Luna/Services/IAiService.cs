using Luna.Models;

namespace Luna.Services;

public readonly record struct StreamChunk(string? Content, string? ReasoningContent);

public interface IAiService
{
    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamChunk> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}