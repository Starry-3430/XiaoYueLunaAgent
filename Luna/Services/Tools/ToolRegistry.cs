using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;

    public IEnumerable<ITool> GetAllTools() => _tools.Values;

    public ITool? GetTool(string name) => _tools.GetValueOrDefault(name);

    public bool IsRegistered(string name) => _tools.ContainsKey(name);

    public ToolRegistry(IEnumerable<ITool> tools, ILogger<ToolRegistry> logger)
    {
        _logger = logger;
        foreach (var tool in tools)
        {
            _tools[tool.Name] = tool;
            _logger.LogInformation("已注册工具: {Name}", tool.Name);
        }
    }
}