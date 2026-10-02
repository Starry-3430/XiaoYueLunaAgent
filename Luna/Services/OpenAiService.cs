using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services.Tools;

namespace Luna.Services;

public class OpenAiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<OpenAiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OpenAiService(HttpClient httpClient, AiSettings settings,
        ToolRegistry toolRegistry, ILogger<OpenAiService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, stream: false);

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.LogInformation("POST {Url} model={Model}", url, _settings.Model);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"请求 {url} 失败：{ex.InnerException?.Message ?? ex.Message}", ex, ex.StatusCode);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("HTTP {Status} 错误：{Body}", (int)response.StatusCode, errorBody);
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} @ {url}：{errorBody}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Response: {Body}", body);

            var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
            return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        }
    }

    public async IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, stream: true);

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.LogInformation("POST stream {Url} model={Model}", url, _settings.Model);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"请求 {url} 失败：{ex.InnerException?.Message ?? ex.Message}", ex, ex.StatusCode);
        }

        using var responseScope = response;
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("HTTP {Status} 错误：{Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} @ {url}：{errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (!line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
            {
                yield return new StreamDone();
                yield break;
            }

            if (string.IsNullOrWhiteSpace(data))
                continue;

            List<StreamEvent>? events = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;

                var delta = choices[0].GetProperty("delta");
                events = new List<StreamEvent>();

                if (delta.TryGetProperty("reasoning_content", out var r))
                {
                    var text = r.GetString();
                    if (!string.IsNullOrEmpty(text))
                        events.Add(new ReasoningDelta(text));
                }

                if (delta.TryGetProperty("content", out var c))
                {
                    var text = c.GetString();
                    if (!string.IsNullOrEmpty(text))
                        events.Add(new ContentDelta(text));
                }

                if (delta.TryGetProperty("tool_calls", out var t))
                {
                    foreach (var call in t.EnumerateArray())
                    {
                        var index = call.TryGetProperty("index", out var idxProp)
                            ? idxProp.GetInt32()
                            : 0;

                        string? id = null;
                        if (call.TryGetProperty("id", out var idProp))
                            id = idProp.GetString();

                        string? name = null;
                        if (call.TryGetProperty("function", out var funcProp) &&
                            funcProp.TryGetProperty("name", out var nameProp))
                            name = nameProp.GetString();

                        string? argsFragment = null;
                        if (call.TryGetProperty("function", out var funcProp2) &&
                            funcProp2.TryGetProperty("arguments", out var argsProp))
                            argsFragment = argsProp.GetString();

                        events.Add(new ToolCallDelta(index, id, name, argsFragment));
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "SSE 解析跳过: {Line}", line);
            }

            if (events is not null)
            {
                foreach (var evt in events)
                    yield return evt;
            }
        }
    }

    private string BuildSystemPrompt()
    {
        var prompt = _settings.SystemPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
            return string.Empty;

        prompt = prompt
            .Replace("{nickname}", _settings.Nickname)
            .Replace("{Nickname}", _settings.Nickname)
            .Replace("{user}", _settings.UserName)
            .Replace("{User}", _settings.UserName);

        prompt += _settings.ResponseLanguage switch
        {
            "zh" => "\n\n请始终使用简体中文回复。",
            "en" => "\n\nAlways reply in English.",
            _ => string.Empty,
        };

        return prompt;
    }

    /// <summary>开启深度思考时选择合适的模型/推理参数。</summary>
    private string ResolveModel(JsonObject root)
    {
        var model = _settings.Model;
        if (!_settings.DeepThinking)
            return model;

        var lower = model.ToLowerInvariant();

        // 本身就是推理模型：无需额外参数
        if (lower.Contains("reasoner") || lower.Contains("-r1") ||
            lower.Contains("thinking") || lower.Contains("reasoning"))
            return model;

        // OpenAI o 系列 / GPT-5：通过 reasoning_effort 控制思考强度
        if (lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4") || lower.Contains("gpt-5"))
        {
            root["reasoning_effort"] = "high";
            return model;
        }

        // DeepSeek 非推理模型：切换到推理模型
        if (lower.StartsWith("deepseek"))
            return "deepseek-reasoner";

        // 其他 OpenAI 兼容模型：尝试开启 reasoning_effort
        root["reasoning_effort"] = "high";
        return model;
    }

    private JsonObject BuildRequestBody(IEnumerable<ChatMessage> messages, bool stream)
    {
        var root = new JsonObject
        {
            ["stream"] = stream,
            ["max_tokens"] = _settings.MaxTokens,
            ["temperature"] = _settings.Temperature,
            ["top_p"] = _settings.TopP,
            ["frequency_penalty"] = _settings.FrequencyPenalty,
            ["presence_penalty"] = _settings.PresencePenalty,
        };
        root["model"] = ResolveModel(root);

        var messagesArray = new JsonArray();

        var systemPrompt = BuildSystemPrompt();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messagesArray.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            });
        }

        foreach (var msg in messages)
        {
            if (msg.Role == "assistant" &&
                string.IsNullOrEmpty(msg.Content) &&
                string.IsNullOrEmpty(msg.Reasoning) &&
                msg.ToolCalls.Count == 0)
                continue;

            var obj = new JsonObject
            {
                ["role"] = msg.Role,
            };

            if (msg.Role == "tool")
            {
                if (!string.IsNullOrEmpty(msg.ToolCallId))
                    obj["tool_call_id"] = msg.ToolCallId;
                obj["content"] = msg.Content;
            }
            else
            {
                obj["content"] = msg.Content;

                if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Reasoning))
                {
                    obj["reasoning_content"] = msg.Reasoning;
                }

                if (msg.Role == "assistant" && msg.ToolCalls.Count > 0)
                {
                    var tcArray = new JsonArray();
                    foreach (var tc in msg.ToolCalls)
                    {
                        tcArray.Add(new JsonObject
                        {
                            ["id"] = tc.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.ToolName,
                                ["arguments"] = tc.ArgumentsJson,
                            },
                        });
                    }
                    obj["tool_calls"] = tcArray;
                }
            }

            messagesArray.Add(obj);
        }
        root["messages"] = messagesArray;

        var registeredTools = _toolRegistry.GetEnabledTools().ToList();
        if (registeredTools.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in registeredTools)
            {
                toolsArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersSchema),
                    },
                });
            }
            root["tools"] = toolsArray;
            root["tool_choice"] = "auto";
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Request body (tools={ToolCount}): {Body}",
                registeredTools.Count, root.ToJsonString());
        }

        return root;
    }
}