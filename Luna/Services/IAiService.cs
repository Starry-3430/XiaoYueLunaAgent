using Luna.Models;

namespace Luna.Services;

public abstract record StreamEvent;
public record ReasoningDelta(string Text) : StreamEvent;
public record ContentDelta(string Text) : StreamEvent;
public record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment) : StreamEvent;
public record ToolCallCompleted(int Index, string Id, string Name, string ArgumentsJson) : StreamEvent;
public record StreamFinish(string Reason) : StreamEvent;
public record StreamDone : StreamEvent;

/// <summary>单次补全请求的可选参数；未设置时回退到用户配置。</summary>
public sealed record AiRequestOptions
{
    /// <summary>采样温度；null 使用设置中的值。</summary>
    public double? Temperature { get; init; }

    /// <summary>最大生成 token 数；null 使用设置中的值。</summary>
    public int? MaxTokens { get; init; }

    /// <summary>是否在请求中加入已启用的工具定义。</summary>
    public bool IncludeTools { get; init; } = true;

    /// <summary>是否加入系统提示词。</summary>
    public bool IncludeSystemPrompt { get; init; } = true;

    /// <summary>是否在系统提示词后追加用户目录与语言偏好（仅当使用设置内系统提示词时生效）。</summary>
    public bool IncludeUserEnvironment { get; init; } = true;

    /// <summary>覆盖设置中的系统提示词；null 时使用设置中的值。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>是否启用深度思考模型切换；默认跟随设置。</summary>
    public bool DeepThinking { get; init; } = true;

    /// <summary>response_format.type，例如 "json_object"；null 时不发送该参数。</summary>
    public string? ResponseFormat { get; init; }
}

public interface IAiService
{
    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    Task<string> ChatAsync(IEnumerable<ChatMessage> messages, AiRequestOptions options,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}