namespace Luna.Services.Tools;

/// <summary>
/// 工具风险等级。Medium / High 会在执行前要求用户确认；
/// None / Low 直接执行。
/// </summary>
public enum ToolRiskLevel
{
    None,
    Low,
    Medium,
    High,
}
