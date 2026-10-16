// 工具：写入文本文件（限定输出目录 + 扩展名黑名单）。
using System.IO;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

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

    private const string SchemaTemplate =
        """{"type":"object","properties":{"path":{"type":"string","description":"$path$"},"content":{"type":"string","description":"$content$"},"mode":{"type":"string","enum":["create","overwrite","append"],"description":"$mode$"}},"required":["path","content"]}""";

    private readonly AiSettings _settings;
    private readonly ToolPermissionService _permission;
    private readonly ILogger<WriteFileTool> _logger;

    public string Name => "write_file";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$path$", Param("Tool_WriteFile_Param_Path"))
        .Replace("$content$", Param("Tool_WriteFile_Param_Content"))
        .Replace("$mode$", Param("Tool_WriteFile_Param_Mode")));
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
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "path"));

        var mode = GetString(args, "mode").ToLowerInvariant();
        if (string.IsNullOrEmpty(mode)) mode = ModeCreate;
        if (mode is not (ModeCreate or ModeOverwrite or ModeAppend))
            return ToolResult.Error(Text("Tool_WriteFile_Msg_BadMode", mode));

        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "content"));
        var content = contentElement.GetString() ?? string.Empty;

        if (!TryResolveTarget(path, out var fullPath, out var error))
            return ToolResult.Error(error);

        if (content.Contains('\0'))
            return ToolResult.Error(Text("Tool_WriteFile_Msg_Binary"));

        var exists = File.Exists(fullPath);

        if (mode == ModeCreate && exists)
            return ToolResult.Error(Text("Tool_WriteFile_Msg_Exists", Path.GetFileName(fullPath)));

        if (mode == ModeOverwrite && exists)
        {
            var decision = _permission.RequestConfirmation(
                this, Text("Tool_WriteFile_Msg_ConfirmOverwrite", Path.GetFileName(fullPath)));
            if (decision == ToolPermissionDecision.Deny)
            {
                _logger.LogInformation("用户拒绝覆盖文件：{Path}", fullPath);
                return ToolResult.Error(Text("Tool_WriteFile_Msg_DeniedOverwrite"));
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
            return ToolResult.Ok(mode == ModeAppend
                ? Text("Tool_WriteFile_Msg_Appended", fullPath, content.Length)
                : Text("Tool_WriteFile_Msg_Written", fullPath, content.Length));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error(Text("Tool_WriteFile_Msg_Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入文件失败：{Path}", fullPath);
            return ToolResult.Error(Text("Tool_WriteFile_Msg_Failed", ex.Message));
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
            error = Text("Tool_WriteFile_Msg_NoOutputDir");
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(outputDirectory);
        }
        catch (Exception ex)
        {
            error = Text("Tool_WriteFile_Msg_BadOutputDir", ex.Message);
            return false;
        }

        if (!Directory.Exists(root))
        {
            error = Text("Tool_WriteFile_Msg_NoOutputDirExists", root);
            return false;
        }

        try
        {
            var candidate = Path.IsPathRooted(input) ? input : Path.Combine(root, input);
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = Text("Tool_WriteFile_Msg_BadPath", ex.Message);
            return false;
        }

        if (!FileSandbox.IsInsideDirectory(fullPath, root))
        {
            error = Text("Tool_WriteFile_Msg_Outside");
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension))
        {
            error = Text("Tool_WriteFile_Msg_NoExtension");
            return false;
        }

        if (FileSandbox.BlockedExtensions.Contains(extension))
        {
            error = Text("Tool_WriteFile_Msg_BlockedType", extension);
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
