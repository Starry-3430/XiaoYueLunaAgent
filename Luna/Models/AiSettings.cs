using System.Text.Json.Serialization;

namespace Luna.Models;

public class AiSettings
{
    public const string DefaultProvider = "deepseek";
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";
    public const string DefaultModel = "deepseek-chat";
    public const int DefaultMaxTokens = 4096;
    public const double DefaultTemperature = 1.0;
    public const string DefaultNickname = "Luna";
    public const string DefaultUserName = "主人";

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
    public string SystemPrompt { get; set; } = DefaultPrompts.SystemPrompt;
    public string Nickname { get; set; } = DefaultNickname;
    public string UserName { get; set; } = DefaultUserName;

    [JsonIgnore]
    public string TavilyApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 Tavily API Key（Base64），持久化字段。</summary>
    public string TavilyApiKeyProtected { get; set; } = string.Empty;
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