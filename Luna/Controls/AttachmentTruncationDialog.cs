using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Luna.Models;

namespace Luna.Controls;

/// <summary>附件导入方式。</summary>
public enum AttachmentImportMode
{
    /// <summary>智能截断（仅保留标题、关键段落与表格摘要）。</summary>
    SmartTruncate,

    /// <summary>继续导入整个文件。</summary>
    KeepFull,
}

/// <summary>文档过长时的确认弹窗。</summary>
public static class AttachmentTruncationDialog
{
    /// <summary>弹出“文档过长”确认框，返回用户选择的导入方式（默认智能截断）。</summary>
    public static AttachmentImportMode Show(Window? owner)
    {
        var result = AttachmentImportMode.SmartTruncate;

        var dialog = new LunaDialog
        {
            DialogTitle = "文档过长",
            DialogContent = new TextBlock
            {
                Text = "文档内容过大，是否采用智能截断策略？这有助于节省Tokens，你也可以选择继续导入整个文件。",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "智能截断",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    result = AttachmentImportMode.SmartTruncate;
                    dialog.Close();
                }),
            },
            new()
            {
                Text = "继续导入",
                StyleKey = "StyleDark",
                Command = new RelayCommand(() =>
                {
                    result = AttachmentImportMode.KeepFull;
                    dialog.Close();
                }),
            },
        };

        if (owner is not null)
            dialog.Owner = owner;

        dialog.ShowDialog();
        return result;
    }
}
