using System.Text.Json.Serialization;

namespace Luna.Models;

public class AiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";
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