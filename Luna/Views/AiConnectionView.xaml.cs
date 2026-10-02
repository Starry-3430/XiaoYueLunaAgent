using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Luna.Models;
using Luna.ViewModels;

namespace Luna.Views;

public partial class AiConnectionView : UserControl
{
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
        };
    }

    /// <summary>API Key 输入变化时同步到视图模型。</summary>
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }

    /// <summary>温度输入框失焦时，校验并夹紧取值范围。</summary>
    private void TemperatureBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiConnectionViewModel vm)
            vm.CommitTemperature();
    }

    /// <summary>点击下拉框以外区域时，关闭所有下拉弹出层。</summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AiConnectionViewModel vm) return;
        vm.ProviderSetting.IsOpen = false;
        vm.ModelSetting.IsOpen = false;
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
