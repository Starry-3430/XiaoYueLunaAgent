// settings.json 读写：默认值生成、热更新、密钥加密、配置迁移与备份恢复

using System.IO;
using System.Text.Json;
using Luna.Models;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>配置文件加载结果，用于启动时提示用户。</summary>
public enum SettingsLoadStatus
{
    /// <summary>主配置文件正常加载。</summary>
    Loaded,

    /// <summary>主配置文件不可用，已从备份恢复。</summary>
    RecoveredFromBackup,

    /// <summary>配置文件损坏或首次运行，已重置为默认配置。</summary>
    ResetToDefaults,

    /// <summary>配置文件暂时无法读取；本次使用默认配置，但未覆盖原文件。</summary>
    Unavailable
}

public class SettingsService
{
    private const int ReadRetryCount = 3;
    private const int ReadRetryDelayMs = 80;

    private readonly string _settingsPath;
    private readonly string _backupPath;
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
        _backupPath = _settingsPath + ".bak";
    }

    public string SettingsPath => _settingsPath;

    /// <summary>最近一次 <see cref="Load"/> 的结果。</summary>
    public SettingsLoadStatus LastLoadStatus { get; private set; } = SettingsLoadStatus.Loaded;

    /// <summary>最近一次 <see cref="Load"/> 是否因为文件丢失或结构损坏而重置为默认配置。</summary>
    public bool LastLoadWasReset => LastLoadStatus == SettingsLoadStatus.ResetToDefaults;

    public AiSettings Load()
    {
        var primary = TryReadSettings(_settingsPath);
        if (primary.Settings is not null)
        {
            RestoreApiKey(primary.Settings);
            MigrateLegacyDefaults(primary.Settings);
            LastLoadStatus = SettingsLoadStatus.Loaded;

            // 每次成功加载后刷新备份，确保 .bak 始终是最近一次有效配置。
            if (primary.Json is not null)
                TryWriteBackup(primary.Json);

            _logger.LogInformation("已加载配置文件：{Path}", _settingsPath);
            return primary.Settings;
        }

        var backup = TryReadSettings(_backupPath);
        if (backup.Settings is not null)
        {
            RestoreApiKey(backup.Settings);
            MigrateLegacyDefaults(backup.Settings);
            LastLoadStatus = SettingsLoadStatus.RecoveredFromBackup;

            // 主文件存在但读不出来时，先保留现场，避免恢复过程覆盖诊断信息。
            if (!primary.Missing)
                TryPreserveUnreadableSettings();

            Save(backup.Settings);
            _logger.LogWarning("配置文件不可用，已从备份恢复：{Backup}", _backupPath);
            return backup.Settings;
        }

        var defaults = CreateDefault();

        // 主配置和备份都不存在：只有真正的首次运行才创建默认配置。
        if (primary.Missing && !InstallMarker.Exists)
        {
            LastLoadStatus = SettingsLoadStatus.ResetToDefaults;
            Save(defaults);
            _logger.LogWarning("配置文件不存在，已创建默认配置：{Path}", _settingsPath);
            return defaults;
        }

        // 文件存在但结构损坏：保留损坏文件后重置，避免每次启动都失败。
        if (!primary.Missing && IsCorrupt(primary.Error))
        {
            TryPreserveUnreadableSettings();
            LastLoadStatus = SettingsLoadStatus.ResetToDefaults;
            Save(defaults);
            _logger.LogWarning(primary.Error, "配置文件结构损坏，已重置为默认配置：{Path}", _settingsPath);
            return defaults;
        }

        // 文件已丢失（但已不是首次运行）或暂时无法读取：
        // 本次使用默认值，但不覆盖原文件，给用户保留现场和手动恢复的机会。
        LastLoadStatus = SettingsLoadStatus.Unavailable;
        if (primary.Error is not null)
            _logger.LogError(primary.Error, "配置文件不可用，本次使用默认配置且未覆盖原文件：{Path}", _settingsPath);
        else
            _logger.LogWarning("配置文件丢失，本次使用默认配置且未覆盖原文件：{Path}", _settingsPath);

        return defaults;
    }

    /// <summary>把历史遗留配置迁移到新的默认值（如旧的 4096 输出上限）。</summary>
    private static void MigrateLegacyDefaults(AiSettings settings)
    {
        if (settings.MaxTokens == AiSettings.LegacyDefaultMaxTokens)
            settings.MaxTokens = AiSettings.DefaultMaxTokens;

        // 旧版默认提示词把“系统规范”也写进了人格提示词；若仍是旧默认，迁移为仅人格提示词（规范改由系统强制注入）。
        if (IsLegacyCombinedPrompt(settings.SystemPrompt))
            settings.SystemPrompt = DefaultPrompts.PersonalityPrompt;
    }

    /// <summary>判断提示词是否为旧版“人格 + 系统规范”合并的默认文本。</summary>
    private static bool IsLegacyCombinedPrompt(string? prompt) =>
        !string.IsNullOrEmpty(prompt)
        && prompt.Contains("【上下文纪律】")
        && prompt.Contains("【不泄露内部过程】");

    public void Save(AiSettings settings)
    {
        try
        {
            ProtectApiKey(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            WriteAtomically(_settingsPath, json);
            TryWriteBackup(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置文件失败");
        }
    }

    /// <summary>
    /// 原子写入：先写临时文件，再替换目标文件。
    /// 写入失败时原文件保持不变，不会留下半截 JSON。
    /// </summary>
    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>刷新备份文件；备份失败不应影响正常配置读写。</summary>
    private void TryWriteBackup(string json)
    {
        try
        {
            WriteAtomically(_backupPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入配置备份失败：{Path}", _backupPath);
        }
    }

    /// <summary>把无法读取的主配置文件改名保留，便于后续诊断或手工恢复。</summary>
    private void TryPreserveUnreadableSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;

            var preservedPath = _settingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(_settingsPath, preservedPath, overwrite: true);
            _logger.LogWarning("已保留无法读取的配置文件副本：{Path}", preservedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保留无法读取的配置文件失败：{Path}", _settingsPath);
        }
    }

    /// <summary>
    /// 读取并解析配置文件。区分“文件不存在”和“文件存在但读取/解析失败”，
    /// 对瞬时占用和权限异常做短暂重试，避免误判后重置配置。
    /// </summary>
    private static SettingsReadResult TryReadSettings(string path)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < ReadRetryCount; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new SettingsReadResult(null, false, null,
                        new InvalidDataException("配置文件内容为空"));

                var settings = JsonSerializer.Deserialize<AiSettings>(json, JsonOptions);
                if (settings is null)
                    return new SettingsReadResult(null, false, null,
                        new InvalidDataException("配置文件内容为空"));

                return new SettingsReadResult(settings, false, json, null);
            }
            catch (FileNotFoundException)
            {
                return new SettingsReadResult(null, true, null, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new SettingsReadResult(null, true, null, null);
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException)
                && attempt < ReadRetryCount - 1)
            {
                lastError = ex;
                Thread.Sleep(ReadRetryDelayMs * (attempt + 1));
            }
            catch (Exception ex)
            {
                return new SettingsReadResult(null, false, null, ex);
            }
        }

        return new SettingsReadResult(null, false, null, lastError);
    }

    private static bool IsCorrupt(Exception? error) =>
        error is JsonException or InvalidDataException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不影响主流程。
        }
    }

    /// <summary>把当前内存中的 API Key 用 DPAPI 加密后写入对应 Protected 字段。</summary>
    private static void ProtectApiKey(AiSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKey))
            settings.ApiKeyProtected = CredentialProtector.Protect(settings.ApiKey);

        if (!string.IsNullOrEmpty(settings.TavilyApiKey))
            settings.TavilyApiKeyProtected = CredentialProtector.Protect(settings.TavilyApiKey);

        if (!string.IsNullOrEmpty(settings.ProxyPassword))
            settings.ProxyPasswordProtected = CredentialProtector.Protect(settings.ProxyPassword);
    }

    /// <summary>加载后把 Protected 字段解密放回内存；同时兼容历史明文密钥迁移。</summary>
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
    /// 生成默认配置。首次运行时从环境变量迁移旧 API Key（一次性），
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
            SystemPrompt = DefaultPrompts.PersonalityPrompt,
            Nickname = AiSettings.DefaultNickname,
            UserName = AiSettings.DefaultUserName,
            TavilyApiKey = Environment.GetEnvironmentVariable("LUNA_TAVILY_API_KEY") ?? "",
        };
    }

    private readonly record struct SettingsReadResult(
        AiSettings? Settings,
        bool Missing,
        string? Json,
        Exception? Error);
}
