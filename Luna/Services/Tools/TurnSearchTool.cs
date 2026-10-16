// 工具：在历史对话轮次摘要中按关键词做检索，返回命中的轮次摘要与日期。
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 在历史对话轮次摘要（TurnSummaries.SummaryJson）中按关键词检索，返回命中的轮次摘要与日期。
/// 归“记忆”开关（turn_search）。只读、无副作用。
/// 最简实现：直接 LIKE 扫 SummaryJson，暂不建 FTS 索引。
/// </summary>
public class TurnSearchTool : ITool
{
    /// <summary>摘要无法解析为 JSON 时，回退返回原文的最大字数。</summary>
    private const int MaxRawLength = 200;

    /// <summary>展示的会话 ID 前缀长度（完整 GUID 对模型无意义）。</summary>
    private const int SessionIdDisplayLength = 6;

    private const int DefaultLimit = 5;
    private const int HardMaxLimit = 20;

    private const string SchemaTemplate =
        """{"type":"object","properties":{"keyword":{"type":"string","description":"$keyword$"},"limit":{"type":"integer","description":"$limit$"}},"required":["keyword"]}""";

    private readonly DatabaseInitializer _db;
    private readonly ILogger<TurnSearchTool> _logger;

    public string Name => "turn_search";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$keyword$", Param("Tool_TurnSearch_Param_Keyword"))
        .Replace("$limit$", Param("Tool_TurnSearch_Param_Limit")));
    public ToolRiskLevel Risk => ToolRiskLevel.None;

    public TurnSearchTool(DatabaseInitializer db, ILogger<TurnSearchTool> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var keyword = GetString(args, "keyword");
        if (string.IsNullOrWhiteSpace(keyword))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "keyword"));

        var limit = DefaultLimit;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n))
            limit = Math.Clamp(n, 1, HardMaxLimit);

        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            var rows = (await conn.QueryAsync<TurnRow>(
                @"SELECT TurnId, SessionId, LogicalDate, SummaryJson
                  FROM TurnSummaries
                  WHERE SummaryJson LIKE '%' || @Keyword || '%'
                  ORDER BY LogicalDate DESC
                  LIMIT @Limit",
                new { Keyword = keyword, Limit = limit })).ToList();

            if (rows.Count == 0)
                return ToolResult.Ok(Text("Tool_TurnSearch_Msg_None", keyword));

            var sb = new StringBuilder();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                sb.Append('[').Append(rows[i].LogicalDate).Append(Text("Tool_TurnSearch_Msg_SessionPrefix"))
                  .AppendLine(ShortId(rows[i].SessionId));
                sb.AppendLine(BuildBody(rows[i].SummaryJson));
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检索轮次摘要失败：{Keyword}", keyword);
            return ToolResult.Error(Text("Tool_TurnSearch_Msg_Failed", ex.Message));
        }
    }

    private static string ShortId(string id)
        => string.IsNullOrEmpty(id)
            ? "?"
            : id.Length <= SessionIdDisplayLength ? id : id[..SessionIdDisplayLength];

    /// <summary>把 SummaryJson 反序列化拼成文本；JSON 非法时降级为原文截断。</summary>
    private static string BuildBody(string summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
            return Text("Tool_TurnSearch_Msg_NoSummary");

        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Truncate(summaryJson.Trim());

            var sb = new StringBuilder();

            foreach (var point in ReadStringArray(root, "keyPoints"))
                sb.Append("· ").AppendLine(point);

            var files = ReadFileNames(root).Distinct().ToList();
            if (files.Count > 0)
                sb.Append(Text("Tool_TurnSearch_Msg_Files")).AppendLine(string.Join(Text("Tool_Msg_ListSeparator"), files));

            var topics = ReadStringArray(root, "topics").Distinct().ToList();
            if (topics.Count > 0)
                sb.Append(Text("Tool_TurnSearch_Msg_Topics")).AppendLine(string.Join(Text("Tool_Msg_ListSeparator"), topics));

            var text = sb.ToString().TrimEnd();
            return text.Length > 0 ? text : Truncate(summaryJson.Trim());
        }
        catch (JsonException)
        {
            // AI 生成的 JSON 不保证合法，降级返回原文截断。
            return Truncate(summaryJson.Trim());
        }
    }

    private static IEnumerable<string> ReadStringArray(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    private static IEnumerable<string> ReadFileNames(JsonElement root)
    {
        if (!TryGetProperty(root, "fileRefs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetProperty(item, "name", out var n))
                continue;
            var name = n.GetString()?.Trim();
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string Truncate(string text)
        => text.Length <= MaxRawLength ? text : text[..MaxRawLength] + "…";

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private sealed class TurnRow
    {
        public string TurnId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string LogicalDate { get; set; } = string.Empty;
        public string SummaryJson { get; set; } = string.Empty;
    }
}
