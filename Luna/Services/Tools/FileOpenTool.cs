// 工具：使用 Windows 文件关联打开文件、目录或启动应用程序。
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 使用 Windows 默认文件关联打开文件或目录，也可以直接启动应用程序。
/// 风险等级 Medium：打开应用可能产生外部副作用，因此执行前需要用户确认。
/// </summary>
public sealed class FileOpenTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"path":{"type":"string","description":"$path$"}},"required":["path"]}""";

    private readonly ILogger<FileOpenTool> _logger;

    public string Name => "file_open";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$path$", Param("Tool_FileOpen_Param_Path")));
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    public FileOpenTool(ILogger<FileOpenTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var input = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult(ToolResult.Error(Text("Tool_Msg_MissingParam", "path")));

        if (ct.IsCancellationRequested)
            return Task.FromResult(ToolResult.Error(Text("Tool_FileOpen_Msg_Cancelled")));

        if (!TryResolvePath(input, out var fullPath, out var error))
            return Task.FromResult(ToolResult.Error(error));

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
            });

            _logger.LogInformation("已打开路径");
            return Task.FromResult(ToolResult.Ok(Text("Tool_FileOpen_Msg_Opened", fullPath)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开路径失败");
            return Task.FromResult(ToolResult.Error(Text("Tool_FileOpen_Msg_Failed", ex.Message)));
        }
    }

    private static bool TryResolvePath(string input, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        var value = input.Trim().Trim('"');

        try
        {
            if (UserEnvironment.TryResolveFolder(value, out var folder))
                fullPath = Path.GetFullPath(folder);
            else
            {
                var candidate = Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(FileSandbox.Root, value);
                fullPath = Path.GetFullPath(candidate);
            }
        }
        catch (Exception ex)
        {
            error = Text("Tool_FileOpen_Msg_BadPath", ex.Message);
            return false;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            error = Text("Tool_FileOpen_Msg_NotFound", fullPath);
            return false;
        }

        return true;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
