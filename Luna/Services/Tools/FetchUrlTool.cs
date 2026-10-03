using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>抓取指定 URL 的网页并提取正文文本，供搜索后阅读使用。无副作用。</summary>
public class FetchUrlTool : ITool
{
    private const int MaxChars = 5000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"url":{"type":"string","description":"要读取的网页完整 URL（需以 http:// 或 https:// 开头）"}},"required":["url"]}""");

    private readonly HttpClient _httpClient;
    private readonly ILogger<FetchUrlTool> _logger;

    public string Name => "fetch_url";
    public string DisplayName => "网页阅读";
    public string Description => "抓取指定 URL 的网页内容并提取正文文本，用于阅读搜索到的网页详情";
    public JsonElement ParametersSchema => Schema;

    public FetchUrlTool(HttpClient httpClient, ILogger<FetchUrlTool> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var url = ExtractUrl(args);
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Error("缺少参数 url。");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ToolResult.Error($"无效的 URL：{url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Luna/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Error($"抓取失败：HTTP {(int)response.StatusCode}");

            var html = await response.Content.ReadAsStringAsync(ct);
            var text = ExtractText(html);

            if (string.IsNullOrWhiteSpace(text))
                return ToolResult.Error($"网页 {uri} 未提取到正文内容。");

            _logger.LogInformation("网页抓取成功: {Url}, 正文长度: {Length}", uri, text.Length);
            return ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("抓取超时或被取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网页抓取失败: {Url}", url);
            return ToolResult.Error($"抓取失败：{ex.Message}");
        }
    }

    private static string ExtractUrl(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("url", out var u))
            return u.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static readonly Regex TitleRegex =
        new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DropRegex =
        new(@"<(script|style|noscript|svg|head|nav|footer)[^>]*>.*?</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockRegex =
        new(@"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|table|ul|ol|blockquote)[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"[ \t\u00A0]{2,}", RegexOptions.Compiled);
    private static readonly Regex NewlinesRegex = new(@"\n{3,}", RegexOptions.Compiled);

    private static string ExtractText(string html)
    {
        var title = TitleRegex.Match(html).Groups[1].Value;

        var body = DropRegex.Replace(html, " ");
        body = BlockRegex.Replace(body, "\n");
        body = TagRegex.Replace(body, string.Empty);
        body = WebUtility.HtmlDecode(body);

        body = body.Replace("\r\n", "\n").Replace('\r', '\n');
        body = SpacesRegex.Replace(body, " ");
        body = NewlinesRegex.Replace(body, "\n\n");
        body = body.Trim();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("标题：").Append(WebUtility.HtmlDecode(title).Trim()).Append("\n\n");
        sb.Append(body);

        var result = sb.ToString().Trim();
        if (result.Length > MaxChars)
            result = result[..MaxChars] + "\n…（内容过长已截断）";
        return result;
    }
}
