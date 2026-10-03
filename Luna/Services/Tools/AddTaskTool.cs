using System.Text;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>创建待办事项（可选截止/提醒时间）。归属“待办事项”开关（todo）。</summary>
public class AddTaskTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"title":{"type":"string","description":"待办标题"},"dueAt":{"type":"string","description":"截止时间，ISO 8601 或 yyyy-MM-dd HH:mm（本地时间），可空"},"remindAt":{"type":"string","description":"提醒时间，同上，可空"},"notes":{"type":"string","description":"备注，可空"}},"required":["title"]}""");

    private readonly TaskRepository _repo;
    private readonly ILogger<AddTaskTool> _logger;

    public string Name => "add_task";
    public string DisplayName => "添加待办";
    public string Description => "创建一个待办事项，可选截止时间与提醒时间（时间用本地时间）";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "todo";

    public AddTaskTool(TaskRepository repo, ILogger<AddTaskTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var title = GetString(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return ToolResult.Error("缺少参数 title。");

        var notes = GetString(args, "notes");
        var dueRaw = GetString(args, "dueAt");
        var remindRaw = GetString(args, "remindAt");

        DateTime? dueUtc = null;
        if (!string.IsNullOrWhiteSpace(dueRaw))
        {
            if (!ToolTime.TryParseUtc(dueRaw, out var d))
                return ToolResult.Error($"无法解析截止时间：{dueRaw}");
            dueUtc = d;
        }

        DateTime? remindUtc = null;
        if (!string.IsNullOrWhiteSpace(remindRaw))
        {
            if (!ToolTime.TryParseUtc(remindRaw, out var r))
                return ToolResult.Error($"无法解析提醒时间：{remindRaw}");
            remindUtc = r;
        }

        var task = new TaskItem
        {
            Title = title.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            DueAtUtc = dueUtc,
            RemindAtUtc = remindUtc,
        };

        try
        {
            await _repo.AddAsync(task);
            _logger.LogInformation("已创建待办：{Id} {Title}", task.Id, task.Title);

            var sb = new StringBuilder();
            sb.Append($"已创建待办：「{task.Title}」");
            sb.Append($"\nID：{task.Id}");
            if (task.DueAtUtc is not null)
                sb.Append($"\n截止：{ToolTime.ToLocalString(task.DueAtUtc)}");
            if (task.RemindAtUtc is not null)
                sb.Append($"\n提醒：{ToolTime.ToLocalString(task.RemindAtUtc)}");
            sb.Append("\n（时间均为本地时间）");
            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建待办失败");
            return ToolResult.Error($"创建待办失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
