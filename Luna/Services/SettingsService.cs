using System.IO;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private readonly ILogger<SettingsService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Luna");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    /// <summary>最近一次 <see cref="Load"/> 是否因配置文件丢失或结构损坏而进行了重置。</summary>
    public bool LastLoadWasReset { get; private set; }

    public AiSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            LastLoadWasReset = true;
            var defaults = CreateDefault();
            Save(defaults);
            _logger.LogWarning("配置文件丢失，已重置为默认配置：{Path}", _settingsPath);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AiSettings>(json, JsonOptions);
            if (settings is not null)
            {
                LastLoadWasReset = false;
                RestoreApiKey(settings);
                _logger.LogInformation("已加载配置文件：{Path}", _settingsPath);
                return settings;
            }

            _logger.LogWarning("配置文件内容为空，准备重置：{Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置文件解析失败，准备重置");
        }

        // 结构损坏：覆盖为默认配置
        LastLoadWasReset = true;
        var reset = CreateDefault();
        Save(reset);
        _logger.LogWarning("配置文件结构损坏，已重置为默认配置：{Path}", _settingsPath);
        return reset;
    }

    public void Save(AiSettings settings)
    {
        try
        {
            ProtectApiKey(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置文件失败");
        }
    }

    /// <summary>保存前把明文密钥用 DPAPI 加密写入对应的 Protected 字段。</summary>
    private static void ProtectApiKey(AiSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKey))
            settings.ApiKeyProtected = CredentialProtector.Protect(settings.ApiKey);

        if (!string.IsNullOrEmpty(settings.TavilyApiKey))
            settings.TavilyApiKeyProtected = CredentialProtector.Protect(settings.TavilyApiKey);

        if (!string.IsNullOrEmpty(settings.ProxyPassword))
            settings.ProxyPasswordProtected = CredentialProtector.Protect(settings.ProxyPassword);
    }

    /// <summary>加载后把 Protected 字段解密回明文；不存在则迁移旧明文字段。</summary>
    private static void RestoreApiKey(AiSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKeyProtected))
            settings.ApiKey = CredentialProtector.Unprotect(settings.ApiKeyProtected);
        else if (!string.IsNullOrEmpty(settings.ApiKey))
            settings.ApiKeyProtected = CredentialProtector.Protect(settings.ApiKey);

        if (!string.IsNullOrEmpty(settings.TavilyApiKeyProtected))
            settings.TavilyApiKey = CredentialProtector.Unprotect(settings.TavilyApiKeyProtected);
        else if (!string.IsNullOrEmpty(settings.TavilyApiKey))
            settings.TavilyApiKeyProtected = CredentialProtector.Protect(settings.TavilyApiKey);

        if (!string.IsNullOrEmpty(settings.ProxyPasswordProtected))
            settings.ProxyPassword = CredentialProtector.Unprotect(settings.ProxyPasswordProtected);
        else if (!string.IsNullOrEmpty(settings.ProxyPassword))
            settings.ProxyPasswordProtected = CredentialProtector.Protect(settings.ProxyPassword);
    }

    /// <summary>
    /// 生成默认配置。首次运行时会从环境变量迁移旧的 API Key（一次性），
    /// 之后统一以 settings.json 为准。
    /// </summary>
    private static AiSettings CreateDefault()
    {
        return new AiSettings
        {
            Provider = AiSettings.DefaultProvider,
            ApiKey = Environment.GetEnvironmentVariable("LUNA_API_KEY") ?? "",
            BaseUrl = AiSettings.DefaultBaseUrl,
            Model = AiSettings.DefaultModel,
            MaxTokens = AiSettings.DefaultMaxTokens,
            Temperature = AiSettings.DefaultTemperature,
            SystemPrompt = DefaultPrompts.SystemPrompt,
            Nickname = AiSettings.DefaultNickname,
            UserName = AiSettings.DefaultUserName,
            TavilyApiKey = Environment.GetEnvironmentVariable("LUNA_TAVILY_API_KEY") ?? "",
        };
    }
}