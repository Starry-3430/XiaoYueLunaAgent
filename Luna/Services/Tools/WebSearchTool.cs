using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class WebSearchTool : ITool
{
    private const string TavilyApiUrl = "https://api.tavily.com/search";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词或问题"}},"required":["query"]}""");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<WebSearchTool> _logger;

    public string Name => "web_search";
    public string DisplayName => "网页搜索";
    public string Description => "在互联网上搜索信息，返回相关网页的标题、URL 和内容摘要";
    public JsonElement ParametersSchema => Schema;

    public WebSearchTool(HttpClient httpClient, AiSettings settings, ILogger<WebSearchTool> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.TavilyApiKey))
            return ToolResult.Error("未配置 Tavily API Key，请在“工具”页面的“网页搜索”下方填写后再试。");

        var query = ExtractQuery(args);
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("缺少参数 query。");

        try
        {
            var results = await SearchAsync(query, 5, ct);
            return ToolResult.Ok(FormatResults(results));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tavily 搜索失败: {Query}", query);
            return ToolResult.Error($"搜索失败: {ex.Message}");
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
        if (results.Count == 0) return "未找到相关结果。";

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
