// 工具：读取文本/代码文件内容（受安全策略限制）。
using System.IO;
using System.Text;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 读取文件。文本/代码直接读取；PDF、Word、Excel、PPT、HTML 等文档先通过
/// <see cref="IDocumentConverterService"/>（MarkItDown）转换为 Markdown 再返回。
/// 可访问全盘任意位置，受 <see cref="FileSandbox"/> 的大小限制。
/// 风险等级 Medium，执行前需要用户确认（用户上传的附件除外）。
/// </summary>
public class ReadFileTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"path":{"type":"string","description":"$path$"},"keyword":{"type":"string","description":"$keyword$"},"start_line":{"type":"integer","description":"$startLine$"},"end_line":{"type":"integer","description":"$endLine$"}},"required":["path"]}""";

    /// <summary>不允许出现的控制字符占解码文本的比例上限。</summary>
    private const int MaxControlPercent = 5;

    /// <summary>UTF-8 解码后替换字符（U+FFFD）的比例上限，超过视为二进制/加密。</summary>
    private const int MaxReplacementPercent = 10;

    /// <summary>搜索/分段读取时允许的最大文本文件大小。</summary>
    private const long SegmentedTextMaxBytes = 64L * 1024 * 1024;

    /// <summary>工具结果字符预算，控制在 ChatGenerationService 的 5000 字符截断以内。</summary>
    private const int MaxResultChars = 4500;

    private const int MaxKeywordMatches = 10;
    private const int KeywordContextLines = 2;
    private const int MaxRangeLines = 120;
    private const int MaxLineChars = 500;

    /// <summary>需要先经 MarkItDown 转换的文档扩展名。</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
        ".html", ".htm", ".epub", ".rtf", ".odt", ".ods", ".odp",
    };

    /// <summary>文档转换读取的最大文件大小（100 MB，与附件上限一致）。</summary>
    private const long DocumentMaxBytes = 100L * 1024 * 1024;

    /// <summary>MarkItDown 转换失败时返回的友好文本前缀。</summary>
    private const string ConversionFailurePrefix = "> **文档转换失败**：";

    private readonly IDocumentConverterService _documentConverter;
    private readonly ILogger<ReadFileTool> _logger;

    public string Name => "read_file";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$path$", Param("Tool_ReadFile_Param_Path"))
        .Replace("$keyword$", Param("Tool_ReadFile_Param_Keyword"))
        .Replace("$startLine$", Param("Tool_ReadFile_Param_StartLine"))
        .Replace("$endLine$", Param("Tool_ReadFile_Param_EndLine")));
    public ToolRiskLevel Risk => ToolRiskLevel.Medium;

    /// <summary>文档转换可能较慢，给读文件工具更长的兜底时间。</summary>
    public TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(120);

    public ReadFileTool(IDocumentConverterService documentConverter, ILogger<ReadFileTool> logger)
    {
        _documentConverter = documentConverter;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var path = ExtractPath(args);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "path"));

        var keyword = GetOptionalString(args, "keyword");
        var startLine = GetOptionalInt(args, "start_line");
        var endLine = GetOptionalInt(args, "end_line");
        var hasSelector = !string.IsNullOrWhiteSpace(keyword) || startLine.HasValue || endLine.HasValue;

        var isDocument = DocumentExtensions.Contains(Path.GetExtension(path));
        var maxBytes = isDocument ? DocumentMaxBytes : SegmentedTextMaxBytes;
        if (!FileSandbox.TryResolveExisting(path, out var fullPath, out var error, maxBytes))
            return ToolResult.Error(error);

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception ex)
        {
            return ToolResult.Error(Text("Tool_ReadFile_Msg_Failed", ex.Message));
        }

        // 普通文本一次性整文件读取仍限制在 1MB；更大的文本必须搜索或分段。
        if (!isDocument && !hasSelector && length > FileSandbox.MaxBytes)
        {
            return ToolResult.Error(Text("Tool_ReadFile_Msg_TooLarge",
                AttachmentHelper.FormatSize(length)));
        }

        try
        {
            if (isDocument)
            {
                var markdown = await _documentConverter.ConvertToMarkdownAsync(fullPath, ct);

                if (markdown.StartsWith(ConversionFailurePrefix, StringComparison.Ordinal))
                    return ToolResult.Error(markdown);

                if (string.IsNullOrWhiteSpace(markdown))
                    return ToolResult.Error(Text("Tool_ReadFile_Msg_Empty"));

                _logger.LogInformation("文档读取成功，长度 {Length}", markdown.Length);
                return BuildResult(markdown, fullPath, keyword, startLine, endLine);
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            if (!TryDecodeText(bytes, out var content))
            {
                _logger.LogWarning("拒绝读取非文本文件");
                return ToolResult.Error(Text("Tool_ReadFile_Msg_Binary"));
            }

            _logger.LogInformation("读取文件成功，长度 {Length}", content.Length);
            return BuildResult(content, fullPath, keyword, startLine, endLine);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error(Text("Tool_ReadFile_Msg_Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败");
            return ToolResult.Error(Text("Tool_ReadFile_Msg_Failed", ex.Message));
        }
    }

    private static ToolResult BuildResult(string content, string fullPath,
        string? keyword, int? startLine, int? endLine)
    {
        if (!string.IsNullOrWhiteSpace(keyword))
            return SearchKeyword(content, fullPath, keyword, startLine, endLine);

        if (startLine.HasValue || endLine.HasValue)
            return ReadRange(content, fullPath, startLine, endLine);

        return ToolResult.Ok(content);
    }

    /// <summary>按关键词搜索，返回命中行及前后各两行上下文。</summary>
    private static ToolResult SearchKeyword(string content, string fullPath,
        string keyword, int? startLine, int? endLine)
    {
        var matchLines = new List<int>();
        var lineNo = 0;
        var reachedLimit = false;

        foreach (var line in EnumerateLines(content))
        {
            lineNo++;
            if (startLine is { } start && lineNo < Math.Max(1, start)) continue;
            if (endLine is { } end && lineNo > Math.Max(1, end)) break;

            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matchLines.Add(lineNo);
                if (matchLines.Count >= MaxKeywordMatches)
                {
                    reachedLimit = true;
                    break;
                }
            }
        }

        if (matchLines.Count == 0)
            return ToolResult.Ok(Text("Tool_ReadFile_Msg_KeywordNotFound", keyword));

        var displayLines = new SortedSet<int>();
        foreach (var match in matchLines)
        {
            var from = Math.Max(1, match - KeywordContextLines);
            var to = match + KeywordContextLines;
            for (var i = from; i <= to; i++)
                displayLines.Add(i);
        }

        var sb = new StringBuilder();
        sb.AppendLine(Text("Tool_ReadFile_Msg_SearchHeader",
            Path.GetFileName(fullPath), keyword, matchLines.Count));

        if (reachedLimit)
            sb.AppendLine(Text("Tool_ReadFile_Msg_MoreMatches"));

        var truncated = false;
        lineNo = 0;
        var lastLine = 0;

        foreach (var line in EnumerateLines(content))
        {
            lineNo++;
            if (!displayLines.Contains(lineNo)) continue;

            if (lastLine > 0 && lineNo > lastLine + 1)
                sb.AppendLine("...");

            AppendNumberedLine(sb, lineNo, line);
            lastLine = lineNo;

            if (sb.Length >= MaxResultChars)
            {
                truncated = true;
                break;
            }
        }

        if (truncated)
            sb.AppendLine(Text("Tool_ReadFile_Msg_ResultTruncated"));

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    /// <summary>按行号分段读取，单次最多 120 行。</summary>
    private static ToolResult ReadRange(string content, string fullPath, int? startLine, int? endLine)
    {
        var start = Math.Max(1, startLine ?? 1);
        var end = Math.Max(1, endLine ?? start + MaxRangeLines - 1);
        if (end < start)
            return ToolResult.Error(Text("Tool_ReadFile_Msg_BadRange", start, end));

        if (end - start + 1 > MaxRangeLines)
            end = start + MaxRangeLines - 1;

        var sb = new StringBuilder();
        sb.AppendLine(Text("Tool_ReadFile_Msg_RangeHeader",
            Path.GetFileName(fullPath), start, end));

        var lineNo = 0;
        var emitted = 0;
        var truncated = false;

        foreach (var line in EnumerateLines(content))
        {
            lineNo++;
            if (lineNo < start) continue;
            if (lineNo > end) break;

            AppendNumberedLine(sb, lineNo, line);
            emitted++;

            if (sb.Length >= MaxResultChars)
            {
                truncated = true;
                break;
            }
        }

        if (emitted == 0)
            return ToolResult.Ok(Text("Tool_ReadFile_Msg_RangeEmpty", start, end));

        if (truncated)
            sb.AppendLine(Text("Tool_ReadFile_Msg_ResultTruncated"));

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static IEnumerable<string> EnumerateLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static void AppendNumberedLine(StringBuilder sb, int lineNo, string line)
    {
        if (line.Length > MaxLineChars)
            line = line[..MaxLineChars] + "…";

        sb.Append(lineNo).Append(": ").AppendLine(line);
    }

    private static string ExtractPath(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p))
            return p.GetString()?.Trim().Trim('"', '\'').Trim() ?? string.Empty;
        return string.Empty;
    }

    private static string? GetOptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static int? GetOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        // 兼容部分模型把数字参数序列化成字符串的情况。
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }

    /// <summary>
    /// 尝试把字节解码为文本。识别 BOM（UTF-8/16/32）、无 BOM 的 UTF-16，
    /// 其余按 UTF-8 解码；含 NUL、控制字符过多或替换字符过多时判定为二进制/加密。
    /// </summary>
    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length == 0) return true;

        // 带 BOM 的编码
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return TryAccept(new UTF32Encoding(false, false).GetString(bytes, 4, bytes.Length - 4), out text);
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return TryAccept(new UTF32Encoding(true, false).GetString(bytes, 4, bytes.Length - 4), out text);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return TryAccept(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), out text);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return TryAccept(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), out text);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return TryAccept(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), out text);

        // 无 BOM：通过 NUL 的奇偶分布判断 UTF-16
        var sample = Math.Min(bytes.Length, 4096);
        int zeroEven = 0, zeroOdd = 0;
        for (var i = 0; i < sample; i++)
        {
            if (bytes[i] != 0) continue;
            if ((i & 1) == 0) zeroEven++;
            else zeroOdd++;
        }

        if (sample >= 4)
        {
            if (zeroOdd > sample / 8 && zeroEven < sample / 32)
                return TryAccept(Encoding.Unicode.GetString(bytes), out text);
            if (zeroEven > sample / 8 && zeroOdd < sample / 32)
                return TryAccept(Encoding.BigEndianUnicode.GetString(bytes), out text);
        }

        // 单字节文本不应含 NUL
        if (zeroEven + zeroOdd > 0) return false;

        var decoded = Encoding.UTF8.GetString(bytes);
        if (decoded.Length == 0) return true;

        // 随机/加密数据通常无法构成合法 UTF-8，会产生大量替换字符
        var replacements = 0;
        foreach (var ch in decoded)
            if (ch == '\uFFFD') replacements++;
        if (replacements * 100 > decoded.Length * MaxReplacementPercent) return false;

        return TryAccept(decoded, out text);
    }

    private static bool TryAccept(string value, out string text)
    {
        text = value;
        if (value.Length == 0) return true;

        var control = 0;
        foreach (var ch in value)
        {
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r' or '\f' or '\b' or '\u001B'))
                control++;
        }

        return control * 100 <= value.Length * MaxControlPercent;
    }
}
