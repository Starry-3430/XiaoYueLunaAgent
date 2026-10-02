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
        _tools.Values.Where(t => _toolSettings.IsEnabled(t.Name));

    public ITool? GetTool(string name) => _tools.GetValueOrDefault(name);

    public bool IsRegistered(string name) => _tools.ContainsKey(name);

    /// <summary>工具是否已注册且处于启用状态。</summary>
    public bool IsEnabled(string name) =>
        _tools.ContainsKey(name) && _toolSettings.IsEnabled(name);

    public ToolRegistry(IEnumerable<ITool> tools, ToolSettingsService toolSettings,
        ILogger<ToolRegistry> logger)
    {
        _toolSettings = toolSettings;
        _logger = logger;
        foreach (var tool in tools)
        {
            _tools[tool.Name] = tool;
            _logger.LogInformation("已注册工具: {Name}", tool.Name);
        }
    }
}