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
    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<WebSearchTool> _logger;

    private const string TavilyApiUrl = "https://api.tavily.com/search";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Name => "web_search";
    public string DisplayName => "网页搜索";
    public string Description => "在互联网上搜索信息，返回相关网页的标题、URL 和内容摘要";
    public string ParametersSchema => """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词或问题"}},"required":["query"]}""";

    public WebSearchTool(HttpClient httpClient, AiSettings settings, ILogger<WebSearchTool> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.TavilyApiKey))
            return "未配置 Tavily API Key，请在“工具”页面的“网页搜索”下方填写后再试。";

        var query = argumentsJson;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q))
                query = q.GetString() ?? query;
        }
        catch { }

        try
        {
            var results = await SearchAsync(query, 5, ct);
            return FormatResults(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tavily 搜索失败: {Query}", query);
            return $"搜索失败: {ex.Message}";
        }
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