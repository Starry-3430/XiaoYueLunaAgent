using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>单次查询最多使用的关键词数量。</summary>
    private const int MaxKeywords = 16;

    /// <summary>无需参与事实检索的常见虚词 / 客套词。</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "else",
        "is", "are", "was", "were", "be", "been", "being",
        "to", "of", "in", "on", "at", "for", "with", "about", "from", "by", "as",
        "that", "this", "these", "those",
        "i", "you", "he", "she", "it", "we", "they",
        "me", "my", "your", "his", "her", "its", "our", "their",
        "do", "does", "did", "can", "could", "would", "should", "will", "shall",
        "may", "might", "must", "what", "how", "why", "when", "where", "who", "which",
        "please", "tell", "help",
        "什么", "怎么", "为什么", "如何", "可以", "能否", "帮我", "我想", "我的", "你的",
        "我们", "你们", "他们", "这个", "那个", "一下", "一些", "现在", "今天", "明天",
        "昨天", "然后", "因为", "所以", "但是", "如果", "已经", "还是", "就是", "没有",
        "不是", "一个",
    };

    /// <summary>中文单字虚词：从连续中文片段中剔除，减少无意义关键词。</summary>
    private static readonly HashSet<char> ChineseStopChars = new(
        "的了吗呢吧啊哦嗯呀嘛是在有和与就都也还又很太更最把被让给对从到为以及等这那我你他她它们个些请帮想要会能可".ToCharArray());

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
    /// 从当前用户输入中提炼关键词，检索最相关的长期事实并拼成【关于用户】段落。
    /// 没有关键词、没有匹配或读取失败时返回空字符串（不再无差别注入全部事实）。
    /// </summary>
    private async Task<string> BuildFactsSectionAsync(string? queryText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(queryText))
            return string.Empty;

        var keywords = ExtractKeywords(queryText);
        if (keywords.Count == 0)
            return string.Empty;

        List<Fact> facts;
        try
        {
            facts = await _facts.SearchRelevantAsync(keywords, MaxRelevantFacts);
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

        _logger.LogInformation("已向系统提示词注入 {Count} 条相关长期事实（关键词：{Keywords}）",
            selected.Count, string.Join(", ", keywords));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 从用户输入提炼检索关键词：
    /// 按空白/标点切分，剔除中英文停用词；中文长句生成 2 字片段（LIKE 兜底）与 3 字片段（FTS trigram）。
    /// </summary>
    private static List<string> ExtractKeywords(string text)
    {
        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (keywords.Count >= MaxKeywords || string.IsNullOrWhiteSpace(value))
                return;

            value = value.Trim();
            if (value.Length < 2) return;
            if (StopWords.Contains(value)) return;
            if (!value.Any(char.IsLetter)) return; // 跳过纯数字/符号
            if (seen.Add(value)) keywords.Add(value);
        }

        foreach (var segment in Regex.Split(text, @"[\s\p{P}\p{S}]+"))
        {
            if (string.IsNullOrWhiteSpace(segment) || StopWords.Contains(segment))
                continue;

            if (!segment.Any(IsCjk))
            {
                Add(segment.ToLowerInvariant());
                continue;
            }

            // 中文片段剔除单字虚词，避免“的/了/我/是”等制造噪声。
            var filtered = new string(segment
                .Where(c => !IsCjk(c) || !ChineseStopChars.Contains(c))
                .ToArray())
                .Trim();

            if (filtered.Length == 0)
                continue;

            if (filtered.Length <= 4)
            {
                Add(filtered);

                // 3~4 字片段额外拆出 2 字词，避免“生日哪”这类输入漏掉“生日”。
                if (filtered.Length >= 3)
                {
                    for (var i = 0; i + 1 < filtered.Length; i++)
                        Add(filtered.Substring(i, 2));
                }

                continue;
            }

            // 长中文句：2 字片段保证“喜欢/生日/颜色”等常用词可命中；
            // 3 字片段交给 FTS5 trigram。
            for (var i = 0; i + 1 < filtered.Length; i++)
                Add(filtered.Substring(i, 2));
            for (var i = 0; i + 2 < filtered.Length; i++)
                Add(filtered.Substring(i, 3));
        }

        return keywords;
    }

    private static bool IsCjk(char c)
        => c is >= '\u4E00' and <= '\u9FFF'   // 基本汉字
            or >= '\u3400' and <= '\u4DBF'   // 扩展 A
            or >= '\u3040' and <= '\u30FF';  // 日文假名（兼容混排）

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>粗略估算 token 数：按字符数计（对中文偏保守）。</summary>
    private static int EstimateTokens(string text) => text.Length;
}
