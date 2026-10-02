using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class AiConnectionViewModel : ObservableObject
{
    private const string CustomModelValue = "__custom__";

    private readonly AiSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiConnectionViewModel> _logger;
    private readonly DispatcherTimer _saveStatusTimer;
    private bool _syncingTemperature;

    public SelectSetting ProviderSetting { get; } = new();
    public SelectSetting ModelSetting { get; } = new();
    public SelectSetting ResponseLanguageSetting { get; } = new();

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private int _maxTokens = 4096;

    [ObservableProperty]
    private double _temperature = 1.0;

    [ObservableProperty]
    private string _temperatureText = "1.0";

    [ObservableProperty]
    private double _topP = AiSettings.DefaultTopP;

    [ObservableProperty]
    private string _topPText = "0.9";

    [ObservableProperty]
    private double _frequencyPenalty = AiSettings.DefaultFrequencyPenalty;

    [ObservableProperty]
    private string _frequencyPenaltyText = "0.1";

    [ObservableProperty]
    private double _presencePenalty = AiSettings.DefaultPresencePenalty;

    [ObservableProperty]
    private string _presencePenaltyText = "0.2";

    [ObservableProperty]
    private bool _deepThinking;

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    [ObservableProperty]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _customModel = string.Empty;

    [ObservableProperty]
    private bool _isCustomModel;

    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    private bool _loading;

    public AiConnectionViewModel(AiSettings settings, SettingsService settingsService,
        HttpClient httpClient, ILogger<AiConnectionViewModel> logger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _httpClient = httpClient;
        _logger = logger;

        foreach (var preset in AiProviderCatalog.Presets)
            ProviderSetting.Options.Add(new SelectOption { Value = preset.Id, Label = preset.Name });

        ResponseLanguageSetting.Options.Add(new SelectOption { Value = "auto", Label = "跟随用户" });
        ResponseLanguageSetting.Options.Add(new SelectOption { Value = "zh", Label = "简体中文" });
        ResponseLanguageSetting.Options.Add(new SelectOption { Value = "en", Label = "English" });

        ProviderSetting.PropertyChanged += OnProviderSettingChanged;
        ModelSetting.PropertyChanged += OnModelSettingChanged;

        _saveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _saveStatusTimer.Tick += (_, _) =>
        {
            _saveStatusTimer.Stop();
            SaveStatus = string.Empty;
        };

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            BaseUrl = _settings.BaseUrl;
            ApiKey = _settings.ApiKey;
            MaxTokens = _settings.MaxTokens;
            Temperature = _settings.Temperature;
            TopP = _settings.TopP;
            FrequencyPenalty = _settings.FrequencyPenalty;
            PresencePenalty = _settings.PresencePenalty;
            DeepThinking = _settings.DeepThinking;
            SystemPrompt = _settings.SystemPrompt;
            Nickname = _settings.Nickname;
            UserName = _settings.UserName;
            Model = _settings.Model;

            ResponseLanguageSetting.SelectedOption =
                ResponseLanguageSetting.Options.FirstOrDefault(o => o.Value == _settings.ResponseLanguage)
                ?? ResponseLanguageSetting.Options.FirstOrDefault(o => o.Value == AiSettings.DefaultResponseLanguage);

            var provider = ProviderSetting.Options.FirstOrDefault(o => o.Value == _settings.Provider)
                           ?? ProviderSetting.Options.FirstOrDefault(o => o.Value == "custom");
            ProviderSetting.SelectedOption = provider;

            RebuildModelOptions();

            var modelOption = ModelSetting.Options.FirstOrDefault(o => o.Value == _settings.Model);
            if (modelOption is not null)
            {
                ModelSetting.SelectedOption = modelOption;
            }
            else
            {
                ModelSetting.SelectedOption = ModelSetting.Options.FirstOrDefault(o => o.Value == CustomModelValue);
                CustomModel = _settings.Model;
                IsCustomModel = true;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnProviderSettingChanged(object? sender, PropertyChangedEventArgs e)
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

    private void OnModelSettingChanged(object? sender, PropertyChangedEventArgs e)
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
            foreach (var m in preset.Models)
                ModelSetting.Options.Add(new SelectOption { Value = m, Label = m });
        }

        ModelSetting.Options.Add(new SelectOption { Value = CustomModelValue, Label = "自定义…" });
    }

    partial void OnCustomModelChanged(string value)
    {
        if (_loading) return;
        if (IsCustomModel)
            Model = value;
    }

    partial void OnTemperatureChanged(double value)
    {
        if (_syncingTemperature) return;
        _syncingTemperature = true;
        TemperatureText = value.ToString("0.0", CultureInfo.InvariantCulture);
        _syncingTemperature = false;
    }

    partial void OnTopPChanged(double value) => TopPText = FormatValue(value);
    partial void OnFrequencyPenaltyChanged(double value) => FrequencyPenaltyText = FormatValue(value);
    partial void OnPresencePenaltyChanged(double value) => PresencePenaltyText = FormatValue(value);

    private static string FormatValue(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static double ParseClamped(string text, double fallback, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            parsed = fallback;
        return Math.Round(Math.Clamp(parsed, min, max), 1);
    }

    /// <summary>
    /// 提交温度输入框：解析并在回车/失焦时把数值夹紧到 [0, 2]，同时回写规范化后的文本。
    /// </summary>
    public void CommitTemperature()
    {
        Temperature = ParseClamped(TemperatureText, Temperature, 0.0, 2.0);
        TemperatureText = FormatValue(Temperature);
    }

    /// <summary>提交 top_p，夹紧到 [0, 1]。</summary>
    public void CommitTopP()
    {
        TopP = ParseClamped(TopPText, TopP, 0.0, 1.0);
        TopPText = FormatValue(TopP);
    }

    /// <summary>提交 frequency_penalty，夹紧到 [-2, 2]。</summary>
    public void CommitFrequencyPenalty()
    {
        FrequencyPenalty = ParseClamped(FrequencyPenaltyText, FrequencyPenalty, -2.0, 2.0);
        FrequencyPenaltyText = FormatValue(FrequencyPenalty);
    }

    /// <summary>提交 presence_penalty，夹紧到 [-2, 2]。</summary>
    public void CommitPresencePenalty()
    {
        PresencePenalty = ParseClamped(PresencePenaltyText, PresencePenalty, -2.0, 2.0);
        PresencePenaltyText = FormatValue(PresencePenalty);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _settings.Provider = ProviderSetting.SelectedOption?.Value ?? "custom";
            _settings.Model = Model;
            _settings.BaseUrl = BaseUrl;
            _settings.ApiKey = ApiKey;
            _settings.MaxTokens = MaxTokens;
            _settings.Temperature = Temperature;
            _settings.TopP = TopP;
            _settings.FrequencyPenalty = FrequencyPenalty;
            _settings.PresencePenalty = PresencePenalty;
            _settings.DeepThinking = DeepThinking;
            _settings.ResponseLanguage = ResponseLanguageSetting.SelectedOption?.Value ?? AiSettings.DefaultResponseLanguage;
            _settings.SystemPrompt = SystemPrompt;
            _settings.Nickname = Nickname;
            _settings.UserName = UserName;

            _settingsService.Save(_settings);
            SaveStatus = "已保存";
            _saveStatusTimer.Stop();
            _saveStatusTimer.Start();
            _logger.LogInformation("AI 连接设置已保存，Provider={Provider}", _settings.Provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 AI 连接设置失败");
            SaveStatus = "保存失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting) return;

        var url = BuildBaseUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            TestStatus = "未填写 Base URL";
            return;
        }

        IsTesting = true;
        TestStatus = "连接测试中…";

        try
        {
            var endpoint = $"{url.TrimEnd('/')}/chat/completions";

            var body = new JsonObject
            {
                ["model"] = Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "ping" },
                },
                ["max_tokens"] = 1,
                ["stream"] = false,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(ApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                TestStatus = "连接成功 ✓";
                _logger.LogInformation("连接测试成功：{Url} model={Model}", endpoint, Model);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                var snippet = errorBody.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (snippet.Length > 160) snippet = snippet[..160] + "…";
                TestStatus = $"连接失败：HTTP {(int)response.StatusCode} {snippet}";
                _logger.LogWarning("连接测试失败：{Status} {Body}", (int)response.StatusCode, errorBody);
            }
        }
        catch (OperationCanceledException)
        {
            TestStatus = "连接超时（15 秒）";
        }
        catch (HttpRequestException ex)
        {
            TestStatus = "连接失败：" + (ex.InnerException?.Message ?? ex.Message);
            _logger.LogError(ex, "连接测试异常：{Url}", BuildBaseUrl());
        }
        catch (Exception ex)
        {
            TestStatus = "连接失败：" + ex.Message;
            _logger.LogError(ex, "连接测试异常");
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void IncreaseTemperature()
    {
        Temperature = Math.Round(Math.Min(Temperature + 0.1, 2.0), 1);
    }

    [RelayCommand]
    private void DecreaseTemperature()
    {
        Temperature = Math.Round(Math.Max(Temperature - 0.1, 0.0), 1);
    }

    [RelayCommand]
    private void IncreaseTopP() => TopP = Math.Round(Math.Min(TopP + 0.1, 1.0), 1);

    [RelayCommand]
    private void DecreaseTopP() => TopP = Math.Round(Math.Max(TopP - 0.1, 0.0), 1);

    [RelayCommand]
    private void IncreaseFrequencyPenalty() => FrequencyPenalty = Math.Round(Math.Min(FrequencyPenalty + 0.1, 2.0), 1);

    [RelayCommand]
    private void DecreaseFrequencyPenalty() => FrequencyPenalty = Math.Round(Math.Max(FrequencyPenalty - 0.1, -2.0), 1);

    [RelayCommand]
    private void IncreasePresencePenalty() => PresencePenalty = Math.Round(Math.Min(PresencePenalty + 0.1, 2.0), 1);

    [RelayCommand]
    private void DecreasePresencePenalty() => PresencePenalty = Math.Round(Math.Max(PresencePenalty - 0.1, -2.0), 1);

    // ===== 恢复默认值 =====

    [RelayCommand]
    private void ResetNickname()
    {
        Nickname = AiSettings.DefaultNickname;
    }

    [RelayCommand]
    private void ResetUserName()
    {
        UserName = AiSettings.DefaultUserName;
    }

    [RelayCommand]
    private void ResetSystemPrompt()
    {
        SystemPrompt = DefaultPrompts.SystemPrompt;
    }

    [RelayCommand]
    private void ResetMaxTokens()
    {
        MaxTokens = AiSettings.DefaultMaxTokens;
    }

    [RelayCommand]
    private void ResetTemperature()
    {
        Temperature = AiSettings.DefaultTemperature;
        TemperatureText = AiSettings.DefaultTemperature.ToString("0.0", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void ResetTopP()
    {
        TopP = AiSettings.DefaultTopP;
        TopPText = FormatValue(TopP);
    }

    [RelayCommand]
    private void ResetFrequencyPenalty()
    {
        FrequencyPenalty = AiSettings.DefaultFrequencyPenalty;
        FrequencyPenaltyText = FormatValue(FrequencyPenalty);
    }

    [RelayCommand]
    private void ResetPresencePenalty()
    {
        PresencePenalty = AiSettings.DefaultPresencePenalty;
        PresencePenaltyText = FormatValue(PresencePenalty);
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
}
