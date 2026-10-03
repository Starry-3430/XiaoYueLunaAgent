using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolSettingsService _toolSettings;
    private readonly ILogger<ToolRegistry> _logger;

    public IEnumerable<ITool> GetAllTools() => _tools.Values;

    /// <summary>已注册且被用户启用的工具（用于暴露给 AI）。</summary>
    public IEnumerable<ITool> GetEnabledTools() =>
        _tools.Values.Where(t => _toolSettings.IsEnabled(t.SettingsKey));

    public ITool? GetTool(string name) => _tools.GetValueOrDefault(name);

    public bool IsRegistered(string name) => _tools.ContainsKey(name);

    /// <summary>同一开关 Key 下的全部工具。</summary>
    public IEnumerable<ITool> GetToolsBySettingsKey(string settingsKey) =>
        _tools.Values.Where(t => string.Equals(t.SettingsKey, settingsKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>该开关 Key 是否已有任一工具注册（用于工具页的“已实现”判定）。</summary>
    public bool IsGroupRegistered(string settingsKey) => GetToolsBySettingsKey(settingsKey).Any();

    /// <summary>同一开关 Key 下的最高风险等级。</summary>
    public ToolRiskLevel GetGroupRisk(string settingsKey) =>
        GetToolsBySettingsKey(settingsKey).Select(t => t.Risk).DefaultIfEmpty(ToolRiskLevel.None).Max();

    /// <summary>工具是否已注册且处于启用状态（按其开关 Key 判定）。</summary>
    public bool IsEnabled(string name) =>
        _tools.TryGetValue(name, out var tool) && _toolSettings.IsEnabled(tool.SettingsKey);

    public ToolRegistry(IEnumerable<ITool> tools, ToolSettingsService toolSettings,
        ILogger<ToolRegistry> logger)
    {
        _toolSettings = toolSettings;
        _logger = logger;
        foreach (var tool in tools)
        {
            _tools[tool.Name] = tool;
            _logger.LogInformation("已注册工具: {Name} (开关: {Key})", tool.Name, tool.SettingsKey);
        }
    }
}
