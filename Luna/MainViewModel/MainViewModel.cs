using Microsoft.Extensions.Logging;

namespace Luna.ViewModels;

public class MainViewModel
{
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(ILogger<MainViewModel> logger)
    {
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");
        _logger.LogWarning("这是一条警告");
        // _logger.LogError(ex, "请求失败");
        _logger.LogError("请求失败");
    }

    public string InputText { get; set; } = string.Empty;
}