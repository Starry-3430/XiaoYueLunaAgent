using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly SystemPromptBuilder _systemPromptBuilder;
    private readonly ILogger<OpenAiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>一次请求最多尝试的次数（含首次）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>重试等待的上限，避免服务端给出过长的 Retry-After 时卡住界面。</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>非流式请求的总超时（包含响应体读取）。</summary>
    private static readonly TimeSpan NonStreamingTimeout = TimeSpan.FromSeconds(120);

    /// <summary>流式请求等待响应头的超时；拿到响应头后不再限制总时长。</summary>
    private static readonly TimeSpan StreamHeaderTimeout = TimeSpan.FromSeconds(120);

    /// <summary>流式响应允许的最长“无新数据”间隔，持续有数据时不会触发。</summary>
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>附件注入 system 消息的识别标记（由 ChatGenerationService.BuildAttachmentContext 生成）。</summary>
    private const string AttachmentContextMarker = "用户上传了以下附件";

    /// <summary>附件原始路径行前缀。</summary>
    private const string AttachmentPathPrefix = "原始路径：";

    /// <summary>发给 API 时附件上下文最多保留的字符数，完整内容仍保留在数据库中。</summary>
    private const int MaxAttachmentContextChars = 3000;

    public OpenAiService(HttpClient httpClient, AiSettings settings,
        ToolRegistry toolRegistry, SystemPromptBuilder systemPromptBuilder,
        ILogger<OpenAiService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _toolRegistry = toolRegistry;
        _systemPromptBuilder = systemPromptBuilder;
        _logger = logger;
    }

    public Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        => ChatAsync(messages, new AiRequestOptions(), cancellationToken);

    public async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, AiRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        var requestBody = await BuildRequestBodyAsync(messages, stream: false, options, cancellationToken);

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        var requestModel = requestBody["model"]?.GetValue<string>() ?? _settings.Model;
        _logger.LogInformation("POST {Url} model={Model}", url, requestModel);

        using var response = await SendWithRetryAsync(
            () => CreateHttpRequest(url, requestBody.ToJsonString()),
            HttpCompletionOption.ResponseContentRead,
            NonStreamingTimeout,
            url,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Response: {Body}", body);

        var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
        => ChatStreamAsync(messages, new AiRequestOptions(), cancellationToken);

    public async IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        AiRequestOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = await BuildRequestBodyAsync(messages, stream: true, options, cancellationToken);

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        var requestModel = requestBody["model"]?.GetValue<string>() ?? _settings.Model;
        _logger.LogInformation("POST stream {Url} model={Model}", url, requestModel);

        // 只在这里重试“建立请求 + 拿到成功响应头”的阶段。
        // 一旦开始 yield 流式事件，就不能安全重试（否则会重复输出已发送的内容）。
        using var response = await SendWithRetryAsync(
            () => CreateHttpRequest(url, requestBody.ToJsonString()),
            HttpCompletionOption.ResponseHeadersRead,
            StreamHeaderTimeout,
            url,
            cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // 流式回答不限制总时长，只限制“多久没有新数据”。
        // 每读到一行（包括 SSE 心跳/空行）都会重置空闲计时器，所以持续输出时不会超时。
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamCts.CancelAfter(StreamIdleTimeout);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(streamCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("流式响应超过 {Seconds} 秒没有新数据，中止读取",
                    StreamIdleTimeout.TotalSeconds);
                throw new TimeoutException(
                    $"流式响应超过 {StreamIdleTimeout.TotalSeconds:0} 秒没有新数据。");
            }

            if (line is null) break;
            streamCts.CancelAfter(StreamIdleTimeout);

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

                var choice = choices[0];
                events = new List<StreamEvent>();

                // finish_reason 可能单独成块（此时没有 delta）：用于判断是否被输出上限截断
                if (choice.TryGetProperty("finish_reason", out var finishProp) &&
                    finishProp.ValueKind == JsonValueKind.String)
                {
                    var reason = finishProp.GetString();
                    if (!string.IsNullOrEmpty(reason))
                        events.Add(new StreamFinish(reason));
                }

                if (choice.TryGetProperty("delta", out var delta))
                {
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

    private HttpRequestMessage CreateHttpRequest(string url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>429、408 与服务端 5xx 适合重试；4xx 参数/鉴权错误重试没有意义。</summary>
    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    /// <summary>
    /// 建立连接并拿到成功响应头；对 429/5xx、网络异常和超时做指数退避重试。
    /// 注意：流式响应一旦开始 yield 就不再重试，避免重复输出已经展示给用户的内容。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        TimeSpan attemptTimeout,
        string url,
        CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(attemptTimeout);
                response = await _httpClient.SendAsync(request, completionOption, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 用户没取消，说明是本次尝试的超时。
                lastError = new TimeoutException(
                    $"请求 {url} 超时（{attemptTimeout.TotalSeconds:0} 秒，第 {attempt}/{MaxAttempts} 次）");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }

            if (response is null)
            {
                if (attempt >= MaxAttempts)
                    break;

                var retryDelay = GetRetryDelay(attempt, null);
                _logger.LogWarning(lastError, "请求 {Url} 失败（第 {Attempt}/{Max} 次），{Delay} 后重试",
                    url, attempt, MaxAttempts, retryDelay);
                await Task.Delay(retryDelay, ct);
                continue;
            }

            if (response.IsSuccessStatusCode)
                return response;

            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter;
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                response.Dispose();
            }

            if (!IsRetryableStatus(status) || attempt >= MaxAttempts)
            {
                _logger.LogError("HTTP {Status} 错误：{Body}", (int)status, TruncateForLog(errorBody));
                throw new HttpRequestException($"HTTP {(int)status} @ {url}：{errorBody}", null, status);
            }

            var delay = GetRetryDelay(attempt, retryAfter);
            _logger.LogWarning("HTTP {Status}，第 {Attempt}/{Max} 次请求失败，{Delay} 后重试。响应：{Body}",
                (int)status, attempt, MaxAttempts, delay, TruncateForLog(errorBody));
            await Task.Delay(delay, ct);
        }

        throw new HttpRequestException(
            $"请求 {url} 失败（已尝试 {MaxAttempts} 次）：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>优先使用服务端的 Retry-After，否则按 1s / 2s / 4s 指数退避，并加入少量抖动。</summary>
    private static TimeSpan GetRetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta
                    ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);

        if (delay <= TimeSpan.Zero)
            delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

        if (delay > MaxRetryDelay)
            delay = MaxRetryDelay;

        return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    }

    private static string TruncateForLog(string text, int max = 500)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 对附件注入的 system 内容做 API 侧降级：保留开头 3000 字符，
    /// 并在末尾列出所有附件的原始路径，供模型按需调用 read_file。
    /// 数据库中的完整内容不变。
    /// </summary>
    private static string TruncateAttachmentContext(string content)
    {
        if (content.Length <= MaxAttachmentContextChars)
            return content;

        var paths = ExtractAttachmentPaths(content);
        var sb = new StringBuilder(content[..MaxAttachmentContextChars]);

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"[附件内容过长，已折叠其余 {content.Length - MaxAttachmentContextChars} 个字符。]");

        if (paths.Count > 0)
        {
            sb.AppendLine("【附件原始路径】");
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {path}");

            sb.AppendLine("如需更多细节，可调用 read_file 读取上述原始路径；" +
                          "PDF、Word、Excel、PPT 等文档会自动转换为 Markdown；" +
                          "大文件可用 keyword 或 start_line/end_line 分段读取。");
        }
        else
        {
            sb.AppendLine("如需更多细节，请依靠用户的后续提问。");
        }

        return sb.ToString();
    }

    /// <summary>从附件 system 内容中提取所有“原始路径：...”行。</summary>
    private static List<string> ExtractAttachmentPaths(string content)
    {
        var paths = new List<string>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(AttachmentPathPrefix, StringComparison.Ordinal))
                continue;

            var path = line[AttachmentPathPrefix.Length..].Trim();
            if (path.Length > 0)
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>开启深度思考时选择合适的模型/推理参数。</summary>
    private string ResolveModel(JsonObject root, string currentModel)
    {
        var model = currentModel;
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

    /// <summary>
    /// o1/o3/o4 等推理模型不支持 temperature/top_p/penalty，且使用 max_completion_tokens。
    /// 命中时从请求体中移除不兼容参数。
    /// </summary>
    private static void ApplyReasoningModelParameterCompat(JsonObject root, string model)
    {
        var lower = model.ToLowerInvariant();
        if (!(lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4")))
            return;

        root.Remove("temperature");
        root.Remove("top_p");
        root.Remove("frequency_penalty");
        root.Remove("presence_penalty");

        if (root.TryGetPropertyValue("max_tokens", out var maxTokens) && maxTokens is not null)
        {
            root.Remove("max_tokens");
            root["max_completion_tokens"] = maxTokens.DeepClone();
        }
    }

    private async Task<JsonObject> BuildRequestBodyAsync(IEnumerable<ChatMessage> messages, bool stream,
        AiRequestOptions options, CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["stream"] = stream,
            ["max_tokens"] = options.MaxTokens ?? _settings.MaxTokens,
            ["temperature"] = options.Temperature ?? _settings.Temperature,
            ["top_p"] = _settings.TopP,
            ["frequency_penalty"] = _settings.FrequencyPenalty,
            ["presence_penalty"] = _settings.PresencePenalty,
        };
        var targetModel = options.IsBackgroundTask
            ? _settings.EffectiveBackgroundModel
            : _settings.Model;

        root["model"] = options.DeepThinking ? ResolveModel(root, targetModel) : targetModel;

        ApplyReasoningModelParameterCompat(root, root["model"]?.GetValue<string>() ?? targetModel);

        if (!string.IsNullOrEmpty(options.ResponseFormat))
        {
            root["response_format"] = new JsonObject
            {
                ["type"] = options.ResponseFormat,
            };
        }

        var messagesArray = new JsonArray();

        // 用当前用户输入做长期事实的相关性检索（工具循环期间最后一条 user 仍是本轮输入）。
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var queryText = messageList.LastOrDefault(m => m.Role == "user")?.Content;

        // 渐进披露：核心工具 + 会话已解锁组 + 根据当前输入自动命中的组。
        var activeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.ActiveToolGroups is not null)
        {
            foreach (var group in options.ActiveToolGroups)
                activeGroups.Add(group);
        }
        foreach (var group in ToolGroups.MatchGroups(queryText))
            activeGroups.Add(group);

        var systemPrompt = await _systemPromptBuilder.BuildAsync(options, queryText, ct);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messagesArray.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            });
        }

        // 记录上一条 assistant tool_calls 的 id 集合：只有能对应上的 tool 结果才会发送，
        // 避免出现 "Messages with role 'tool' must be a response to a preceding message with 'tool_calls'"。
        HashSet<string>? pendingToolCallIds = null;

        // 最近一次工具返回的截图路径，以及当前请求末尾是否为工具消息。
        // 工具刚结束时把图片追加成一条运行期 user 消息，让模型真正“看”到画面。
        string? latestScreenshotPath = null;
        var lastMessageIsTool = false;

        foreach (var msg in messageList)
        {
            if (msg.Role == "assistant")
            {
                if (string.IsNullOrEmpty(msg.Content) &&
                    string.IsNullOrEmpty(msg.Reasoning) &&
                    msg.ToolCalls.Count == 0)
                {
                    pendingToolCallIds = null;
                    continue;
                }

                var obj = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = msg.Content,
                };

                if (!string.IsNullOrEmpty(msg.Reasoning))
                    obj["reasoning_content"] = msg.Reasoning;

                if (msg.ToolCalls.Count > 0)
                {
                    var tcArray = new JsonArray();
                    var ids = new HashSet<string>(StringComparer.Ordinal);
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
                        if (!string.IsNullOrEmpty(tc.ToolCallId))
                            ids.Add(tc.ToolCallId);
                    }
                    obj["tool_calls"] = tcArray;
                    pendingToolCallIds = ids;
                }
                else
                {
                    pendingToolCallIds = null;
                }

                messagesArray.Add(obj);
                lastMessageIsTool = false;
                latestScreenshotPath = null;
            }
            else if (msg.Role == "tool")
            {
                // 无对应 tool_calls 的孤儿工具结果直接丢弃，防止 API 400
                if (pendingToolCallIds is null ||
                    string.IsNullOrEmpty(msg.ToolCallId) ||
                    !pendingToolCallIds.Contains(msg.ToolCallId))
                    continue;

                var obj = new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = msg.Content,
                    ["tool_call_id"] = msg.ToolCallId,
                };
                messagesArray.Add(obj);
                lastMessageIsTool = true;

                if (ToolResult.TryExtractImagePath(msg.Content, out var screenshotPath))
                    latestScreenshotPath = screenshotPath;
            }
            else
            {
                // user / system 等：重置工具上下文
                pendingToolCallIds = null;
                lastMessageIsTool = false;
                latestScreenshotPath = null;

                var obj = new JsonObject { ["role"] = msg.Role };

                if (msg.HasImages)
                {
                    // 多模态：文本 + 图片（data URL），交由模型识图
                    var parts = new JsonArray();
                    if (!string.IsNullOrEmpty(msg.Content))
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = msg.Content });
                    foreach (var url in msg.Images)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = url },
                        });
                    }
                    obj["content"] = parts;
                }
                else if (msg.Role == "system" &&
                         msg.Content.Contains(AttachmentContextMarker, StringComparison.Ordinal))
                {
                    // 附件正文在数据库里保持完整，只在发给 API 时降级，
                    // 避免长文档每一轮都重复占满上下文。
                    obj["content"] = TruncateAttachmentContext(msg.Content);
                }
                else
                {
                    obj["content"] = msg.Content;
                }

                messagesArray.Add(obj);
            }
        }
        // 工具轮刚结束（末尾是 tool 消息）时，把最近一张截图作为 user 图片追加到请求末尾。
        // 仅存在于本次请求，不写入历史，也不会在 UI 里多出一条用户消息。
        if (lastMessageIsTool && !string.IsNullOrEmpty(latestScreenshotPath))
            await AppendScreenshotMessageAsync(messagesArray, latestScreenshotPath, ct);

        root["messages"] = messagesArray;

        var registeredTools = options.IncludeTools
            ? _toolRegistry.GetEnabledTools()
                .Where(tool => ToolGroups.ShouldInclude(tool.Name, activeGroups))
                .ToList()
            : new List<ITool>();
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
                        ["parameters"] = JsonNode.Parse(tool.ParametersSchema.GetRawText()),
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

    /// <summary>
    /// 把工具生成的截图以 data URL 形式追加为一条 user 消息。
    /// 标准 OpenAI 协议不允许 tool 消息携带图片，因此只能在工具轮结束后补一条 user 消息。
    /// </summary>
    private async Task AppendScreenshotMessageAsync(JsonArray messagesArray, string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return;

            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > AttachmentHelper.MaxImageBytes)
            {
                _logger.LogWarning("截图不可用或过大，跳过多模态注入: {Path} ({Size} bytes)", path, info.Length);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path, ct);
            var dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

            messagesArray.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = ToolLocalization.Text("Tool_ReadScreen_Msg_ImageInjected"),
                    },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = dataUrl },
                    },
                },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注入截图到多模态请求失败: {Path}", path);
        }
    }
}
