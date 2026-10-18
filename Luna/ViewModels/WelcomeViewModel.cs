using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Microsoft.Extensions.Logging;

namespace Luna.ViewModels;

/// <summary>
/// 首次启动欢迎向导：昵称、AI 连接与快捷键/托盘说明。
/// AI 配置项与设置页保持一致（提供商、模型、Base URL、API Key、连接测试）。
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    private const string CustomModelValue = "__custom__";

    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WelcomeViewModel> _logger;
    private bool _loading;

    public event Action<bool>? RequestClose;
    public event Action? StepChanged;

    public SelectSetting ProviderSetting { get; } = new();
    public SelectSetting ModelSetting { get; } = new();
    public SelectSetting ProxyTypeSetting { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText), nameof(SecondaryButtonText), nameof(StepIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowApiSkippedNote))]
    private int _step = 1;

    [ObservableProperty]
    private string _aiNickname = AiSettings.DefaultNickname;

    [ObservableProperty]
    private string _userName = AiSettings.DefaultUserName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiConfigured), nameof(ShowApiSkippedNote))]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiConfigured), nameof(ShowApiSkippedNote))]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiConfigured), nameof(ShowApiSkippedNote))]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _customModel = string.Empty;

    [ObservableProperty]
    private bool _isCustomModel;

    [ObservableProperty]
    private string _backgroundModel = string.Empty;

    [ObservableProperty]
    private string _proxyServer = string.Empty;

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private string _proxyUsername = string.Empty;

    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [ObservableProperty]
    private bool _isProxyExpanded;

    [ObservableProperty]
    private bool _isBackgroundModelExpanded;

    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    public bool IsApiConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model);

    public bool ShowApiSkippedNote => IsStep3 && !IsApiConfigured;

    public string HotkeyText => _settings.Hotkey;

    public string PrimaryButtonText => Step == 3
        ? LocalizationService.Instance["Welcome_Finish"]
        : LocalizationService.Instance["Welcome_Next"];

    public string SecondaryButtonText => Step switch
    {
        1 => LocalizationService.Instance["Welcome_Skip"],
        2 => LocalizationService.Instance["Welcome_Later"],
        _ => LocalizationService.Instance["Welcome_Back"],
    };

    public string StepIndicator => LocalizationService.Instance.Format("Welcome_StepIndicator", Step);

    public WelcomeViewModel(AiSettings settings, SettingsService settingsService,
        HttpClient httpClient, ILogger<WelcomeViewModel> logger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _httpClient = httpClient;
        _logger = logger;

        var loc = LocalizationService.Instance;
        foreach (var preset in AiProviderCatalog.Presets)
            ProviderSetting.Options.Add(new SelectOption { Value = preset.Id, Label = ProviderLabel(preset.Id) });

        ProxyTypeSetting.Options.Add(new SelectOption { Value = "none", Label = loc["Settings_Proxy_None"] });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "http", Label = "HTTP" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks4", Label = "SOCKS4" });
        ProxyTypeSetting.Options.Add(new SelectOption { Value = "socks5", Label = "SOCKS5" });

        _loading = true;
        try
        {
            AiNickname = string.IsNullOrWhiteSpace(settings.Nickname)
                ? AiSettings.DefaultNickname
                : settings.Nickname;
            UserName = string.IsNullOrWhiteSpace(settings.UserName)
                ? AiSettings.DefaultUserName
                : settings.UserName;
            BaseUrl = settings.BaseUrl;
            ApiKey = settings.ApiKey;
            Model = settings.Model;
            BackgroundModel = settings.BackgroundModel;
            ProxyServer = settings.ProxyServer;
            ProxyPort = settings.ProxyPort;
            ProxyUsername = settings.ProxyUsername;
            ProxyPassword = settings.ProxyPassword;

            ProviderSetting.SelectedOption =
                ProviderSetting.Options.FirstOrDefault(o => o.Value == settings.Provider)
                ?? ProviderSetting.Options.FirstOrDefault(o => o.Value == "openai")
                ?? ProviderSetting.Options.FirstOrDefault();
            ProxyTypeSetting.SelectedOption =
                ProxyTypeSetting.Options.FirstOrDefault(o => o.Value == settings.ProxyType)
                ?? ProxyTypeSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultProxyType)
                ?? ProxyTypeSetting.Options.FirstOrDefault();

            RebuildModelOptions();
            var modelOption = ModelSetting.Options.FirstOrDefault(o => o.Value == settings.Model);
            if (modelOption is not null)
            {
                ModelSetting.SelectedOption = modelOption;
            }
            else if (!string.IsNullOrWhiteSpace(settings.Model))
            {
                ModelSetting.SelectedOption = ModelSetting.Options.FirstOrDefault(o => o.Value == CustomModelValue);
                CustomModel = settings.Model;
                IsCustomModel = true;
                Model = settings.Model;
            }
            else
            {
                // 首次启动：使用当前提供商的第一个预设模型；自定义提供商则显示模型输入框。
                ModelSetting.SelectedOption =
                    ModelSetting.Options.FirstOrDefault(o => o.Value != CustomModelValue)
                    ?? ModelSetting.Options.FirstOrDefault(o => o.Value == CustomModelValue);

                if (ModelSetting.SelectedOption?.Value == CustomModelValue)
                {
                    IsCustomModel = true;
                    Model = CustomModel;
                }
                else
                {
                    IsCustomModel = false;
                    Model = ModelSetting.SelectedOption?.Value ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(BaseUrl))
                BaseUrl = AiProviderCatalog.Find(ProviderSetting.SelectedOption?.Value ?? string.Empty)?.BaseUrl ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }

        ProviderSetting.PropertyChanged += OnProviderSettingChanged;
        ModelSetting.PropertyChanged += OnModelSettingChanged;
    }

    private static string ProviderLabel(string id)
    {
        var loc = LocalizationService.Instance;
        return id switch
        {
            "zhipu" => loc["AiConnection_Provider_Zhipu"],
            "qwen" => loc["AiConnection_Provider_Qwen"],
            "custom" => loc["AiConnection_Provider_Custom"],
            _ => AiProviderCatalog.Find(id)?.Name ?? id,
        };
    }

    private void OnProviderSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectSetting.SelectedOption)) return;
        if (_loading || ProviderSetting.SelectedOption is null) return;

        var preset = AiProviderCatalog.Find(ProviderSetting.SelectedOption.Value);
        if (preset is null) return;

        if (!string.IsNullOrWhiteSpace(preset.BaseUrl))
            BaseUrl = preset.BaseUrl;

        CustomModel = string.Empty;
        RebuildModelOptions();

        ModelSetting.SelectedOption =
            ModelSetting.Options.FirstOrDefault(o => o.Value != CustomModelValue)
            ?? ModelSetting.Options.FirstOrDefault(o => o.Value == CustomModelValue);
    }

    private void OnModelSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectSetting.SelectedOption)) return;
        if (_loading || ModelSetting.SelectedOption is null) return;

        if (ModelSetting.SelectedOption.Value == CustomModelValue)
        {
            IsCustomModel = true;
            Model = CustomModel;
        }
        else
        {
            IsCustomModel = false;
            Model = ModelSetting.SelectedOption.Value;
        }
    }

    private void RebuildModelOptions()
    {
        ModelSetting.Options.Clear();

        var preset = ProviderSetting.SelectedOption is null
            ? null
            : AiProviderCatalog.Find(ProviderSetting.SelectedOption.Value);
        if (preset is not null)
        {
            foreach (var model in preset.Models)
                ModelSetting.Options.Add(new SelectOption { Value = model, Label = model });
        }

        ModelSetting.Options.Add(new SelectOption
        {
            Value = CustomModelValue,
            Label = LocalizationService.Instance["AiConnection_Model_Custom"],
        });
    }

    partial void OnCustomModelChanged(string value)
    {
        if (_loading) return;
        if (IsCustomModel)
            Model = value;
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(ShowApiSkippedNote));
        StepChanged?.Invoke();
    }

    [RelayCommand]
    private void Next()
    {
        if (Step < 3)
        {
            Step++;
            return;
        }

        Complete();
    }

    [RelayCommand]
    private void Secondary()
    {
        // 第 1、2 步的“跳过/稍后配置”都直接走到最后一步，
        // 在那里提示用户之后可以在设置里补齐 API 配置。
        Step = Step == 3 ? 2 : 3;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 1) Step--;
    }

    [RelayCommand]
    private void ToggleProxy() => IsProxyExpanded = !IsProxyExpanded;

    [RelayCommand]
    private void ToggleBackgroundModel() => IsBackgroundModelExpanded = !IsBackgroundModelExpanded;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting) return;

        var loc = LocalizationService.Instance;
        var url = BuildBaseUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            TestStatus = loc["AiConnection_Test_NoBaseUrl"];
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            TestStatus = loc["Welcome_Test_NoModel"];
            return;
        }

        IsTesting = true;
        TestStatus = loc["AiConnection_Test_Testing"];

        // 让连接测试立即使用向导中填写的代理（SettingsProxy 读取的是实时设置对象）。
        ApplyProxyToSettings();

        try
        {
            var endpoint = $"{url.TrimEnd('/')}/chat/completions";

            var body = new JsonObject
            {
                ["model"] = Model.Trim(),
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "ping" },
                },
                ["max_tokens"] = 1,
                ["stream"] = false,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(ApiKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey.Trim());
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                TestStatus = loc["Welcome_Test_Success"];
                _logger.LogInformation("欢迎向导连接测试成功：{Url} model={Model}", endpoint, Model);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                var snippet = errorBody.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (snippet.Length > 160) snippet = snippet[..160] + "…";
                TestStatus = loc.Format("AiConnection_Test_FailedHttp", (int)response.StatusCode, snippet);
                _logger.LogWarning("欢迎向导连接测试失败：{Status} {Body}", (int)response.StatusCode, errorBody);
            }
        }
        catch (OperationCanceledException)
        {
            TestStatus = loc["AiConnection_Test_Timeout"];
        }
        catch (HttpRequestException ex)
        {
            TestStatus = loc.Format("AiConnection_Test_Failed", ex.InnerException?.Message ?? ex.Message);
            _logger.LogError(ex, "欢迎向导连接测试异常：{Url}", url);
        }
        catch (Exception ex)
        {
            TestStatus = loc.Format("AiConnection_Test_Failed", ex.Message);
            _logger.LogError(ex, "欢迎向导连接测试异常");
        }
        finally
        {
            IsTesting = false;
        }
    }

    private string BuildBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
            return BaseUrl.Trim();

        var preset = ProviderSetting.SelectedOption is null
            ? null
            : AiProviderCatalog.Find(ProviderSetting.SelectedOption.Value);
        return preset?.BaseUrl ?? string.Empty;
    }

    private void Complete()
    {
        SaveSettings();
        RequestClose?.Invoke(true);
    }

    private void ApplyProxyToSettings()
    {
        _settings.ProxyType = ProxyTypeSetting.SelectedOption?.Value ?? AiSettings.DefaultProxyType;
        _settings.ProxyServer = ProxyServer.Trim();
        _settings.ProxyPort = ProxyPort;
        _settings.ProxyUsername = ProxyUsername.Trim();
        _settings.ProxyPassword = ProxyPassword;
    }

    private void SaveSettings()
    {
        _settings.Nickname = string.IsNullOrWhiteSpace(AiNickname)
            ? AiSettings.DefaultNickname
            : AiNickname.Trim();
        _settings.UserName = string.IsNullOrWhiteSpace(UserName)
            ? AiSettings.DefaultUserName
            : UserName.Trim();

        _settings.Provider = ProviderSetting.SelectedOption?.Value ?? "custom";
        _settings.BaseUrl = BaseUrl.Trim();
        _settings.ApiKey = ApiKey;
        _settings.Model = Model.Trim();
        _settings.BackgroundModel = BackgroundModel.Trim();

        ApplyProxyToSettings();

        _settingsService.Save(_settings);
    }
}
