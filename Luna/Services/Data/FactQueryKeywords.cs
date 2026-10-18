using System.Text.RegularExpressions;

namespace Luna.Services.Data;

/// <summary>
/// 从用户输入中提炼长期事实检索关键词：按空白/标点切分，剔除中英文停用词；
/// 中文长句生成 2 字片段（LIKE 兜底）与 3 字片段（FTS5 trigram）。
/// </summary>
internal static class FactQueryKeywords
{
    /// <summary>单次查询最多使用的关键词数量。</summary>
    public const int MaxKeywords = 16;

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
        "不是", "一个"
    };

    /// <summary>中文单字虚词：从连续中文片段中剔除，减少无意义关键词。</summary>
    private static readonly HashSet<char> ChineseStopChars = new(
        "的了吗呢吧啊哦嗯呀嘛是在有和与就都也还又很太更最把被让给对从到为以及等这那我你他她它们个些请帮想要会能可".ToCharArray());

    /// <summary>提取用于检索的关键词；无法提炼时返回空列表。</summary>
    public static List<string> Extract(string? text)
    {
        var keywords = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return keywords;

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
}
