using System.Windows;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class ClipboardReadTool : ITool
{
    private readonly ILogger<ClipboardReadTool> _logger;

    public string Name => "read_clipboard";
    public string DisplayName => "读取剪贴板";
    public string Description => "读取系统剪贴板中的文本内容";
    public string ParametersSchema => """{"type":"object","properties":{},"required":[]}""";

    public ClipboardReadTool(ILogger<ClipboardReadTool> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var text = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Clipboard.ContainsText())
                    return Clipboard.GetText();
                return "";
            });
            _logger.LogInformation("剪贴板读取成功, 长度: {Length}", text.Length);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "剪贴板读取失败");
            return "";
        }
    }
}