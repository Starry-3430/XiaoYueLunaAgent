using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

/// <summary>“设置 - 通用”页的视图模型：字体、快捷键、代理。</summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly HotkeyService _hotkeyService;
    private readonly ILogger<GeneralSettingsViewModel> _logger;
    private bool _loading;

    public SelectSetting FontSetting { get; } = new();
    public SelectSetting ProxyTypeSetting { get; } = new();

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
    private string _proxyTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingProxy;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    private readonly DispatcherTimer _saveStatusTimer;

    public GeneralSettingsViewModel(AiSettings settings, SettingsService settingsService,
        HotkeyService hotkeyService, ILogger<GeneralSettingsViewModel> logger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _hotkeyService = hotkeyService;
        _logger = logger;

        _saveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _saveStatusTimer.Tick += (_, _) =>
        {
            _saveStatusTimer.Stop();
            SaveStatus = string.Empty;
        };

        FontSetting.Options.Add(new SelectOption { Value = string.Empty, Label = "默认（内置字体）" });
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

        ProxyTypeSetting.Options.Add(new SelectOption { Value = "none", Label = "无代理" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "http", Label = "HTTP" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks4", Label = "SOCKS4" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks5", Label = "SOCKS5" });

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            HotkeyText = _settings.Hotkey;
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

    /// <summary>保存通用设置。</summary>
    [RelayCommand]
    private void Save()
    {
        Persist();
        SaveStatus = "已保存";
        _saveStatusTimer.Stop();
        _saveStatusTimer.Start();
    }

    private void Persist()
    {
        if (_loading) return;

        _settings.FontFamily = FontSetting.SelectedOption?.Value ?? string.Empty;
        _settings.Hotkey = HotkeyText;
        _settings.ProxyType = ProxyTypeSetting.SelectedOption?.Value ?? AiSettings.DefaultProxyType;
        _settings.ProxyServer = ProxyServer;
        _settings.ProxyPort = ProxyPort;
        _settings.ProxyUsername = ProxyUsername;
        _settings.ProxyPassword = ProxyPassword;

        _settingsService.Save(_settings);
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
    private void ResetProxyServer() => ProxyServer = string.Empty;

    [RelayCommand]
    private void ResetProxyPort() => ProxyPort = 0;

    [RelayCommand]
    private void ResetProxyUsername() => ProxyUsername = string.Empty;

    /// <summary>测试当前代理是否可用。</summary>
    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (IsTestingProxy) return;

        IsTestingProxy = true;
        ProxyTestStatus = "测试中…";

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
                ? "代理可用 ✓"
                : $"代理返回 HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException)
        {
            ProxyTestStatus = "代理测试超时";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "代理测试失败");
            ProxyTestStatus = "代理不可用：" + ex.Message;
        }
        finally
        {
            IsTestingProxy = false;
        }
    }
}
