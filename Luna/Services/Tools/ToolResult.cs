namespace Luna.Services.Tools;

/// <summary>工具执行结果：内容会作为 tool 消息回传给模型。</summary>
public sealed class ToolResult
{
    /// <summary>图片路径标记前缀：随工具文本持久化，供后续请求识别并注入多模态图片。</summary>
    public const string ImagePathMarker = "[IMAGE_PATH]";

    public string Content { get; init; } = string.Empty;

    /// <summary>是否为错误结果（用于 UI 标记为失败）。</summary>
    public bool IsError { get; init; }

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string content) => new() { Content = content, IsError = true };

    /// <summary>成功结果，并在文本末尾附带可供模型识图的图片路径标记。</summary>
    public static ToolResult OkWithImage(string content, string imagePath) =>
        new() { Content = $"{content}\n{ImagePathMarker}{imagePath}" };

    /// <summary>从工具结果文本中提取最后一个图片路径标记。</summary>
    public static bool TryExtractImagePath(string? content, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(content)) return false;

        var index = content.LastIndexOf(ImagePathMarker, StringComparison.Ordinal);
        if (index < 0) return false;

        var start = index + ImagePathMarker.Length;
        var end = content.IndexOf('\n', start);
        var value = (end < 0 ? content[start..] : content[start..end]).Trim();
        if (value.Length == 0) return false;

        path = value;
        return true;
    }
}
