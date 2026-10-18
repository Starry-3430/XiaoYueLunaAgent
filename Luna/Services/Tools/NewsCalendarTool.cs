using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public sealed class NewsCalendarTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{"mode":{"type":"string","enum":["news","calendar"]},"limit":{"type":"integer","description":"新闻条数，默认 5，最大 10"}},"required":[]}""");
    private readonly HttpClient _http;
    private readonly ILogger<NewsCalendarTool> _logger;
    public NewsCalendarTool(HttpClient http, ILogger<NewsCalendarTool> logger) { _http = http; _logger = logger; }
    public string Name => "news_calendar";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var mode = GetString(args, "mode").ToLowerInvariant(); if (string.IsNullOrEmpty(mode)) mode = "news";
        if (mode == "calendar") return ToolResult.Ok(BuildCalendar());
        if (mode != "news") return ToolResult.Error("mode 必须是 news 或 calendar。");
        var limit = 5; if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n)) limit = Math.Clamp(n, 1, 10);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://feeds.bbci.co.uk/news/rss.xml");
            request.Headers.UserAgent.ParseAdd("Luna/1.0");
            using var response = await _http.SendAsync(request, timeoutCts.Token); if (!response.IsSuccessStatusCode) return ToolResult.Error($"新闻获取失败：HTTP {(int)response.StatusCode}");
            var xml = await response.Content.ReadAsStringAsync(timeoutCts.Token); var doc = XDocument.Parse(xml); var items = doc.Descendants("item").Take(limit).ToList();
            if (items.Count == 0) return ToolResult.Error("没有获取到新闻条目。");
            var lines = items.Select((item, index) => $"{index + 1}. {Clean(item.Element("title")?.Value)}\n   {Clean(item.Element("description")?.Value)}\n   {item.Element("link")?.Value}");
            return ToolResult.Ok("新闻摘要（BBC RSS）：\n" + string.Join("\n", lines));
        }
        catch (OperationCanceledException) { return ToolResult.Error("新闻获取被取消。"); }
        catch (Exception ex) { _logger.LogError(ex, "获取新闻失败"); return ToolResult.Error($"新闻获取失败：{ex.Message}"); }
    }
    private static string BuildCalendar()
    {
        var now = DateTimeOffset.Now; var first = new DateTime(now.Year, now.Month, 1); var days = DateTime.DaysInMonth(now.Year, now.Month);
        return $"当前日期：{now:yyyy-MM-dd dddd}\n当前时间：{now:HH:mm:ss zzz}\n本月：{now:yyyy年M月}，共 {days} 天，1 号是 {first:dddd}。\n本地时区：{TimeZoneInfo.Local.Id}";
    }
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : System.Net.WebUtility.HtmlDecode(value).Trim();
    private static string GetString(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;
}
