using System.Text.Json;

namespace Luna.Services.Tools;

/// <summary>
/// 把 JSON Schema 字符串转换为 <see cref="JsonElement"/>。
/// 结果经过 <see cref="JsonElement.Clone"/> 处理，可脱离原 <see cref="JsonDocument"/> 长期持有。
/// </summary>
public static class ToolSchema
{
    public static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>无参数工具的通用空 schema。</summary>
    public static JsonElement Empty { get; } =
        Parse("""{"type":"object","properties":{},"required":[]}""");
}
