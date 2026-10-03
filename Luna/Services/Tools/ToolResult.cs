namespace Luna.Services.Tools;

/// <summary>工具执行结果：内容会作为 tool 消息回传给模型。</summary>
public sealed class ToolResult
{
    public string Content { get; init; } = string.Empty;

    /// <summary>是否为错误结果（用于 UI 标记为失败）。</summary>
    public bool IsError { get; init; }

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string content) => new() { Content = content, IsError = true };
}
