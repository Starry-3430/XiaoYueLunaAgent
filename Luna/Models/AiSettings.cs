using System.Text.Json.Serialization;

namespace Luna.Models;

public class AiSettings
{
    public const string DefaultProvider = "deepseek";
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";
    public const string DefaultModel = "deepseek-chat";
    public const int DefaultMaxTokens = 8192;

    /// <summary>旧版本的默认输出上限；加载配置时若仍为该值，自动迁移到新的默认值。</summary>
    public const int LegacyDefaultMaxTokens = 4096;
    public const double DefaultTemperature = 1.0;
    public const double DefaultTopP = 0.9;
    public const double DefaultFrequencyPenalty = 0.1;
    public const double DefaultPresencePenalty = 0.2;
    public const string DefaultNickname = "Luna";
    public const string DefaultUserName = "主人";
    public const string DefaultResponseLanguage = "auto";
    public const string DefaultFontFamily = "";
    public const string DefaultHotkey = "Ctrl + Alt + Y";
    public const string DefaultProxyType = "none"; // none / http / socks4 / socks5

    /// <summary>提供商标识（deepseek / openai / anthropic / moonshot / zhipu / qwen / ollama / custom）。</summary>
    public string Provider { get; set; } = DefaultProvider;

    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 API Key（Base64），持久化字段。</summary>
    public string ApiKeyProtected { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = DefaultModel;
    public int MaxTokens { get; set; } = DefaultMaxTokens;
    public double Temperature { get; set; } = DefaultTemperature;
    public double TopP { get; set; } = DefaultTopP;
    public double FrequencyPenalty { get; set; } = DefaultFrequencyPenalty;
    public double PresencePenalty { get; set; } = DefaultPresencePenalty;
    public bool DeepThinking { get; set; } = false;
    public string ResponseLanguage { get; set; } = DefaultResponseLanguage;
    public string SystemPrompt { get; set; } = DefaultPrompts.SystemPrompt;
    public string Nickname { get; set; } = DefaultNickname;
    public string UserName { get; set; } = DefaultUserName;

    // ===== 通用 =====
    public string FontFamily { get; set; } = DefaultFontFamily;
    public string Hotkey { get; set; } = DefaultHotkey;
    public string ProxyType { get; set; } = DefaultProxyType;
    public string ProxyServer { get; set; } = string.Empty;
    public int ProxyPort { get; set; }
    public string ProxyUsername { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProxyPassword { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的代理密码（Base64），持久化字段。</summary>
    public string ProxyPasswordProtected { get; set; } = string.Empty;

    [JsonIgnore]
    public string TavilyApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 Tavily API Key（Base64），持久化字段。</summary>
    public string TavilyApiKeyProtected { get; set; } = string.Empty;

    /// <summary>各工具的启用状态（工具 Id → 是否启用）。未记录时默认启用。</summary>
    public Dictionary<string, bool> ToolStates { get; set; } = new();
}

public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatCompletionMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

public class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

public class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = [];
}

public class Choice
{
    [JsonPropertyName("message")]
    public ChatCompletionMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public ChatCompletionMessage? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}