namespace Luna.Services;

/// <summary>单个提供商的预设信息。</summary>
public sealed record ProviderPreset(string Id, string Name, string BaseUrl, string[] Models);

/// <summary>
/// 常用 OpenAI 兼容提供商的预设目录，用于在设置界面自动填充 Base URL 与模型列表。
/// </summary>
public static class AiProviderCatalog
{
    public static readonly IReadOnlyList<ProviderPreset> Presets =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", ["gpt-4o", "gpt-4o-mini", "o3-mini", "gpt-4.1", "gpt-4.1-mini"]),
        new("moonshot", "Moonshot", "https://api.moonshot.cn/v1", ["moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k", "kimi-k2-0711-preview"]),
        new("zhipu", "智谱", "https://open.bigmodel.cn/api/paas/v4", ["glm-4-plus", "glm-4-air", "glm-4-flash"]),
        new("qwen", "通义", "https://dashscope.aliyuncs.com/compatible-mode/v1", ["qwen-max", "qwen-plus", "qwen-turbo"]),
        new("custom", "自定义（OpenAI 兼容）", "", []),
    ];

    public static ProviderPreset? Find(string id) =>
        Presets.FirstOrDefault(p => p.Id == id);
}
