// 工具：联网搜索（Tavily API，含请求/响应/结果 DTO）。
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luna.Models;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

public class WebSearchTool : ITool
{
    private const string TavilyApiUrl = "https://api.tavily.com/search";

    private const string SchemaTemplate =
        """{"type":"object","properties":{"query":{"type":"string","description":"$query$"}},"required":["query"]}""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<WebSearchTool> _logger;

    public string Name => "web_search";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$query$", Param("Tool_WebSearch_Param_Query")));

    public WebSearchTool(HttpClient httpClient, AiSettings settings, ILogger<WebSearchTool> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.TavilyApiKey))
            return ToolResult.Error(Text("Tool_WebSearch_Msg_NoApiKey"));

        var query = ExtractQuery(args);
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "query"));

        try
        {
            var results = await SearchAsync(query, 5, ct);
            return ToolResult.Ok(FormatResults(results));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tavily 搜索失败: {Query}", query);
            return ToolResult.Error(Text("Tool_WebSearch_Msg_Failed", ex.Message));
        }
    }

    private static string ExtractQuery(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var q))
            return q.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var request = new TavilySearchRequest
        {
            ApiKey = _settings.TavilyApiKey,
            Query = query,
            MaxResults = maxResults,
        };

        using var response = await _httpClient.PostAsJsonAsync(TavilyApiUrl, request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<TavilySearchResponse>(body, JsonOptions);
        return result?.Results ?? [];
    }

    private static string FormatResults(List<WebSearchResult> results)
    {
        if (results.Count == 0) return Text("Tool_WebSearch_Msg_NoResults");

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. **{r.Title}**");
            sb.AppendLine($"   URL: {r.Url}");
            sb.AppendLine($"   {r.Content}");
            if (i < results.Count - 1) sb.AppendLine();
        }
        return sb.ToString();
    }
}

public class TavilySearchRequest
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 5;
}

public class TavilySearchResponse
{
    [JsonPropertyName("results")]
    public List<WebSearchResult> Results { get; set; } = [];
}

public class WebSearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
