using System.Text.Json;

namespace Luna.Services.Tools;

/// <summary>
/// 把工具调用参数转换为面向用户的纯文本，避免在界面上直接暴露 JSON。
/// 目前仅 PowerShell 做专门处理，其余工具暂保持原始参数文本，便于后续逐步接入。
/// </summary>
public static class ToolArgumentFormatter
{
    /// <summary>
    /// 返回工具参数的可读文本。PowerShell 只返回命令本身；其它工具回退到原始文本。
    /// 解析失败（例如流式输出中的半截 JSON）时同样回退，避免抛异常。
    /// </summary>
    public static string FormatArguments(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;

        if (IsPowerShell(toolName) && TryGetPowerShellCommand(argumentsJson) is { } command)
            return command;

        return argumentsJson.Trim();
    }

    public static bool IsPowerShell(string? toolName)
        => string.Equals(toolName, "powershell", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取 PowerShell 的 command 参数；无法解析时返回 null。</summary>
    public static string? TryGetPowerShellCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String)
            {
                return command.GetString();
            }
        }
        catch (JsonException)
        {
            // 流式传输中可能是半截 JSON。
        }

        return null;
    }
}
