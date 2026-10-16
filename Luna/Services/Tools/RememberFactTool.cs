// 工具：长期记住一条事实（键值对）。
using System.Text.Json;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>长期记住一条关于用户的事实。归“长期事实”开关（long_term_facts）。</summary>
public class RememberFactTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"key":{"type":"string","description":"$key$"},"value":{"type":"string","description":"$value$"}},"required":["key","value"]}""";

    /// <summary>敏感信息关键字：命中则拒绝写入（简单关键字匹配，不做复杂检测）。</summary>
    private static readonly string[] SensitiveKeywords =
        ["sk-", "password", "passwd", "-----BEGIN", "api_key"];

    private readonly FactRepository _repo;
    private readonly ILogger<RememberFactTool> _logger;

    public string Name => "remember_fact";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate
        .Replace("$key$", Param("Tool_RememberFact_Param_Key"))
        .Replace("$value$", Param("Tool_RememberFact_Param_Value")));
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
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "key"));
        if (string.IsNullOrWhiteSpace(value))
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "value"));

        // 只对 value 做敏感信息检查，key 不强制（中文 key 允许）。
        if (ContainsSensitive(value))
            return ToolResult.Error(Text("Tool_RememberFact_Msg_Sensitive"));

        key = key.Trim();
        value = value.Trim();

        try
        {
            await _repo.UpsertAsync(key, value, sourceTurnId: null);
            _logger.LogInformation("已记住事实：{Key}", key);
            return ToolResult.Ok(Text("Tool_RememberFact_Msg_Remembered", key, value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记住事实失败：{Key}", key);
            return ToolResult.Error(Text("Tool_RememberFact_Msg_Failed", ex.Message));
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
