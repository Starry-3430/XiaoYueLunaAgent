using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>列出待办事项。归属“待办事项”开关（todo）。</summary>
public class ListTasksTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"status":{"type":"string","description":"筛选状态：pending / done / cancelled / all，默认 pending"}}}""");

    private readonly TaskRepository _repo;
    private readonly ILogger<ListTasksTool> _logger;

    public string Name => "list_tasks";
    public string DisplayName => "列出待办";
    public string Description => "列出待办事项，可按状态筛选（pending/done/cancelled/all）";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "todo";

    public ListTasksTool(TaskRepository repo, ILogger<ListTasksTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var status = GetString(args, "status");
        if (string.IsNullOrWhiteSpace(status))
            status = "pending";

        try
        {
            var tasks = await _repo.ListAsync(status);
            if (tasks.Count == 0)
                return ToolResult.Ok($"没有 {StatusLabel(status)} 的待办。");

            var sb = new StringBuilder();
            sb.AppendLine($"{StatusLabel(status)}的待办（{tasks.Count}）：");
            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                sb.AppendLine($"{i + 1}. {t.Title}");
                sb.AppendLine($"   ID: {t.Id}  状态: {StatusLabel(t.Status)}");
                if (t.DueAtUtc is not null)
                    sb.AppendLine($"   截止: {ToolTime.ToLocalString(t.DueAtUtc)}");
                if (t.RemindAtUtc is not null)
                    sb.AppendLine($"   提醒: {ToolTime.ToLocalString(t.RemindAtUtc)}");
                if (!string.IsNullOrWhiteSpace(t.Notes))
                    sb.AppendLine($"   备注: {t.Notes}");
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出待办失败");
            return ToolResult.Error($"列出待办失败：{ex.Message}");
        }
    }

    private static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "done" => "已完成",
        "cancelled" => "已取消",
        "all" => "全部",
        _ => "未完成",
    };

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
