// 工具：读取文本/代码文件内容（受安全策略限制）。
using System.IO;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 读取文本文件。可访问全盘任意位置，受 <see cref="FileSandbox"/> 限制：限定扩展名与大小。
/// 风险等级 Medium，执行前需要用户确认。
/// </summary>
public class ReadFileTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"path":{"type":"string","description":"$path$"}},"required":["path"]}""";

    private readonly ILogger<ReadFileTool> _logger;

    public string Name => "read_file";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$path$", Param("Tool_ReadFile_Param_Path")));
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    public ReadFileTool(ILogger<ReadFileTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = ExtractPath(args);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "path"));

        if (!FileSandbox.TryResolveExisting(path, out var fullPath, out var error))
            return ToolResult.Error(error);

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct);
            _logger.LogInformation("读取文件成功：{Path}，长度 {Length}", fullPath, content.Length);
            return ToolResult.Ok(content);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error(Text("Tool_ReadFile_Msg_Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败：{Path}", fullPath);
            return ToolResult.Error(Text("Tool_ReadFile_Msg_Failed", ex.Message));
        }
    }

    private static string ExtractPath(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p))
            return p.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
