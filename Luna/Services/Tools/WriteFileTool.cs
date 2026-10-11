// 工具：写入文本文件（限定输出目录 + 扩展名黑名单）。
using System.IO;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>
/// 写入文本文件。只允许写入设置中的“输出目录”（为空则拒绝执行），并禁止可执行/二进制类型。
/// 覆盖已存在文件（overwrite）前通过 <see cref="ToolPermissionService"/> 弹二次确认。
/// 风险等级 Low：授权由“输出目录”约束，覆盖另行确认。
/// </summary>
public class WriteFileTool : ITool
{
    private const string ModeCreate = "create";
    private const string ModeOverwrite = "overwrite";
    private const string ModeAppend = "append";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"path":{"type":"string","description":"相对输出目录的路径（也可用输出目录下的绝对路径）"},"content":{"type":"string","description":"要写入的文件内容"},"mode":{"type":"string","enum":["create","overwrite","append"],"description":"写入模式，默认 create；create 遇已存在文件会失败，overwrite 覆盖前需确认，append 追加"}},"required":["path","content"]}""");

    private readonly AiSettings _settings;
    private readonly ToolPermissionService _permission;
    private readonly ILogger<WriteFileTool> _logger;

    public string Name => "write_file";
    public string DisplayName => "写入文件";
    public string Description => "在设置的输出目录内写入文本文件（create/overwrite/append），禁止可执行与二进制类型";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;

    public WriteFileTool(AiSettings settings, ToolPermissionService permission, ILogger<WriteFileTool> logger)
    {
        _settings = settings;
        _permission = permission;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("缺少参数 path。");

        var mode = GetString(args, "mode").ToLowerInvariant();
        if (string.IsNullOrEmpty(mode)) mode = ModeCreate;
        if (mode is not (ModeCreate or ModeOverwrite or ModeAppend))
            return ToolResult.Error($"不支持的 mode：{mode}（可用：create / overwrite / append）。");

        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
            return ToolResult.Error("缺少参数 content。");
        var content = contentElement.GetString() ?? string.Empty;

        if (!TryResolveTarget(path, out var fullPath, out var error))
            return ToolResult.Error(error);

        if (content.Contains('\0'))
            return ToolResult.Error("内容包含二进制数据（NUL 字节），已被安全策略拒绝。");

        var exists = File.Exists(fullPath);

        if (mode == ModeCreate && exists)
            return ToolResult.Error($"文件已存在：{Path.GetFileName(fullPath)}。如需覆盖请使用 overwrite 模式。");

        if (mode == ModeOverwrite && exists)
        {
            var decision = _permission.RequestConfirmation(
                this, $"覆盖文件 {Path.GetFileName(fullPath)}？\n内容将丢失。");
            if (decision == ToolPermissionDecision.Deny)
            {
                _logger.LogInformation("用户拒绝覆盖文件：{Path}", fullPath);
                return ToolResult.Error("用户拒绝覆盖。");
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (mode == ModeAppend)
                await File.AppendAllTextAsync(fullPath, content, ct);
            else
                await File.WriteAllTextAsync(fullPath, content, ct);

            _logger.LogInformation("写入文件成功：{Path}（mode={Mode}，{Length} 字符）", fullPath, mode, content.Length);
            return ToolResult.Ok($"已{(mode == ModeAppend ? "追加" : "写入")}文件：{fullPath}（{content.Length} 字符）");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("写入文件被取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入文件失败：{Path}", fullPath);
            return ToolResult.Error($"写入文件失败：{ex.Message}");
        }
    }

    /// <summary>把相对路径解析到输出目录并做越界 / 扩展名校验。</summary>
    private bool TryResolveTarget(string input, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        var outputDirectory = _settings.OutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error = "未配置输出目录，请先在“设置 - 通用”中指定输出目录后再写入。";
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(outputDirectory);
        }
        catch (Exception ex)
        {
            error = $"输出目录无效：{ex.Message}";
            return false;
        }

        if (!Directory.Exists(root))
        {
            error = $"输出目录不存在：{root}";
            return false;
        }

        try
        {
            var candidate = Path.IsPathRooted(input) ? input : Path.Combine(root, input);
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = $"无效路径：{ex.Message}";
            return false;
        }

        if (!FileSandbox.IsInsideDirectory(fullPath, root))
        {
            error = "路径不在输出目录内。";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension))
        {
            error = "文件没有扩展名，已被安全策略拒绝。";
            return false;
        }

        if (FileSandbox.BlockedExtensions.Contains(extension))
        {
            error = $"不允许写入的文件类型：{extension}";
            return false;
        }

        return true;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
