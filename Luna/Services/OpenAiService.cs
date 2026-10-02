using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Luna.Models;

namespace Luna.Services;

public class OpenAiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<OpenAiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OpenAiService(HttpClient httpClient, AiSettings settings, ILogger<OpenAiService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequest
        {
            Model = _settings.Model,
            Stream = false,
            Messages = messages.Select(m => new ChatCompletionMessage
            {
                Role = m.Role,
                Content = m.Content,
            }).ToList(),
        };

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";

        _logger.LogInformation("POST {Url} model={Model}", url, _settings.Model);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Response: {Body}", body);

        var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);

        return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public async IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequest
        {
            Model = _settings.Model,
            Stream = true,
            Messages = messages.Select(m => new ChatCompletionMessage
            {
                Role = m.Role,
                Content = m.Content,
            }).ToList(),
        };

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";

        _logger.LogInformation("POST stream {Url} model={Model}", url, _settings.Model);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

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
}