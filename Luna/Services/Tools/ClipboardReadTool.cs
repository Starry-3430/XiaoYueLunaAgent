using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public class ClipboardReadTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Empty;

    private readonly ILogger<ClipboardReadTool> _logger;

    public string Name => "read_clipboard";
    public string DisplayName => "读取剪贴板";
    public string Description => "读取系统剪贴板中的文本内容";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public ClipboardReadTool(ILogger<ClipboardReadTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
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
            return ToolResult.Ok(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "剪贴板读取失败");
            return ToolResult.Error("读取剪贴板失败：" + ex.Message);
        }
    }
}
