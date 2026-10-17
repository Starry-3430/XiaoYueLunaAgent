// 工具：读取文本/代码文件内容（受安全策略限制）。
using System.IO;
using System.Text;
using System.Text.Json;
using Luna.Services;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>
/// 读取文本文件。可访问全盘任意位置，受 <see cref="FileSandbox"/> 限制：只限制大小；
/// 读取后按内容判断是否为可读文本，二进制/加密文件会被拒绝。风险等级 Medium，执行前需要用户确认。
/// </summary>
public class ReadFileTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"path":{"type":"string","description":"$path$"}},"required":["path"]}""";

    /// <summary>不允许出现的控制字符占解码文本的比例上限。</summary>
    private const int MaxControlPercent = 5;

    /// <summary>UTF-8 解码后替换字符（U+FFFD）的比例上限，超过视为二进制/加密。</summary>
    private const int MaxReplacementPercent = 10;

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
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            if (!TryDecodeText(bytes, out var content))
            {
                _logger.LogWarning("拒绝读取非文本文件：{Path}", fullPath);
                return ToolResult.Error(Text("Tool_ReadFile_Msg_Binary"));
            }

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
