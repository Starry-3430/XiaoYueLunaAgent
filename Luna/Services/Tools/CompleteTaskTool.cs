// 工具：把指定待办标记为已完成
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>标记待办为完成。归属“待办事项”开关（todo）。</summary>
public class CompleteTaskTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"taskId":{"type":"string","description":"$taskId$"}},"required":["taskId"]}""";

    private readonly TaskRepository _repo;
    private readonly ILogger<CompleteTaskTool> _logger;

    public string Name => "complete_task";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$taskId$", Param("Tool_CompleteTask_Param_TaskId")));
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "todo";

    public CompleteTaskTool(TaskRepository repo, ILogger<CompleteTaskTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var id = GetString(args, "taskId");
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "taskId"));

        try
        {
            var affected = await _repo.CompleteAsync(id);
            if (affected == 0)
                return ToolResult.Error(Text("Tool_CompleteTask_Msg_NotFound", id));

            _logger.LogInformation("已完成待办：{Id}", id);
            return ToolResult.Ok(Text("Tool_CompleteTask_Msg_Done", id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "完成待办失败");
            return ToolResult.Error(Text("Tool_CompleteTask_Msg_Failed", ex.Message));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
