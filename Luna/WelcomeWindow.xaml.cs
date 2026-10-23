using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;

namespace Luna;

public partial class WelcomeWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly WelcomeViewModel _viewModel;

    public bool Completed { get; private set; }

    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.RequestClose += OnRequestClose;
        _viewModel.StepChanged += AnimateStep;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };

        Loaded += OnWindowLoaded;
        Deactivated += (_, _) => CloseDropdowns();

        Closed += (_, _) =>
        {
            _viewModel.RequestClose -= OnRequestClose;
            _viewModel.StepChanged -= AnimateStep;
        };
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = _viewModel.ApiKey;
        ProxyPasswordBox.Password = _viewModel.ProxyPassword;

        AnimateStep();
    }

    private void OnRequestClose(bool completed)
    {
        Completed = completed;
        SystemCommands.CloseWindow(this); // 使用原生命令关闭
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;

        try { DragMove(); } catch { }
    }

    /// <summary>
    /// 使用 Windows 系统原生命令最小化，以获得完美的“缩放至任务栏”动画。
    /// </summary>
    private void MinButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    /// <summary>
    /// 使用 Windows 系统原生命令关闭，保留系统关闭动效。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    /// <summary>步骤切换：内容轻微上移淡入。</summary>
    private void AnimateStep()
    {
        if (!IsLoaded) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        StepTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        StepHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    /// <summary>API Key 输入变化时同步到视图模型。</summary>
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is WelcomeViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }

    /// <summary>代理密码输入变化时同步到视图模型。</summary>
    private void ProxyPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is WelcomeViewModel vm)
            vm.ProxyPassword = ProxyPasswordBox.Password;
    }

    /// <summary>一键把快捷键绑定到 Copilot 键（Win + Shift + F23）。</summary>
    private void BindCopilot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WelcomeViewModel vm) return;

        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = loc["Dialog_CopilotWarning_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_CopilotWarning_Body"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

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
                Text = loc["Dialog_Button_Continue"],
                StyleKey = "StyleDark",
                Command = new RelayCommand(() =>
                {
                    dialog.Close();
                    vm.HotkeyText = "Win + Shift + F23";
                }),
            },
        };

        dialog.ShowDialog();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WelcomeViewModel vm) return;
        vm.HotkeyText = string.Empty;
        HotkeyHint.Visibility = Visibility.Visible;
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotkeyHint.Visibility = Visibility.Collapsed;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WelcomeViewModel vm) return;

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

    /// <summary>点击下拉框以外区域时，关闭所有下拉弹出层。</summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e) => CloseDropdowns();

    private void CloseDropdowns()
    {
        if (DataContext is not WelcomeViewModel vm) return;
        vm.ProviderSetting.IsOpen = false;
        vm.ModelSetting.IsOpen = false;
        vm.ProxyTypeSetting.IsOpen = false;
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

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = child is Visual ? VisualTreeHelper.GetParent(child) : null;
        }
        return null;
    }
}
