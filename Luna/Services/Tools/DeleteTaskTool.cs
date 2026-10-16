// 工具：删除指定待办。
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>删除待办。归属“待办事项”开关（todo）。</summary>
public class DeleteTaskTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"taskId":{"type":"string","description":"$taskId$"}},"required":["taskId"]}""";

    private readonly TaskRepository _repo;
    private readonly ILogger<DeleteTaskTool> _logger;

    public string Name => "delete_task";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$taskId$", Param("Tool_DeleteTask_Param_TaskId")));
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "todo";

    public DeleteTaskTool(TaskRepository repo, ILogger<DeleteTaskTool> logger)
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
            var affected = await _repo.DeleteAsync(id);
            if (affected == 0)
                return ToolResult.Error(Text("Tool_DeleteTask_Msg_NotFound", id));

            _logger.LogInformation("已删除待办：{Id}", id);
            return ToolResult.Ok(Text("Tool_DeleteTask_Msg_Deleted", id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除待办失败");
            return ToolResult.Error(Text("Tool_DeleteTask_Msg_Failed", ex.Message));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
