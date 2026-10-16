// 工具：列出待办事项（可按状态筛选）。
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>列出待办事项。归属“待办事项”开关（todo）。</summary>
public class ListTasksTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"status":{"type":"string","description":"$status$"}}}""";

    private readonly TaskRepository _repo;
    private readonly ILogger<ListTasksTool> _logger;

    public string Name => "list_tasks";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$status$", Param("Tool_ListTasks_Param_Status")));
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
                return ToolResult.Ok(Text("Tool_ListTasks_Msg_None", StatusLabel(status)));

            var sb = new StringBuilder();
            sb.AppendLine(Text("Tool_ListTasks_Msg_Header", StatusLabel(status), tasks.Count));
            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                sb.AppendLine($"{i + 1}. {t.Title}");
                sb.AppendLine(Text("Tool_ListTasks_Msg_Line", t.Id, StatusLabel(t.Status)));
                if (t.DueAtUtc is not null)
                    sb.AppendLine(Text("Tool_ListTasks_Msg_Due", ToolTime.ToLocalString(t.DueAtUtc)));
                if (t.RemindAtUtc is not null)
                    sb.AppendLine(Text("Tool_ListTasks_Msg_Remind", ToolTime.ToLocalString(t.RemindAtUtc)));
                if (!string.IsNullOrWhiteSpace(t.Notes))
                    sb.AppendLine(Text("Tool_ListTasks_Msg_Notes", t.Notes));
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出待办失败");
            return ToolResult.Error(Text("Tool_ListTasks_Msg_Failed", ex.Message));
        }
    }

    private static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "done" => Text("Tool_TaskStatus_Done"),
        "cancelled" => Text("Tool_TaskStatus_Cancelled"),
        "all" => Text("Tool_TaskStatus_All"),
        _ => Text("Tool_TaskStatus_Pending"),
    };

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
