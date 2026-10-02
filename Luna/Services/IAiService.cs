using Luna.Models;

namespace Luna.Services;

public abstract record StreamEvent;
public record ReasoningDelta(string Text) : StreamEvent;
public record ContentDelta(string Text) : StreamEvent;
public record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment) : StreamEvent;
public record ToolCallCompleted(int Index, string Id, string Name, string ArgumentsJson) : StreamEvent;
public record StreamDone : StreamEvent;

public interface IAiService
{
    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}