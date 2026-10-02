using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class WebSearchTool
{
    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<WebSearchTool> _logger;

    private const string TavilyApiUrl = "https://api.tavily.com/search";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public WebSearchTool(HttpClient httpClient, AiSettings settings, ILogger<WebSearchTool> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_settings.TavilyApiKey);

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tavily 搜索失败: {Query}", query);
            throw;
        }
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