using Luna.Models;

namespace Luna.Services;

public interface IAiService
{
    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}