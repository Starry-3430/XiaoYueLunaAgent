// 工具：长期记住一条事实（键值对）。
using System.Text.Json;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>长期记住一条关于用户的事实。归“长期事实”开关（long_term_facts）。</summary>
public class RememberFactTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"key":{"type":"string","description":"英文短语 + 下划线的事实标识，如 preferred_language、editor_theme"},"value":{"type":"string","description":"用中文自然语言描述的事实内容"}},"required":["key","value"]}""");

    /// <summary>敏感信息关键字：命中则拒绝写入（简单关键字匹配，不做复杂检测）。</summary>
    private static readonly string[] SensitiveKeywords =
        ["sk-", "password", "passwd", "-----BEGIN", "api_key"];

    private readonly FactRepository _repo;
    private readonly ILogger<RememberFactTool> _logger;

    public string Name => "remember_fact";
    public string DisplayName => "记住事实";
    public string Description =>
        "长期记住一条关于用户的事实，供以后回忆。key 必须用英文短语加下划线（如 preferred_language、editor_theme），" +
        "value 用中文自然描述。不要记录密码、API Key 等敏感信息。";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "long_term_facts";

    public RememberFactTool(FactRepository repo, ILogger<RememberFactTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var key = GetString(args, "key");
        var value = GetString(args, "value");
        if (string.IsNullOrWhiteSpace(key))
            return ToolResult.Error("缺少参数 key。");
        if (string.IsNullOrWhiteSpace(value))
            return ToolResult.Error("缺少参数 value。");

        // 只对 value 做敏感信息检查，key 不强制（中文 key 允许）。
        if (ContainsSensitive(value))
            return ToolResult.Error("检测到疑似敏感信息（密码、密钥等），已拒绝记录。");

        key = key.Trim();
        value = value.Trim();

        try
        {
            await _repo.UpsertAsync(key, value, sourceTurnId: null);
            _logger.LogInformation("已记住事实：{Key}", key);
            return ToolResult.Ok($"已记住：{key} = {value}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记住事实失败：{Key}", key);
            return ToolResult.Error($"记住事实失败：{ex.Message}");
        }
    }

    private static bool ContainsSensitive(string value)
        => SensitiveKeywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
