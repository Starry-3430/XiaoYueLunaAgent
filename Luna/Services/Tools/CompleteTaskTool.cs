using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>标记待办为完成。归属“待办事项”开关（todo）。</summary>
public class CompleteTaskTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"taskId":{"type":"string","description":"待办的 ID"}},"required":["taskId"]}""");

    private readonly TaskRepository _repo;
    private readonly ILogger<CompleteTaskTool> _logger;

    public string Name => "complete_task";
    public string DisplayName => "完成待办";
    public string Description => "把指定待办标记为已完成";
    public JsonElement ParametersSchema => Schema;
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
            return ToolResult.Error("缺少参数 taskId。");

        try
        {
            var affected = await _repo.CompleteAsync(id);
            if (affected == 0)
                return ToolResult.Error($"未找到待办：{id}");

            _logger.LogInformation("已完成待办：{Id}", id);
            return ToolResult.Ok($"已将待办 {id} 标记为完成。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "完成待办失败");
            return ToolResult.Error($"完成待办失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
