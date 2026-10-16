using System.Text;
using Luna.Models;
using Luna.Services.Data;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 构造系统提示词：基础提示词（含占位符替换）+ 用户环境 + 长期事实 + 语言偏好
/// 从 <see cref="OpenAiService"/> 抽出，便于独立维护与注入 Facts
/// </summary>
public class SystemPromptBuilder
{
    /// <summary>注入系统提示词的长期事实条数上限。</summary>
    private const int MaxFacts = 20;

    /// <summary>单条事实 Value 的最大字数。</summary>
    private const int MaxFactValueLength = 100;

    /// <summary>【关于用户】段落的 token 上限（粗略按字符数估算，超限则从末尾丢弃）。</summary>
    private const int MaxFactsTokens = 2000;

    private readonly AiSettings _settings;
    private readonly FactRepository _facts;
    private readonly ILogger<SystemPromptBuilder> _logger;

    public SystemPromptBuilder(AiSettings settings, FactRepository facts,
        ILogger<SystemPromptBuilder> logger)
    {
        _settings = settings;
        _facts = facts;
        _logger = logger;
    }

    /// <summary>按请求参数拼装最终的系统提示词；不需要时返回空字符串。</summary>
    public async Task<string> BuildAsync(AiRequestOptions options, CancellationToken ct = default)
    {
        if (!options.IncludeSystemPrompt)
            return string.Empty;

        var useOverride = options.SystemPrompt is not null;
        var prompt = options.SystemPrompt ?? _settings.SystemPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
            return string.Empty;

        prompt = prompt
            .Replace("{nickname}", _settings.Nickname)
            .Replace("{Nickname}", _settings.Nickname)
            .Replace("{user}", _settings.UserName)
            .Replace("{User}", _settings.UserName);

        // 覆盖提示词（如轮次摘要）自带完整指令，不追加用户目录、长期事实与语言偏好
        if (useOverride)
            return prompt;

        // 注入当前用户的目录信息（用户名、桌面、文档等），让 AI 知道可以搜索/读取的位置
        if (options.IncludeUserEnvironment)
            prompt += "\n\n" + UserEnvironment.BuildPromptSection();

        var factsSection = await BuildFactsSectionAsync(ct);
        if (!string.IsNullOrEmpty(factsSection))
            prompt += "\n\n" + factsSection;

        prompt += _settings.ResponseLanguage switch
        {
            "zh" => "\n\n请始终使用简体中文回复。",
            "en" => "\n\nAlways reply in English.",
            _ => string.Empty,
        };

        return prompt;
    }

    /// <summary>把长期事实拼成【关于用户】段落；没有事实或读取失败时返回空字符串。</summary>
    private async Task<string> BuildFactsSectionAsync(CancellationToken ct)
    {
        List<Fact> facts;
        try
        {
            // 已按 UpdatedAtUtc 倒序
            facts = await _facts.GetAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取长期事实失败，跳过 Facts 注入");
            return string.Empty;
        }

        if (facts.Count == 0)
            return string.Empty;

        // 最多 20 条，单条 Value 截断；累计 token 超限时从末尾丢弃
        var selected = new List<string>();
        var totalTokens = 0;
        foreach (var fact in facts.Take(MaxFacts))
        {
            var line = $"- {fact.Key}：{Truncate(fact.Value, MaxFactValueLength)}";
            var lineTokens = EstimateTokens(line);
            if (totalTokens + lineTokens > MaxFactsTokens)
                break;
            selected.Add(line);
            totalTokens += lineTokens;
        }

        if (selected.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("【关于用户】");
        sb.AppendLine("以下是从以往对话中记住的关于用户的长期事实，回答时可直接参考，无需再调用工具查询：");
        foreach (var line in selected)
            sb.AppendLine(line);

        _logger.LogInformation("已向系统提示词注入 {Count} 条长期事实", selected.Count);
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>粗略估算 token 数：按字符数计（对中文偏保守）。</summary>
    private static int EstimateTokens(string text) => text.Length;
}
