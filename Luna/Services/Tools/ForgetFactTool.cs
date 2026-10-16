// 工具：忘记一条已记录的长期事实。
using System.Text.Json;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

/// <summary>删除一条长期事实。归“长期事实”开关（long_term_facts）。</summary>
public class ForgetFactTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"key":{"type":"string","description":"要忘记的事实标识（key）"}},"required":["key"]}""");

    private readonly FactRepository _repo;
    private readonly ILogger<ForgetFactTool> _logger;

    public string Name => "forget_fact";
    public string DisplayName => "忘记事实";
    public string Description => "忘记（删除）一条已记录的长期事实，需要提供该事实的 key。";
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.Low;
    public string SettingsKey => "long_term_facts";

    public ForgetFactTool(FactRepository repo, ILogger<ForgetFactTool> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var key = GetString(args, "key");
        if (string.IsNullOrWhiteSpace(key))
            return ToolResult.Error("缺少参数 key。");

        key = key.Trim();

        try
        {
            var removed = await _repo.DeleteAsync(key);
            if (removed)
            {
                _logger.LogInformation("已忘记事实：{Key}", key);
                return ToolResult.Ok($"已忘记：{key}");
            }

            return ToolResult.Ok($"没有找到 key 为「{key}」的事实。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "忘记事实失败：{Key}", key);
            return ToolResult.Error($"忘记事实失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
