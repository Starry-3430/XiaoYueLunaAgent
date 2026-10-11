using System.Windows.Input;

namespace Luna.Models;

/// <summary>
/// 弹窗按钮模型（文本、命令、样式键、是否主/取消按钮）
/// </summary>
public class DialogButton
{
    public string Text { get; set; } = string.Empty;
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsCancel { get; set; }

    /// <summary>可选：直接指定 DialogButtonStyles.xaml 中的样式键，优先级最高。</summary>
    public string? StyleKey { get; set; }
}