using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Luna.Controls;
using Luna.Models;
using Luna.Localization;
using Luna.Services;

namespace Luna.ViewModels;

/// <summary>“设置 - 通用”页的视图模型：字体、快捷键、代理。</summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly HotkeyService _hotkeyService;
    private readonly StartupService _startupService;
    private readonly ToolPermissionService _toolPermission;
    private readonly DataExportService _exportService;
    private readonly DataImportService _importService;
    private readonly ILogger<GeneralSettingsViewModel> _logger;
    private bool _loading;

    public SelectSetting FontSetting { get; } = new();
    public SelectSetting ProxyTypeSetting { get; } = new();
    public SelectSetting LanguageSetting { get; } = new();
    public SelectSetting StartupSetting { get; } = new();

    public string SettingsPath => _settingsService.SettingsPath;

    [ObservableProperty]
    private string _hotkeyText = string.Empty;

    /// <summary>录制框展示文本：绑定 Copilot 时显示“Copilot”，否则显示实际组合键。</summary>
    public string HotkeyDisplay => HotkeyService.IsCopilotChord(HotkeyText) ? "Copilot" : HotkeyText;

    [ObservableProperty]
    private string _proxyServer = string.Empty;

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private string _proxyUsername = string.Empty;

    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _proxyTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingProxy;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private string _toolPermissionStatus = string.Empty;

    [ObservableProperty]
    private string _backupStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportDataCommand))]
    private bool _isExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportDataCommand))]
    private bool _isImporting;

    private readonly DispatcherTimer _saveStatusTimer;

    public GeneralSettingsViewModel(AiSettings settings, SettingsService settingsService,
        HotkeyService hotkeyService, StartupService startupService,
        ToolPermissionService toolPermission, DataExportService exportService,
        DataImportService importService, ILogger<GeneralSettingsViewModel> logger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _hotkeyService = hotkeyService;
        _startupService = startupService;
        _toolPermission = toolPermission;
        _exportService = exportService;
        _importService = importService;
        _logger = logger;

        _saveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _saveStatusTimer.Tick += (_, _) =>
        {
            _saveStatusTimer.Stop();
            SaveStatus = string.Empty;
        };

        FontSetting.Options.Add(new SelectOption { Value = string.Empty, Label = LocalizationService.Instance["Settings_Font_DefaultOption"] });
        foreach (var source in System.Windows.Media.Fonts.SystemFontFamilies
                     .Select(f => f.Source)
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct()
                     .OrderBy(s => s, StringComparer.CurrentCulture))
        {
            FontSetting.Options.Add(new SelectOption { Value = source, Label = source });
        }

        if (!string.IsNullOrEmpty(_settings.FontFamily)
            && FontSetting.Options.All(o => o.Value != _settings.FontFamily))
        {
            FontSetting.Options.Insert(1, new SelectOption { Value = _settings.FontFamily, Label = _settings.FontFamily });
        }

        ProxyTypeSetting.Options.Add(new SelectOption { Value = "none", Label = LocalizationService.Instance["Settings_Proxy_None"] });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "http", Label = "HTTP" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks4", Label = "SOCKS4" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks5", Label = "SOCKS5" });

        LanguageSetting.Options.Add(new SelectOption { Value = "zh", Label = "简体中文" });
        LanguageSetting.Options.Add(new SelectOption { Value = "en", Label = "English" });

        StartupSetting.Options.Add(new SelectOption { Value = "none", Label = LocalizationService.Instance["Settings_Startup_No"] });
        StartupSetting.Options.Add(new SelectOption { Value = "user", Label = LocalizationService.Instance["Settings_Startup_Yes"] });
        StartupSetting.Options.Add(new SelectOption { Value = "admin", Label = LocalizationService.Instance["Settings_Startup_YesAdmin"] });

        LoadFromSettings();

        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture))
                ApplyLocalizedOptionLabels();
        };
    }

    /// <summary>语言切换后刷新下拉选项的本地化标签。</summary>
    private void ApplyLocalizedOptionLabels()
    {
        var loc = LocalizationService.Instance;
        SetOptionLabel(FontSetting, string.Empty, loc["Settings_Font_DefaultOption"]);
        SetOptionLabel(ProxyTypeSetting, "none", loc["Settings_Proxy_None"]);
        SetOptionLabel(StartupSetting, "none", loc["Settings_Startup_No"]);
        SetOptionLabel(StartupSetting, "user", loc["Settings_Startup_Yes"]);
        SetOptionLabel(StartupSetting, "admin", loc["Settings_Startup_YesAdmin"]);
    }

    private static void SetOptionLabel(SelectSetting setting, string value, string label)
    {
        var option = setting.Options.FirstOrDefault(o => o.Value == value);
        if (option is not null) option.Label = label;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            HotkeyText = _settings.Hotkey;
            OutputDirectory = _settings.OutputDirectory;
            ProxyServer = _settings.ProxyServer;
            ProxyPort = _settings.ProxyPort;
            ProxyUsername = _settings.ProxyUsername;
            ProxyPassword = _settings.ProxyPassword;
            FontSetting.SelectedOption =
                FontSetting.Options.FirstOrDefault(o => o.Value == _settings.FontFamily)
                ?? FontSetting.Options.FirstOrDefault();
            ProxyTypeSetting.SelectedOption =
                ProxyTypeSetting.Options.FirstOrDefault(o => o.Value == _settings.ProxyType)
                ?? ProxyTypeSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultProxyType);
            LanguageSetting.SelectedOption =
                LanguageSetting.Options.FirstOrDefault(o => o.Value == _settings.UiLanguage)
                ?? LanguageSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultUiLanguage);
            StartupSetting.SelectedOption =
                StartupSetting.Options.FirstOrDefault(o => o.Value == _settings.StartupMode)
                ?? StartupSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultStartupMode);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnHotkeyTextChanged(string value)
    {
        OnPropertyChanged(nameof(HotkeyDisplay));
        if (_loading) return;
        _hotkeyService.Update(value);
        Persist();
    }

    partial void OnOutputDirectoryChanged(string value) => Persist();
    partial void OnProxyServerChanged(string value) => Persist();
    partial void OnProxyPortChanged(int value) => Persist();
    partial void OnProxyUsernameChanged(string value) => Persist();
    partial void OnProxyPasswordChanged(string value) => Persist();

    /// <summary>由外部（下拉选择）调用，应用并保存字体。</summary>
    public void ApplyFontSetting()
    {
        if (_loading) return;
        ThemeService.ApplyFont(FontSetting.SelectedOption?.Value ?? string.Empty);
        Persist();
    }

    /// <summary>由外部（下拉选择）调用，更新代理类型并保存。</summary>
    public void ApplyProxyType()
    {
        if (_loading) return;
        Persist();
    }

    /// <summary>由外部（下拉选择）调用，更新界面语言并保存。切换即时生效，无需重启。</summary>
    public void ApplyLanguage()
    {
        if (_loading) return;
        LocalizationService.Instance.SetLanguage(LanguageSetting.SelectedOption?.Value ?? AiSettings.DefaultUiLanguage);
        Persist();
    }

    /// <summary>由外部（下拉选择）调用，应用开机自启动模式。返回结果供界面提示。</summary>
    public StartupApplyResult ApplyStartup()
    {
        if (_loading) return StartupApplyResult.Ok();

        var mode = StartupSetting.SelectedOption?.Value ?? AiSettings.DefaultStartupMode;
        var result = _startupService.Apply(mode);
        if (result.Success) Persist();
        return result;
    }

    /// <summary>以管理员身份（弹出 UAC）应用“管理员自启动”，成功后保存。返回结果供界面提示。</summary>
    public StartupApplyResult ApplyStartupElevated()
    {
        if (_loading) return StartupApplyResult.Ok();

        var result = _startupService.ApplyElevatedAdmin();
        if (result.Success) Persist();
        return result;
    }

    /// <summary>把开机自启动下拉恢复到上一次成功保存的模式。</summary>
    public void RevertStartupSelection()
    {
        StartupSetting.SelectedOption =
            StartupSetting.Options.FirstOrDefault(o => o.Value == _settings.StartupMode)
            ?? StartupSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultStartupMode);
    }

    /// <summary>保存通用设置（每次修改后自动调用）。</summary>
    private void Persist()
    {
        if (_loading) return;

        _settings.FontFamily = FontSetting.SelectedOption?.Value ?? string.Empty;
        _settings.Hotkey = HotkeyText;
        _settings.OutputDirectory = OutputDirectory;
        _settings.UiLanguage = LanguageSetting.SelectedOption?.Value ?? AiSettings.DefaultUiLanguage;
        _settings.StartupMode = StartupSetting.SelectedOption?.Value ?? AiSettings.DefaultStartupMode;
        _settings.ProxyType = ProxyTypeSetting.SelectedOption?.Value ?? AiSettings.DefaultProxyType;
        _settings.ProxyServer = ProxyServer;
        _settings.ProxyPort = ProxyPort;
        _settings.ProxyUsername = ProxyUsername;
        _settings.ProxyPassword = ProxyPassword;

        _settingsService.Save(_settings);

        SaveStatus = LocalizationService.Instance["Settings_Save_Saved"];
        _saveStatusTimer.Stop();
        _saveStatusTimer.Start();
    }

    // ===== 恢复默认值 =====

    [RelayCommand]
    private void ResetHotkey() => HotkeyText = AiSettings.DefaultHotkey;

    [RelayCommand]
    private void ResetProxyType()
    {
        ProxyTypeSetting.SelectedOption =
            ProxyTypeSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultProxyType)
            ?? ProxyTypeSetting.Options.FirstOrDefault();
        ApplyProxyType();
    }

    [RelayCommand]
    private void ResetLanguage()
    {
        LanguageSetting.SelectedOption =
            LanguageSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultUiLanguage)
            ?? LanguageSetting.Options.FirstOrDefault();
        ApplyLanguage();
    }

    [RelayCommand]
    private void ResetStartup()
    {
        StartupSetting.SelectedOption =
            StartupSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultStartupMode)
            ?? StartupSetting.Options.FirstOrDefault();
        ApplyStartup();
    }

    [RelayCommand]
    private void ResetOutputDirectory() => OutputDirectory = AiSettings.DefaultOutputDirectory;

    [RelayCommand]
    private void ResetProxyServer() => ProxyServer = string.Empty;

    [RelayCommand]
    private void ResetProxyPort() => ProxyPort = 0;

    [RelayCommand]
    private void ResetProxyUsername() => ProxyUsername = string.Empty;

    /// <summary>清除所有“始终允许（记住）”的持久化工具权限。</summary>
    [RelayCommand]
    private async Task ClearPersistentToolPermissionsAsync()
    {
        var loc = LocalizationService.Instance;
        try
        {
            await _toolPermission.ClearPersistentPermissionsAsync();
            ToolPermissionStatus = loc["Settings_ToolPermissions_Cleared"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清除持久化工具权限失败");
            ToolPermissionStatus = loc["Settings_ToolPermissions_ClearFailed"];
        }
    }

    /// <summary>将日记、事实、会话、待办与附件导出为 ZIP 备份。</summary>
    [RelayCommand(CanExecute = nameof(CanExportData))]
    private async Task ExportDataAsync()
    {
        var loc = LocalizationService.Instance;
        var dialog = new SaveFileDialog
        {
            Title = loc["Settings_Backup_DialogTitle"],
            Filter = loc["Settings_Backup_Filter"],
            FileName = $"Luna_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        IsExporting = true;
        BackupStatus = loc["Settings_Backup_Exporting"];
        try
        {
            await _exportService.ExportAllAsync(dialog.FileName);
            BackupStatus = loc.Format("Settings_Backup_Exported", dialog.FileName);
            _logger.LogInformation("数据备份完成：{Path}", dialog.FileName);
        }
        catch (OperationCanceledException)
        {
            BackupStatus = loc["Settings_Backup_Cancelled"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据备份失败");
            BackupStatus = loc.Format("Settings_Backup_Failed", ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>选择 ZIP 备份并暂存，重启后覆盖当前数据。</summary>
    [RelayCommand(CanExecute = nameof(CanImportData))]
    private async Task ImportDataAsync()
    {
        var loc = LocalizationService.Instance;
        var dialog = new OpenFileDialog
        {
            Title = loc["Settings_Import_DialogTitle"],
            Filter = loc["Settings_Import_Filter"],
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        var nickname = string.IsNullOrWhiteSpace(_settings.Nickname)
            ? AiSettings.DefaultNickname
            : _settings.Nickname;
        if (!ShowImportConfirmation(loc.Format("Settings_Import_ConfirmBody", nickname)))
            return;

        IsImporting = true;
        BackupStatus = loc["Settings_Import_Importing"];
        try
        {
            await _importService.StageAsync(dialog.FileName);
            BackupStatus = loc["Settings_Import_Staged"];
            _logger.LogInformation("备份已导入，等待重启：{Path}", dialog.FileName);
            ShowImportCompleted(Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入备份失败");
            BackupStatus = loc.Format("Settings_Import_FailedBody", ex.Message);
            ShowImportFailed(ex.Message);
        }
        finally
        {
            IsImporting = false;
        }
    }

    private bool CanExportData() => !IsExporting && !IsImporting;

    private bool CanImportData() => !IsImporting && !IsExporting;

    private bool ShowImportConfirmation(string body)
    {
        var loc = LocalizationService.Instance;
        var confirmed = false;
        var dialog = new LunaDialog
        {
            Owner = GetDialogOwner(),
            DialogTitle = loc["Settings_Import_ConfirmTitle"],
            DialogContent = CreateDialogBody(body),
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
                StyleKey = "StyleDangerSoft",
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

    private void ShowImportCompleted(string fileName)
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = GetDialogOwner(),
            DialogTitle = loc["Settings_Import_DoneTitle"],
            DialogContent = CreateDialogBody(loc.Format("Settings_Import_DoneBody", fileName)),
        };
        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Settings_Import_RestartButton"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    dialog.Close();
                    if (Application.Current is App app)
                        app.Restart();
                }),
            },
        };
        dialog.ShowDialog();
    }

    private void ShowImportFailed(string message)
    {
        var loc = LocalizationService.Instance;
        var dialog = new LunaDialog
        {
            Owner = GetDialogOwner(),
            DialogTitle = loc["Settings_Import_FailedTitle"],
            DialogContent = CreateDialogBody(loc.Format("Settings_Import_FailedBody", message)),
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

    private static Window? GetDialogOwner()
        => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
           ?? Application.Current.MainWindow;

    private static TextBlock CreateDialogBody(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Left,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>检查更新（壳子）：当前尚无发布通道，仅展示版本信息。</summary>
    [RelayCommand]
    private void CheckUpdate()
    {
        var loc = LocalizationService.Instance;
        var version = typeof(GeneralSettingsViewModel).Assembly.GetName().Version;
        var versionText = version is null ? loc["Settings_Update_UnknownVersion"] : $"{version.Major}.{version.Minor}.{version.Build}";
        UpdateStatus = loc.Format("Settings_Update_StatusFormat", versionText);
    }

    /// <summary>测试当前代理是否可用。</summary>
    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (IsTestingProxy) return;

        var loc = LocalizationService.Instance;
        IsTestingProxy = true;
        ProxyTestStatus = loc["Settings_Proxy_Testing"];

        try
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = new SettingsProxy(_settings),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            using var response = await client.GetAsync("https://www.baidu.com");
            ProxyTestStatus = response.IsSuccessStatusCode
                ? loc["Settings_Proxy_Available"]
                : loc.Format("Settings_Proxy_HttpError", (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            ProxyTestStatus = loc["Settings_Proxy_Timeout"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "代理测试失败");
            ProxyTestStatus = loc.Format("Settings_Proxy_Unavailable", ex.Message);
        }
        finally
        {
            IsTestingProxy = false;
        }
    }
}
