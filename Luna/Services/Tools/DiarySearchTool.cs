// 工具：在历史日记中按关键词做全文检索，返回命中的日期与内容片段。
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 在历史日记（DiaryFts 全文索引）中按关键词检索，返回命中的日期 + 内容片段。
/// 归“记忆”开关（diary_search）。只读、无副作用。
/// </summary>
public class DiarySearchTool : ITool
{
    /// <summary>每篇日记返回时保留的最大字数，避免一次回传过多 token。</summary>
    private const int MaxExcerptLength = 200;

    /// <summary>trigram 分词器要求关键词至少 3 个字符，短于此值回退 LIKE。</summary>
    private const int MinimumTrigramLength = 3;

    private const int DefaultLimit = 5;
    private const int HardMaxLimit = 20;

    private const string SchemaTemplate =
        """{"type":"object","properties":{"keyword":{"type":"string","description":"$keyword$"},"limit":{"type":"integer","description":"$limit$"}},"required":["keyword"]}""";

    private readonly DatabaseInitializer _db;
    private readonly ILogger<DiarySearchTool> _logger;

    public string Name => "diary_search";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$keyword$", Param("Tool_DiarySearch_Param_Keyword"))
        .Replace("$limit$", Param("Tool_DiarySearch_Param_Limit")));
    public ToolRiskLevel Risk => ToolRiskLevel.None;

    public DiarySearchTool(DatabaseInitializer db, ILogger<DiarySearchTool> logger)
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
            var rows = await SearchAsync(keyword, limit);
            if (rows.Count == 0)
                return ToolResult.Ok(Text("Tool_DiarySearch_Msg_None", keyword));

            var sb = new StringBuilder();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                sb.Append(rows[i].LogicalDate).AppendLine("：");
                sb.AppendLine(Excerpt(rows[i].Content, keyword));
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检索日记失败：{Keyword}", keyword);
            return ToolResult.Error(Text("Tool_DiarySearch_Msg_Failed", ex.Message));
        }
    }

    private async Task<List<DiaryRow>> SearchAsync(string keyword, int limit)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);

        // FTS5 的 MATCH 必须作用于虚拟表本身，故先在子查询里检索再 JOIN 回实体表按 rank 排序。
        if (keyword.Length >= MinimumTrigramLength)
        {
            try
            {
                var rows = (await conn.QueryAsync<DiaryRow>(
                    @"SELECT d.LogicalDate AS LogicalDate, d.Content AS Content
                      FROM DiaryEntries d
                      JOIN (SELECT LogicalDate, rank FROM DiaryFts WHERE DiaryFts MATCH @Q) f
                          ON f.LogicalDate = d.LogicalDate
                      ORDER BY f.rank
                      LIMIT @Limit",
                    new { Q = BuildMatchQuery(keyword), Limit = limit })).ToList();
                if (rows.Count > 0)
                    return rows;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "日记 FTS 检索失败，回退 LIKE：{Keyword}", keyword);
            }
        }

        // 短词、无命中或 FTS 语法异常时回退 LIKE（按日期倒序）。
        var fallback = await conn.QueryAsync<DiaryRow>(
            @"SELECT LogicalDate, Content
              FROM DiaryEntries
              WHERE Content LIKE '%' || @Q || '%'
              ORDER BY LogicalDate DESC
              LIMIT @Limit",
            new { Q = keyword, Limit = limit });
        return fallback.ToList();
    }

    /// <summary>
    /// 把用户关键词包装成 FTS5 短语查询，避免 <c>" * ( ) : - ^</c> 等特殊字符引发语法错误。
    /// 短语内的双引号通过翻倍转义。
    /// </summary>
    private static string BuildMatchQuery(string keyword)
        => "\"" + keyword.Replace("\"", "\"\"") + "\"";

    /// <summary>截取片段：优先围绕关键词附近开窗，保留上下文，避免回传整篇。</summary>
    private static string Excerpt(string content, string keyword)
    {
        content = content.Trim();
        if (content.Length <= MaxExcerptLength)
            return content;

        var index = content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        var start = index > MaxExcerptLength / 3 ? index - MaxExcerptLength / 3 : 0;
        if (start + MaxExcerptLength > content.Length)
            start = content.Length - MaxExcerptLength;

        var slice = content.Substring(start, MaxExcerptLength);
        return (start > 0 ? "…" : "") + slice + (start + MaxExcerptLength < content.Length ? "…" : "");
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private sealed class DiaryRow
    {
        public string LogicalDate { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}
