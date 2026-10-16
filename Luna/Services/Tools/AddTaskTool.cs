// 工具：创建待办事项（可带截止与提醒时间）。
using System.Text;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>创建待办事项（可选截止/提醒时间）。归属“待办事项”开关（todo）。</summary>
public class AddTaskTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"title":{"type":"string","description":"$title$"},"dueAt":{"type":"string","description":"$dueAt$"},"remindAt":{"type":"string","description":"$remindAt$"},"notes":{"type":"string","description":"$notes$"}},"required":["title"]}""";

    private readonly TaskRepository _repo;
    private readonly ILogger<AddTaskTool> _logger;

    public string Name => "add_task";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$title$", Param("Tool_AddTask_Param_Title"))
        .Replace("$dueAt$", Param("Tool_AddTask_Param_DueAt"))
        .Replace("$remindAt$", Param("Tool_AddTask_Param_RemindAt"))
        .Replace("$notes$", Param("Tool_AddTask_Param_Notes")));
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
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "title"));

        var notes = GetString(args, "notes");
        var dueRaw = GetString(args, "dueAt");
        var remindRaw = GetString(args, "remindAt");

        DateTime? dueUtc = null;
        if (!string.IsNullOrWhiteSpace(dueRaw))
        {
            if (!ToolTime.TryParseUtc(dueRaw, out var d))
                return ToolResult.Error(Text("Tool_AddTask_Msg_BadDue", dueRaw));
            dueUtc = d;
        }

        DateTime? remindUtc = null;
        if (!string.IsNullOrWhiteSpace(remindRaw))
        {
            if (!ToolTime.TryParseUtc(remindRaw, out var r))
                return ToolResult.Error(Text("Tool_AddTask_Msg_BadRemind", remindRaw));
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
            sb.Append(Text("Tool_AddTask_Msg_Created", task.Title));
            sb.Append("\n").Append(Text("Tool_AddTask_Msg_Id", task.Id));
            if (task.DueAtUtc is not null)
                sb.Append("\n").Append(Text("Tool_AddTask_Msg_Due", ToolTime.ToLocalString(task.DueAtUtc)));
            if (task.RemindAtUtc is not null)
                sb.Append("\n").Append(Text("Tool_AddTask_Msg_Remind", ToolTime.ToLocalString(task.RemindAtUtc)));
            sb.Append("\n").Append(Text("Tool_AddTask_Msg_LocalNote"));
            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建待办失败");
            return ToolResult.Error(Text("Tool_AddTask_Msg_CreateFailed", ex.Message));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
