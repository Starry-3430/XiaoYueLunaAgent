using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;

namespace Luna.Views;

/// <summary>
/// 通用设置页界面
/// </summary>
public partial class GeneralSettingsView : UserControl
{
    private Window? _hostWindow;

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
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.Deactivated -= HostWindow_Deactivated;
        _hostWindow = null;
    }

    private void HostWindow_Deactivated(object? sender, EventArgs e) => CloseDropdowns();

    private void CloseDropdowns()
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;
        vm.ProxyTypeSetting.IsOpen = false;
        vm.FontSetting.IsOpen = false;
        vm.LanguageSetting.IsOpen = false;
        vm.StartupSetting.IsOpen = false;
    }

    private void ProxyPassword_PasswordChanged(object sender, RoutedEventArgs e) => OnProxyPasswordChanged();

    private void ResetProxyPassword_Click(object sender, RoutedEventArgs e)
    {
        ProxyPasswordBox.Password = string.Empty;
    }

    private void OnProxyPasswordChanged()
    {
        if (DataContext is GeneralSettingsViewModel vm && vm.ProxyPassword != ProxyPasswordBox.Password)
            vm.ProxyPassword = ProxyPasswordBox.Password;
    }

    /// <summary>选择“输出目录”。</summary>
    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Instance["Dialog_ChooseFolder_Title"],
            Multiselect = false,
            InitialDirectory = Directory.Exists(vm.OutputDirectory) ? vm.OutputDirectory : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            vm.OutputDirectory = dialog.FolderName;
    }

    /// <summary>点击下拉框以外区域时，关闭弹出层。</summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击弹层内部（如滚动条）时不关闭
        if (IsInsidePopup(e.OriginalSource as DependencyObject)) return;

        CloseDropdowns();
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

    private void SelectOption_Click(object sender, MouseButtonEventArgs e)    {
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
        else if (setting == vm.LanguageSetting)
        {
            vm.ApplyLanguage();
            ShowLanguageReminder();
        }
        else if (setting == vm.StartupSetting)
        {
            HandleStartupSelected(vm);
        }
    }

    /// <summary>重置语言为默认值，并弹出与选择语言一致的提示。</summary>
    private void ResetLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;
        vm.ResetLanguageCommand.Execute(null);
        ShowLanguageReminder();
    }

    /// <summary>处理开机自启动的选择：管理员模式需要先确认并弹出 UAC 提权。</summary>
    private void HandleStartupSelected(GeneralSettingsViewModel vm)
    {
        var mode = vm.StartupSetting.SelectedOption?.Value ?? AiSettings.DefaultStartupMode;

        // 普通用户选择“是 + 管理员”：先确认，再尝试提权
        if (mode == "admin" && !StartupService.IsRunningAsAdministrator())
        {
            if (!ConfirmElevation())
            {
                vm.RevertStartupSelection();
                return;
            }

            var elevated = vm.ApplyStartupElevated();
            if (!elevated.Success)
            {
                vm.RevertStartupSelection();
                if (!elevated.Canceled) ShowStartupFailure(elevated.Message);
            }
            return;
        }

        var result = vm.ApplyStartup();
        if (!result.Success)
        {
            vm.RevertStartupSelection();
            ShowStartupFailure(result.Message);
        }
    }

    /// <summary>询问用户是否继续以管理员权限设置自启动。</summary>
    private bool ConfirmElevation()
    {
        var confirmed = false;
        var loc = LocalizationService.Instance;

        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = loc["Dialog_Elevation_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_Elevation_Body"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
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
                Text = loc["Dialog_Button_Yes"],
                StyleKey = "StyleBeige",
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

    /// <summary>开机自启动设置失败时的提示。</summary>
    private void ShowStartupFailure(string message)
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = loc["Dialog_StartupFailed_Title"],
            DialogContent = new TextBlock
            {
                Text = loc.Format("Dialog_StartupFailed_Body", message),
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
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    /// <summary>切换语言后提示用户可能需重启才能完全生效（弹窗使用切换后的语言）。</summary>
    private void ShowLanguageReminder()
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = loc["Dialog_LanguageReminder_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_LanguageReminder_Body"],
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
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    /// <summary>字体修改后提示用户保存并重启。</summary>
    private void ShowFontReminder()    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = loc["Dialog_FontReminder_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_FontReminder_Body"],
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
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
    }

    /// <summary>一键把快捷键绑定到 Copilot 键（Win + Shift + F23）。</summary>
    private void BindCopilot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralSettingsViewModel vm) return;

        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
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
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    dialog.Close();
                    vm.HotkeyText = "Win + Shift + F23";
                }),
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

        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = loc["Dialog_EditConfig_Title"],
            DialogContent = new TextBlock
            {
                Text = loc["Dialog_EditConfig_Body"],
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
                StyleKey = "StyleDangerSoft",
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
            MessageBox.Show(LocalizationService.Instance.Format("Settings_EditConfig_OpenFailed", ex.Message), "Luna",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
