using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>删除待办。归属“待办事项”开关（todo）。</summary>
public class DeleteTaskTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"taskId":{"type":"string","description":"待办的 ID"}},"required":["taskId"]}""");

    private readonly TaskRepository _repo;
    private readonly ILogger<DeleteTaskTool> _logger;

    public string Name => "delete_task";
    public string DisplayName => "删除待办";
    public string Description => "删除指定待办事项";
    public JsonElement ParametersSchema => Schema;
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
            return ToolResult.Error("缺少参数 taskId。");

        try
        {
            var affected = await _repo.DeleteAsync(id);
            if (affected == 0)
                return ToolResult.Error($"未找到待办：{id}");

            _logger.LogInformation("已删除待办：{Id}", id);
            return ToolResult.Ok($"已删除待办 {id}。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除待办失败");
            return ToolResult.Error($"删除待办失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
