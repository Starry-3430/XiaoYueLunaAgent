using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 完全访问模式开关。启用后解除文件写入目录、扩展名以及代码/命令执行黑名单限制；
/// 关闭后恢复常规沙箱策略。开关会立即持久化到 settings.json。
/// </summary>
public class FullAccessService
{
    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ILogger<FullAccessService> _logger;

    public FullAccessService(AiSettings settings, SettingsService settingsService,
        ILogger<FullAccessService> logger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>当前是否处于完全访问模式。</summary>
    public bool IsEnabled => _settings.FullAccessEnabled;

    /// <summary>完全访问模式切换后触发。</summary>
    public event EventHandler? Changed;

    /// <summary>设置完全访问模式并持久化。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_settings.FullAccessEnabled == enabled) return;

        _settings.FullAccessEnabled = enabled;
        _settingsService.Save(_settings);

        if (enabled)
            _logger.LogWarning("用户已启用完全访问模式");
        else
            _logger.LogWarning("用户已关闭完全访问模式");

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
