// 工具：忘记一条已记录的长期事实。
using System.Text.Json;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;
using static Luna.Services.Tools.ToolLocalization;

namespace Luna.Services.Tools;

/// <summary>删除一条长期事实。归“长期事实”开关（long_term_facts）。</summary>
public class ForgetFactTool : ITool
{
    private const string SchemaTemplate =
        """{"type":"object","properties":{"key":{"type":"string","description":"$key$"}},"required":["key"]}""";

    private readonly FactRepository _repo;
    private readonly ILogger<ForgetFactTool> _logger;

    public string Name => "forget_fact";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => ToolSchema.Parse(SchemaTemplate.Replace("$key$", Param("Tool_ForgetFact_Param_Key")));
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
            return ToolResult.Error(Text("Tool_Msg_MissingParam", "key"));

        key = key.Trim();

        try
        {
            var removed = await _repo.DeleteAsync(key);
            if (removed)
            {
                _logger.LogInformation("已忘记事实");
                return ToolResult.Ok(Text("Tool_ForgetFact_Msg_Done", key));
            }

            return ToolResult.Ok(Text("Tool_ForgetFact_Msg_NotFound", key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "忘记事实失败");
            return ToolResult.Error(Text("Tool_ForgetFact_Msg_Failed", ex.Message));
        }
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
            return v.GetString()?.Trim() ?? string.Empty;
        return string.Empty;
    }
}
