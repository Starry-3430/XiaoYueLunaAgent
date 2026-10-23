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
    /// <summary>按当前用户输入相关性检索后，注入系统提示词的长期事实条数上限。</summary>
    private const int MaxRelevantFacts = 5;

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

    /// <summary>
    /// 按请求参数拼装最终的系统提示词；不需要时返回空字符串。
    /// <paramref name="queryText"/> 为当前用户输入，用于相关性检索长期事实。
    /// </summary>
    public async Task<string> BuildAsync(AiRequestOptions options, string? queryText = null,
        CancellationToken ct = default)
    {
        if (!options.IncludeSystemPrompt)
            return string.Empty;

        // 覆盖提示词（如轮次摘要）自带完整指令，不追加系统规范、用户目录、长期事实与语言偏好
        if (options.SystemPrompt is not null)
            return ReplacePlaceholders(options.SystemPrompt);

        var personality = ReplacePlaceholders(_settings.SystemPrompt ?? string.Empty);

        // 系统规范：始终在人格提示词之后强制注入
        var rules = ReplacePlaceholders(DefaultPrompts.SystemRules);
        var prompt = string.IsNullOrWhiteSpace(personality) ? rules : personality + "\n\n" + rules;

        // 完全访问模式：追加能力说明，但不放宽防提示词注入等底层纪律。
        if (_settings.FullAccessEnabled)
            prompt += "\n\n" + DefaultPrompts.FullAccessRules;

        // 注入当前用户的目录信息（用户名、桌面、文档等），让 AI 知道可以搜索/读取的位置
        if (options.IncludeUserEnvironment)
            prompt += "\n\n" + UserEnvironment.BuildPromptSection(_settings.ResponseLanguage);

        var factsSection = await BuildFactsSectionAsync(queryText, ct);
        if (!string.IsNullOrEmpty(factsSection))
            prompt += "\n\n" + factsSection;

        prompt += DefaultPrompts.ResponseLanguageDirective(_settings.ResponseLanguage);

        return prompt;
    }

    private string ReplacePlaceholders(string text) => text
        .Replace("{nickname}", _settings.Nickname)
        .Replace("{Nickname}", _settings.Nickname)
        .Replace("{user}", _settings.UserName)
        .Replace("{User}", _settings.UserName);

    /// <summary>
    /// 从当前用户输入检索最相关的长期事实并拼成【关于用户】段落。
    /// 关键词检索无结果时由仓储层回退到最近更新的事实；读取失败时返回空字符串。
    /// </summary>
    private async Task<string> BuildFactsSectionAsync(string? queryText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(queryText))
            return string.Empty;

        List<Fact> facts;
        try
        {
            // 关键词相关性检索；无匹配时仓储层回退到最近更新的 5 条。
            facts = await _facts.SearchRelevantAsync(queryText, MaxRelevantFacts);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检索长期事实失败，跳过 Facts 注入");
            return string.Empty;
        }

        if (facts.Count == 0)
            return string.Empty;

        // 单条 Value 截断；累计 token 超限时从末尾丢弃
        var selected = new List<string>();
        var totalTokens = 0;
        foreach (var fact in facts)
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
        sb.AppendLine("以下是从以往对话中检索到的、与当前话题相关的长期事实，回答时可直接参考，无需再调用工具查询：");
        foreach (var line in selected)
            sb.AppendLine(line);

        _logger.LogInformation("已向系统提示词注入 {Count} 条相关长期事实", selected.Count);
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>粗略估算 token 数：按字符数计（对中文偏保守）。</summary>
    private static int EstimateTokens(string text) => text.Length;
}
