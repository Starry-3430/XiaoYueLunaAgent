using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Luna.Controls;
using Luna.Models;
using Luna.ViewModels;

namespace Luna.Views;

/// <summary>
/// AI 连接设置页界面
/// </summary>
public partial class AiConnectionView : UserControl
{
    private Window? _hostWindow;

    public AiConnectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is AiConnectionViewModel vm)
                ApiKeyBox.Password = vm.ApiKey;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is AiConnectionViewModel vm && ApiKeyBox.Password != vm.ApiKey)
                ApiKeyBox.Password = vm.ApiKey;

            AttachHostWindow();
        };
        Unloaded += (_, _) => DetachHostWindow();
    }

    /// <summary>页面所在的窗口失去激活（如点击其它应用）时，关闭所有下拉弹出层。</summary>
    private void AttachHostWindow()
    {
        var window = Window.GetWindow(this);
        if (window is null || ReferenceEquals(_hostWindow, window)) return;

        DetachHostWindow();
        _hostWindow = window;
        _hostWindow.Deactivated += HostWindow_Deactivated;
        _hostWindow.PreviewMouseDown += HostWindow_PreviewMouseDown;
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.Deactivated -= HostWindow_Deactivated;
        _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
        _hostWindow = null;
    }

    private void HostWindow_Deactivated(object? sender, EventArgs e) => CloseDropdowns();

    private void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDropdownToggle(e.OriginalSource as DependencyObject)) return;
        CloseDropdowns();
    }

    private void CloseDropdowns()
    {
        if (DataContext is not AiConnectionViewModel vm) return;
        vm.ProviderSetting.IsOpen = false;
        vm.ModelSetting.IsOpen = false;
        vm.ResponseLanguageSetting.IsOpen = false;
    }

    /// <summary>API Key 输入变化时同步到视图模型。</summary>
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }

    /// <summary>按回车确认 API Key 后展示数据使用提示。</summary>
    private void ApiKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ShowPrivacyNoticeIfNeeded();
    }

    /// <summary>API Key 输入框失焦视为填写完毕，展示数据使用提示。</summary>
    private void ApiKey_LostFocus(object sender, RoutedEventArgs e) => ShowPrivacyNoticeIfNeeded();

    private void ShowPrivacyNoticeIfNeeded()
    {
        if (DataContext is not AiConnectionViewModel vm) return;
        if (!vm.TryBeginPrivacyNotice()) return;

        PrivacyNoticeDialog.Show(Window.GetWindow(this));
    }

    /// <summary>温度输入框失焦时，校验并夹紧取值范围。</summary>
    private void TemperatureBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.CommitTemperature();
    }

    private void TopPBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.CommitTopP();
    }

    private void FrequencyPenaltyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.CommitFrequencyPenalty();
    }

    private void PresencePenaltyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.CommitPresencePenalty();
    }

    /// <summary>点击下拉框以外区域时，关闭所有下拉弹出层。</summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDropdownToggle(e.OriginalSource as DependencyObject)) return;
        CloseDropdowns();
    }

    private static bool IsDropdownToggle(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: SelectSetting }) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>点击下拉触发框：切换其弹出层的展开状态。</summary>
    private void SelectToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SelectSetting setting) return;
        e.Handled = true;
        setting.ToggleOpenCommand.Execute(null);
    }

    /// <summary>点击下拉选项：执行选择命令。</summary>
    private void SelectOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SelectOption option) return;
        e.Handled = true;

        if (FindAncestorItemsControl(fe)?.Tag is SelectSetting setting)
            setting.SelectOptionCommand.Execute(option);
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
}
