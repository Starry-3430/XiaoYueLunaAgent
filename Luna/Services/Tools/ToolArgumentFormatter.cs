using System.IO;
using System.Text;
using System.Text.Json;
using Luna.Localization;

namespace Luna.Services.Tools;

/// <summary>
/// 把工具调用参数转换为面向用户的纯文本，避免在界面上直接暴露 JSON。
/// PowerShell / code_execute 显示代码；read_file / file_open 显示文件信息；
/// 其余工具使用通用的 key-value 展示。
/// </summary>
public static class ToolArgumentFormatter
{
    /// <summary>单个参数值的最大展示长度，避免大段内容撑满界面。</summary>
    private const int MaxValueLength = 500;

    public static string FormatArguments(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;

        if (IsPowerShell(toolName) && TryGetStringArgument(argumentsJson, "command") is { } command)
            return command;

        if (IsCodeExecute(toolName) && TryGetStringArgument(argumentsJson, "code") is { } code)
            return code;

        if (IsReadFile(toolName) && TryGetStringArgument(argumentsJson, "path") is { Length: > 0 } readPath)
            return FormatReadFile(argumentsJson, readPath);

        if (IsFileOpen(toolName) && TryGetStringArgument(argumentsJson, "path") is { Length: > 0 } openPath)
            return FormatFileInfo(openPath);

        if (IsWriteFile(toolName))
            return FormatWriteFile(argumentsJson);

        if (IsGit(toolName))
            return FormatGit(argumentsJson);

        if (IsWebSearch(toolName) && TryGetStringArgument(argumentsJson, "query") is { } webQuery)
            return webQuery;

        if (IsFetchUrl(toolName) && TryGetStringArgument(argumentsJson, "url") is { } url)
            return url;

        return FormatKeyValue(argumentsJson);
    }

    public static bool IsPowerShell(string? toolName)
        => string.Equals(toolName, "powershell", StringComparison.OrdinalIgnoreCase);

    public static bool IsCodeExecute(string? toolName)
        => string.Equals(toolName, "code_execute", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadFile(string? toolName)
        => string.Equals(toolName, "read_file", StringComparison.OrdinalIgnoreCase);

    public static bool IsFileOpen(string? toolName)
        => string.Equals(toolName, "file_open", StringComparison.OrdinalIgnoreCase);

    public static bool IsWebSearch(string? toolName)
        => string.Equals(toolName, "web_search", StringComparison.OrdinalIgnoreCase);

    public static bool IsFetchUrl(string? toolName)
        => string.Equals(toolName, "fetch_url", StringComparison.OrdinalIgnoreCase);

    public static bool IsWriteFile(string? toolName)
        => string.Equals(toolName, "write_file", StringComparison.OrdinalIgnoreCase);

    public static bool IsGit(string? toolName)
        => string.Equals(toolName, "git", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取 PowerShell 的 command 参数；无法解析时返回 null。</summary>
    public static string? TryGetPowerShellCommand(string? argumentsJson)
        => TryGetStringArgument(argumentsJson, "command");

    /// <summary>读取代码类工具（powershell / code_execute）的代码文本。</summary>
    public static string? TryGetCode(string? toolName, string? argumentsJson)
    {
        if (IsPowerShell(toolName)) return TryGetStringArgument(argumentsJson, "command");
        if (IsCodeExecute(toolName)) return TryGetStringArgument(argumentsJson, "code");
        return null;
    }

    /// <summary>读取一个字符串参数；无法解析时返回 null。</summary>
    public static string? TryGetStringArgument(string? argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // 流式传输中可能是半截 JSON。
        }

        return null;
    }

    /// <summary>文件类工具的参数展示：路径、文件名、大小。</summary>
    private static string FormatFileInfo(string path)
    {
        var loc = LocalizationService.Instance;
        var displayPath = path.Trim();
        var namePath = displayPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(namePath);
        var sizeText = TryGetFileSizeText(displayPath);

        var sb = new StringBuilder();
        sb.Append(loc["Tool_Arg_FilePath"]).Append(": ").Append(displayPath);
        if (!string.IsNullOrEmpty(fileName))
            sb.AppendLine().Append(loc["Tool_Arg_FileName"]).Append(": ").Append(fileName);
        if (!string.IsNullOrEmpty(sizeText))
            sb.AppendLine().Append(loc["Tool_Arg_FileSize"]).Append(": ").Append(sizeText);

        return sb.ToString();
    }

    /// <summary>read_file 的参数展示：路径/大小，外加关键词或行范围。</summary>
    private static string FormatReadFile(string argumentsJson, string path)
    {
        var loc = LocalizationService.Instance;
        var sb = new StringBuilder(FormatFileInfo(path));

        var keyword = TryGetStringArgument(argumentsJson, "keyword");
        if (!string.IsNullOrWhiteSpace(keyword))
            sb.AppendLine().Append(loc["Tool_Arg_Keyword"]).Append(": ").Append(keyword);

        var start = TryGetIntArgument(argumentsJson, "start_line");
        var end = TryGetIntArgument(argumentsJson, "end_line");
        if (start.HasValue || end.HasValue)
        {
            sb.AppendLine().Append(loc["Tool_Arg_LineRange"]).Append(": ")
              .Append(start?.ToString() ?? "1")
              .Append('-')
              .Append(end?.ToString() ?? "…");
        }

        return sb.ToString();
    }

    private static int? TryGetIntArgument(string? argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(name, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        catch (JsonException)
        {
            // 流式传输中可能是半截 JSON。
        }

        return null;
    }

    private static string? TryGetFileSizeText(string path)
    {
        try
        {
            var candidate = Path.IsPathRooted(path) ? path : Path.Combine(FileSandbox.Root, path);
            var info = new FileInfo(candidate);
            return info.Exists ? AttachmentHelper.FormatSize(info.Length) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>write_file 的参数展示：路径/文件名、写入模式、内容预览。</summary>
    private static string FormatWriteFile(string argumentsJson)
    {
        var loc = LocalizationService.Instance;
        var path = TryGetStringArgument(argumentsJson, "path");
        var mode = TryGetStringArgument(argumentsJson, "mode");
        var content = TryGetStringArgument(argumentsJson, "content");

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var displayPath = path.Trim();
            var namePath = displayPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileName(namePath);
            sb.Append(loc["Tool_Arg_FilePath"]).Append(": ").Append(displayPath);
            if (!string.IsNullOrEmpty(fileName))
                sb.AppendLine().Append(loc["Tool_Arg_FileName"]).Append(": ").Append(fileName);
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(loc["Tool_Arg_WriteMode"]).Append(": ").Append(ModeLabel(mode));
        }

        if (!string.IsNullOrEmpty(content))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(loc["Tool_Arg_Content"]).AppendLine().Append(Truncate(content));
        }

        return sb.Length == 0 ? FormatKeyValue(argumentsJson) : sb.ToString();
    }

    private static string ModeLabel(string mode) => mode.ToLowerInvariant() switch
    {
        "create" => LocalizationService.Instance["Tool_Arg_Mode_Create"],
        "overwrite" => LocalizationService.Instance["Tool_Arg_Mode_Overwrite"],
        "append" => LocalizationService.Instance["Tool_Arg_Mode_Append"],
        _ => mode,
    };

    /// <summary>git 的参数展示：动作、参数、提交信息、路径。</summary>
    private static string FormatGit(string argumentsJson)
    {
        var loc = LocalizationService.Instance;
        var action = TryGetStringArgument(argumentsJson, "action");
        var message = TryGetStringArgument(argumentsJson, "message");
        var path = TryGetStringArgument(argumentsJson, "path");
        var args = TryGetStringArray(argumentsJson, "args");

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(action))
            sb.Append(loc["Tool_Arg_GitAction"]).Append(": ").Append(action);

        if (args.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(loc["Tool_Arg_Args"]).Append(": ").Append(string.Join(" ", args));
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(loc["Tool_Arg_CommitMessage"]).Append(": ").Append(message);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(loc["Tool_Arg_FilePath"]).Append(": ").Append(path);
        }

        return sb.Length == 0 ? FormatKeyValue(argumentsJson) : sb.ToString();
    }

    private static List<string> TryGetStringArray(string? argumentsJson, string name)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(argumentsJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(name, out var array) ||
                array.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
        }
        catch (JsonException)
        {
            // 流式传输中可能是半截 JSON。
        }

        return result;
    }

    /// <summary>通用 key-value 展示：解析 JSON 对象并逐行输出。</summary>
    private static string FormatKeyValue(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Truncate(argumentsJson.Trim());

            var lines = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = FormatValue(property.Value);
                if (value is null) continue;
                lines.Add($"{Label(property.Name)}: {value}");
            }

            return lines.Count == 0
                ? LocalizationService.Instance["Tool_Arg_NoArgs"]
                : string.Join(Environment.NewLine, lines);
        }
        catch (JsonException)
        {
            return Truncate(argumentsJson.Trim());
        }
    }

    private static string? FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Truncate(value.GetString() ?? string.Empty),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Array => FormatArray(value),
        JsonValueKind.Object => Truncate(value.GetRawText()),
        _ => null,
    };

    private static string? FormatArray(JsonElement array)
    {
        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var formatted = FormatValue(item);
            if (formatted is not null) items.Add(formatted);
        }

        return items.Count == 0 ? null : Truncate(string.Join(", ", items));
    }

    private static string Label(string key) => key switch
    {
        "path" => LocalizationService.Instance["Tool_Arg_FilePath"],
        "query" => LocalizationService.Instance["Tool_Arg_Query"],
        "max_results" => LocalizationService.Instance["Tool_Arg_MaxResults"],
        "url" => LocalizationService.Instance["Tool_Arg_Url"],
        "content" => LocalizationService.Instance["Tool_Arg_Content"],
        "mode" => LocalizationService.Instance["Tool_Arg_WriteMode"],
        "depth" => LocalizationService.Instance["Tool_Arg_Depth"],
        "max_entries" => LocalizationService.Instance["Tool_Arg_MaxEntries"],
        "title" => LocalizationService.Instance["Tool_Arg_Title"],
        "message" => LocalizationService.Instance["Tool_Arg_Message"],
        "timeout_ms" => LocalizationService.Instance["Tool_Arg_TimeoutMs"],
        "dueAt" => LocalizationService.Instance["Tool_Arg_DueAt"],
        "remindAt" => LocalizationService.Instance["Tool_Arg_RemindAt"],
        "notes" => LocalizationService.Instance["Tool_Arg_Notes"],
        "status" => LocalizationService.Instance["Tool_Arg_Status"],
        "taskId" => LocalizationService.Instance["Tool_Arg_TaskId"],
        "keyword" => LocalizationService.Instance["Tool_Arg_Keyword"],
        "limit" => LocalizationService.Instance["Tool_Arg_Limit"],
        "key" => LocalizationService.Instance["Tool_Arg_Key"],
        "value" => LocalizationService.Instance["Tool_Arg_Value"],
        "action" => LocalizationService.Instance["Tool_Arg_GitAction"],
        "args" => LocalizationService.Instance["Tool_Arg_Args"],
        _ => key,
    };

    private static string Truncate(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";
}
