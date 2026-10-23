// API Key 填写完成后的数据使用提示弹窗

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Luna.Localization;
using Luna.Models;

namespace Luna.Controls;

/// <summary>告知用户对话、附件、日记等数据会发送至其配置的 AI 提供商。</summary>
public static class PrivacyNoticeDialog
{
    public static void Show(Window? owner)
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            DialogTitle = loc["Dialog_DataSharing_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_DataSharing_Body"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_OK"],
                StyleKey = "StyleDark",
                Command = new RelayCommand(dialog.Close),
            },
        };

        if (owner is not null)
            dialog.Owner = owner;

        dialog.ShowDialog();
    }
}
