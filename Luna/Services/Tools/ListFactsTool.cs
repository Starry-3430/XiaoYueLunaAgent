// 工具：列出已记录的长期事实（可按关键词过滤）。
using System.Text;
using System.Text.Json;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>列出长期事实，可选关键词过滤。归“长期事实”开关（long_term_facts）。</summary>
public class ListFactsTool : ITool
{
    private const int MaxResults = 30;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"keyword":{"type":"string","description":"可选，按关键词过滤 key 或 value"}},"required":[]}""");

    private readonly FactRepository _repo;
    private readonly ILogger<ListFactsTool> _logger;

    public string Name => "list_facts";
    public string DisplayName => "列出事实";
    public string Description => "列出已记录的长期事实，可按关键词过滤。用于查看当前记住了关于用户的哪些信息。";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "long_term_facts";

    public ListFactsTool(FactRepository repo, ILogger<ListFactsTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var keyword = GetString(args, "keyword");

        try
        {
            var facts = string.IsNullOrWhiteSpace(keyword)
                ? await _repo.GetAllAsync()
                : await _repo.SearchAsync(keyword.Trim());

            if (facts.Count == 0)
                return ToolResult.Ok(string.IsNullOrWhiteSpace(keyword)
                    ? "还没有记住任何事实。"
                    : $"没有找到与「{keyword.Trim()}」相关的事实。");

            var sb = new StringBuilder();
            foreach (var fact in facts.Take(MaxResults))
                sb.Append("· ").Append(fact.Key).Append(" = ").AppendLine(fact.Value);

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出事实失败：{Keyword}", keyword);
            return ToolResult.Error($"列出事实失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
