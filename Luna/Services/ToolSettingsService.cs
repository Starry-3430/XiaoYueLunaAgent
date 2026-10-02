using Luna.Models;

namespace Luna.Services;

/// <summary>工具启用状态：读取/保存到 settings.json，默认启用。</summary>
public class ToolSettingsService
{
    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;

    public ToolSettingsService(AiSettings settings, SettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
    }

    /// <summary>工具是否启用；未记录时默认启用。</summary>
    public bool IsEnabled(string toolId)
    {
        if (string.IsNullOrEmpty(toolId)) return true;
        return !_settings.ToolStates.TryGetValue(toolId, out var enabled) || enabled;
    }

    /// <summary>设置工具启用状态并持久化。</summary>
    public void SetEnabled(string toolId, bool enabled)
    {
        if (string.IsNullOrEmpty(toolId)) return;
        if (_settings.ToolStates.TryGetValue(toolId, out var current) && current == enabled) return;

        _settings.ToolStates[toolId] = enabled;
        _settingsService.Save(_settings);
    }
}
