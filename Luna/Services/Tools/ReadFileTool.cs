using System.IO;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>
/// 读取文本文件。受 <see cref="FileSandbox"/> 限制：限定目录、扩展名与大小。
/// 风险等级 Medium，执行前需要用户确认。
/// </summary>
public class ReadFileTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"path":{"type":"string","description":"要读取的文件路径。相对路径基于工作目录，支持常见文本/代码扩展名"}},"required":["path"]}""");

    private readonly ILogger<ReadFileTool> _logger;

    public string Name => "read_file";
    public string DisplayName => "读取文件";
    public string Description => "读取指定文本文件的内容（仅限允许的目录、扩展名与大小）";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    public ReadFileTool(ILogger<ReadFileTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = ExtractPath(args);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("缺少参数 path。");

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
            return ToolResult.Error("读取文件被取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败：{Path}", fullPath);
            return ToolResult.Error($"读取文件失败：{ex.Message}");
        }
    }

    private static string ExtractPath(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p))
            return p.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
