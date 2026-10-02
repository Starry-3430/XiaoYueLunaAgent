using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;

namespace Luna.Views;

public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is GeneralSettingsViewModel vm)
                ProxyPasswordBox.Password = vm.ProxyPassword;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is GeneralSettingsViewModel vm && ProxyPasswordBox.Password != vm.ProxyPassword)
                ProxyPasswordBox.Password = vm.ProxyPassword;
        };
    }

    private void ProxyPassword_PasswordChanged(object sender, RoutedEventArgs e) => OnProxyPasswordChanged();

    private void OnProxyPasswordChanged()
    {
        if (DataContext is GeneralSettingsViewModel vm && vm.ProxyPassword != ProxyPasswordBox.Password)
            vm.ProxyPassword = ProxyPasswordBox.Password;
    }

    /// <summary>点击下拉框以外区域时，关闭弹出层。</summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;

        // 点击弹层内部（如滚动条）时不关闭
        if (IsInsidePopup(e.OriginalSource as DependencyObject)) return;

        vm.ProxyTypeSetting.IsOpen = false;
        vm.FontSetting.IsOpen = false;
    }

    private static bool IsInsidePopup(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.Popup) return true;
            source = LogicalTreeHelper.GetParent(source) ?? VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void SelectToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SelectSetting setting) return;
        e.Handled = true;
        setting.ToggleOpenCommand.Execute(null);
    }

    private void SelectOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SelectOption option) return;
        e.Handled = true;

        if (FindAncestorItemsControl(fe)?.Tag is not SelectSetting setting) return;

        setting.SelectOptionCommand.Execute(option);

        if (DataContext is not GeneralSettingsViewModel vm) return;
        if (setting == vm.FontSetting)
        {
            vm.ApplyFontSetting();
            ShowFontReminder();
        }
        else if (setting == vm.ProxyTypeSetting)
        {
            vm.ApplyProxyType();
        }
    }

    /// <summary>字体修改后提示用户保存并重启。</summary>
    private void ShowFontReminder()
    {
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = "提示",
            DialogContent = new TextBlock
            {
                Text = "修改字体后，部分界面需要保存设置配置并重启后才能生效",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "确定",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    private static ItemsControl? FindAncestorItemsControl(DependencyObject? child)
    {
        while (child != null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is ItemsControl ic) return ic;
        }
        return null;
    }

    // ===== 快捷键录入 =====

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;
        vm.HotkeyText = string.Empty;
        HotkeyHint.Visibility = Visibility.Visible;
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotkeyHint.Visibility = Visibility.Collapsed;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        var text = HotkeyService.Format(Keyboard.Modifiers, key);
        vm.HotkeyText = text;
        HotkeyHint.Visibility = Visibility.Collapsed;
        HotkeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    // ===== 编辑 settings.json =====

    private void EditSettingsJson_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;

        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = "警告",
            DialogContent = new TextBlock
            {
                Text = "错误地修改配置文件可能导致配置加载出错，确定要打开配置文件吗？",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "取消",
                StyleKey = "StyleDefault",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = "继续",
                StyleKey = "StyleDanger",
                Command = new RelayCommand(() =>
                {
                    dialog.Close();
                    OpenSettingsFile(vm.SettingsPath);
                }),
            },
        };

        dialog.ShowDialog();
    }

    private static void OpenSettingsFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开配置文件：" + ex.Message, "Luna",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
