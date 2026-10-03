using System.Text.Json;

namespace Luna.Services.Tools;

public interface ITool
{
    string Name { get; }
    string DisplayName { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }

    /// <summary>
    /// 风险等级。Medium / High 会在执行前要求用户确认；None / Low 直接执行。
    /// 默认为 None，便于信息类工具无需声明。
    /// </summary>
    ToolRiskLevel Risk => ToolRiskLevel.None;

    /// <summary>
    /// 即使风险等级不高也强制要求用户确认（例如内部实现同样敏感的工具）。
    /// </summary>
    bool RequiresUserConfirmation => false;

    Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default);
}
