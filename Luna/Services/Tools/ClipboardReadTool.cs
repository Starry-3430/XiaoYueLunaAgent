// 工具：读取系统剪贴板文本。
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

public class ClipboardReadTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Empty;

    private readonly ILogger<ClipboardReadTool> _logger;

    public string Name => "read_clipboard";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
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
            return ToolResult.Error(Text("Tool_ReadClipboard_Msg_Failed", ex.Message));
        }
    }
}
