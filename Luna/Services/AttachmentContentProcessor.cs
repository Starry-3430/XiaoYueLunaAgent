using System.Text;

namespace Luna.Services;

/// <summary>
/// 附件内容的 Token 预算与智能截断。
/// Token 数采用估算（中文按 1.5 字/token、其余按 4 字符/token），不追求精确，仅用于判断“是否过大”。
/// </summary>
public static class AttachmentContentProcessor
{
    /// <summary>单个附件的最大 Token 预算。</summary>
    public const int DefaultMaxTokens = 4000;

    /// <summary>估算文本的 Token 数（中文与其它字符分开计算）。</summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var chinese = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) chinese++;
            else other++;
        }
        return (int)(chinese / 1.5 + other / 4.0);
    }

    /// <summary>内容是否超过 Token 预算。</summary>
    public static bool IsOverLimit(string? markdown, int maxTokens = DefaultMaxTokens)
        => EstimateTokens(markdown) > maxTokens;

    /// <summary>
    /// 智能截断：优先保留标题、表格（表头 + 少量数据行）与关键段落，其余按预算截断，
    /// 末尾附加截断说明。用于在超长文档时节省 Token。
    /// </summary>
    public static string Truncate(string? markdown, int maxTokens = DefaultMaxTokens)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;
        if (EstimateTokens(markdown) <= maxTokens) return markdown;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        var priority = new List<string>();   // 标题 + 表格摘要（保持原顺序）
        var paragraphs = new List<string>(); // 普通段落
        var currentPara = new StringBuilder();
        var inTable = false;
        var tableRows = 0;

        void FlushParagraph()
        {
            if (currentPara.Length == 0) return;
            paragraphs.Add(currentPara.ToString().Trim());
            currentPara.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                inTable = false;
                tableRows = 0;
                priority.Add(line);
            }
            else if (line.Contains('|'))
            {
                FlushParagraph();
                if (!inTable)
                {
                    inTable = true;
                    tableRows = 0;
                }
                tableRows++;
                // 表格只保留表头与少量数据行，避免超长表格占满预算
                if (tableRows <= 8) priority.Add(line);
            }
            else
            {
                inTable = false;
                if (string.IsNullOrWhiteSpace(line))
                    FlushParagraph();
                else
                    currentPara.AppendLine(line);
            }
        }
        FlushParagraph();

        const string notice = "> （内容较长，已智能截断，仅保留标题、表格与关键段落。如需完整内容，可选择继续导入整个文件。）";
        var reserve = EstimateTokens(notice) + 2;
        var bodyLimit = Math.Max(1, maxTokens - reserve);

        var sb = new StringBuilder();

        // 先在主体预算的 85% 内保留标题与表格摘要
        var priorityLimit = Math.Max(1, (int)(bodyLimit * 0.85));
        foreach (var line in priority)
        {
            var candidate = sb.ToString() + line + "\n";
            if (EstimateTokens(candidate) > priorityLimit) break;
            sb.AppendLine(line);
        }

        // 在剩余预算内按顺序补充段落
        foreach (var paragraph in paragraphs)
        {
            var candidate = sb.ToString() + "\n" + paragraph + "\n";
            if (EstimateTokens(candidate) > bodyLimit) break;
            sb.AppendLine();
            sb.AppendLine(paragraph);
        }

        sb.AppendLine();
        sb.AppendLine(notice);
        return sb.ToString().TrimEnd();
    }
}
