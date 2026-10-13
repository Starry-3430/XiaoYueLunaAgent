using System.Windows;
using System.Windows.Controls;
using Luna.Models;

namespace Luna.Controls;

/// <summary>
/// 按角色与内容为每条消息选择模板，避免为空消息（只有工具调用）创建 MarkdownDocumentView：
/// user → 纯文本；assistant 且有正文（或正在流式输出）→ Markdown + 思考 + 工具卡片；
/// assistant 仅工具调用 → 只有思考 + 工具卡片；system → 附件摘要；tool 等 → 空模板。
/// 说明：模板选择器只在容器生成时求值一次，因此 assistant 的判定必须包含 IsStreaming，
/// 否则流式消息在正文到达前会被定型到“无 Markdown”模板而永远不显示正文。
/// </summary>
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AssistantTemplate { get; set; }

    public DataTemplate? AssistantToolOnlyTemplate { get; set; }

    public DataTemplate? SystemTemplate { get; set; }

    public DataTemplate? EmptyTemplate { get; set; }

    private static readonly DataTemplate Blank = new();

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ChatMessage message)
            return EmptyTemplate ?? Blank;

        return message.Role switch
        {
            "user" => UserTemplate ?? Blank,
            "assistant" => message.HasContent || message.IsStreaming
                ? AssistantTemplate ?? Blank
                : AssistantToolOnlyTemplate ?? Blank,
            "system" => SystemTemplate ?? Blank,
            _ => EmptyTemplate ?? Blank,
        };
    }
}
