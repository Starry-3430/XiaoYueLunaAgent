using System.Windows;
using System.Windows.Media;
using Luna.Models;

namespace Luna.Controls;

/// <summary>
/// 流式渲染兜底：界面切走再切回时，强制把仍在流式输出的消息重新写回 Markdown 视图，
/// 避免因视图隐藏/暂停导致“文本卡住不动”。
/// </summary>
public static class MarkdownViewerRefresher
{
    /// <summary>刷新所有“正在流式输出”的消息视图。</summary>
    public static void RefreshStreaming(DependencyObject root)
    {
        foreach (var viewer in FindDescendants<WpfMarkdownViewer.Controls.MarkdownDocumentView>(root))
        {
            if (viewer.DataContext is not ChatMessage { IsStreaming: true } msg)
                continue;

            // 保持“流式中”状态：整体重置后再追加，避免 SetMarkdown 把流式标记为完成
            viewer.Reset();
            if (!string.IsNullOrEmpty(msg.Content))
                viewer.AppendDelta(msg.Content);
        }
    }

    public static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }
}
