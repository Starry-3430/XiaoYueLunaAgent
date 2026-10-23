using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Luna.Localization;
using Luna.Models;

namespace Luna.Controls;

/// <summary>完全访问模式的启用 / 关闭确认流程。</summary>
public static class FullAccessDialogs
{
    /// <summary>三步确认后返回是否允许启用完全访问。</summary>
    public static bool ConfirmEnable(Window? owner)
    {
        if (!ShowFirstStep(owner)) return false;
        if (!ShowSecondStep(owner)) return false;
        return ShowFinalStep(owner);
    }

    /// <summary>完全访问启用成功后提示即将重启（调用方负责实际重启）。</summary>
    public static void ShowEnabledRestart(Window? owner)
    {
        var loc = LocalizationService.Instance;

        var dialog = new LunaDialog
        {
            Owner = ResolveOwner(owner),
            DialogTitle = loc["Settings_FullAccess_EnabledTitle"],
            DialogContent = CreateBody(loc["Settings_FullAccess_EnabledBody"]),
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Settings_FullAccess_EnabledRestart"],
                StyleKey = "StyleDangerSevere",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    /// <summary>确认是否关闭完全访问（确认后调用方负责重启）。</summary>
    public static bool ConfirmDisable(Window? owner)
    {
        var loc = LocalizationService.Instance;
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = ResolveOwner(owner),
            DialogTitle = loc["Settings_FullAccess_DisableTitle"],
            DialogContent = CreateBody(loc["Settings_FullAccess_DisableBody"]),
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_No"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = loc["Settings_FullAccess_DisableRestart"],
                StyleKey = "StyleDangerSevere",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
        };

        dialog.ShowDialog();
        return confirmed;
    }

    private static bool ShowFirstStep(Window? owner)
    {
        var loc = LocalizationService.Instance;
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = ResolveOwner(owner),
            DialogTitle = loc["Settings_FullAccess_Title1"],
            DialogContent = CreateBody(loc["Settings_FullAccess_Body1"]),
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_Cancel"],
                StyleKey = "StyleDark",
                IsPrimary = true,
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = loc["Settings_FullAccess_ContinueNext"],
                StyleKey = "StyleDanger",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
        };

        dialog.ShowDialog();
        return confirmed;
    }

    private static bool ShowSecondStep(Window? owner)
    {
        var loc = LocalizationService.Instance;
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = ResolveOwner(owner),
            DialogTitle = loc["Settings_FullAccess_Title2"],
            DialogContent = CreateBody(loc["Settings_FullAccess_Body2"]),
        };

        // 故意把“继续”放在左侧，改变按钮顺序，避免用户凭习惯连续点击。
        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Settings_FullAccess_Acknowledge"],
                StyleKey = "StyleDanger",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
            new()
            {
                Text = loc["Settings_FullAccess_Abandon"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
        return confirmed;
    }

    private static bool ShowFinalStep(Window? owner)
    {
        var loc = LocalizationService.Instance;
        var keyword = loc["Settings_FullAccess_ConfirmKeyword"];
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = ResolveOwner(owner),
            DialogTitle = loc["Settings_FullAccess_Title3"],
        };

        var textBox = new TextBox
        {
            Style = dialog.FindResource("DialogInput") as Style,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(CreateBodyText(loc.Format("Settings_FullAccess_Body3", keyword)));
        panel.Children.Add(textBox);
        dialog.DialogContent = panel;

        RelayCommand? confirmCommand = null;
        confirmCommand = new RelayCommand(
            () =>
            {
                confirmed = true;
                dialog.Close();
            },
            () => string.Equals(textBox.Text, keyword, StringComparison.Ordinal));

        textBox.TextChanged += (_, _) => confirmCommand?.NotifyCanExecuteChanged();

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_Cancel"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = loc["Settings_FullAccess_EnableConfirm"],
                StyleKey = "StyleDangerSevere",
                Command = confirmCommand,
            },
        };

        dialog.ShowDialog();
        return confirmed;
    }

    private static TextBlock CreateBody(string text) => CreateBodyText(text);

    private static TextBlock CreateBodyText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Left,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Window? ResolveOwner(Window? owner)
    {
        if (owner is not null) return owner;

        if (Application.Current is App app)
            return app.EnsureDialogOwner();

        return Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible);
    }
}
