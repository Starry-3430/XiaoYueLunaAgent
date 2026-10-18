using System.Text.Json;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 元工具：按需解锁一组工具。实际解锁状态由 ChatGenerationService 在工具执行后写入会话运行时，
/// 下一轮请求会把该组工具一起下发给模型。
/// </summary>
public class EnableToolGroupTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"group_name":{"type":"string","enum":["filesystem","network","system","memory_modify"],"description":"$group$"}},"required":["group_name"]}""";

    public string Name => "enable_tool_group";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(
        SchemaTemplate.Replace("$group$", Param("Tool_EnableToolGroup_Param_GroupName")));
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var group = GetString(args, "group_name").Trim().ToLowerInvariant();
        if (!ToolGroups.IsValidGroup(group))
            return Task.FromResult(ToolResult.Error(Text("Tool_EnableToolGroup_Msg_Unknown", group)));

        var tools = string.Join(", ", ToolGroups.GetGroupToolNames(group));
        return Task.FromResult(ToolResult.Ok(Text("Tool_EnableToolGroup_Msg_Unlocked", group, tools)));
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value))
            return value.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
