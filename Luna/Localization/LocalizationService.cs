using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Luna.Localization;

/// <summary>
/// 运行时本地化服务：持有当前语言下的全部字符串，切换语言时触发
/// <see cref="INotifyPropertyChanged.PropertyChanged"/>，令所有 <see cref="LocExtension"/>
/// 绑定即时刷新，无需重启应用。
/// </summary>
/// <remarks>
/// 字符串按语言分目录存放：<c>Resources/Localization/&lt;lang&gt;/&lt;Module&gt;.resx</c>
/// （如 <c>en/Settings.resx</c>、<c>zh-Hans/Settings.resx</c>）。每个文件编译为普通嵌入资源，
/// 通过完整资源名以 <see cref="ResourceManager"/> 读取，因此可在运行时切换；缺失的 Key 回退到
/// <see cref="FallbackFolder"/>（英文）。
/// </remarks>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>资源根命名空间（RootNamespace + 目录）。</summary>
    private const string ResourceRoot = "Luna.Resources.Localization";

    /// <summary>各功能域模块（语言文件夹内的文件名，不含扩展名）。</summary>
    private static readonly string[] ModuleNames =
    {
        "Common", "App", "Home", "Main", "Settings", "AiConnection", "Attachment", "Diary", "Dialog", "Notification", "Tool",
    };

    /// <summary>兜底语言文件夹：其余语言缺失的 Key 回退到此。</summary>
    private const string FallbackFolder = "en";

    private static readonly Assembly Assembly = typeof(LocalizationService).Assembly;

    private Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    private LocalizationService()
    {
    }

    /// <summary>全局单例。</summary>
    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    /// <summary>按 Key 取当前语言的字符串；缺失时返回 Key 本身，便于发现遗漏。</summary>
    public string this[string key] =>
        _values.TryGetValue(key, out var value) ? value : key;

    /// <summary>取字符串并按当前区域格式化（如 <c>{0}</c> 占位符）。</summary>
    public string Format(string key, params object?[] args) => string.Format(this[key], args);

    /// <summary>把 "zh"/"en" 之类的语言代码解析为 <see cref="CultureInfo"/>。</summary>
    public static CultureInfo ResolveCulture(string? code) => code?.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" => CultureInfo.GetCultureInfo("zh-Hans"),
        _ => CultureInfo.GetCultureInfo("en"),
    };

    public void SetLanguage(string? code) => SetLanguage(ResolveCulture(code));

    /// <summary>切换语言并刷新所有绑定。</summary>
    public void SetLanguage(CultureInfo culture)
    {
        _culture = culture;

        var folder = FolderFor(culture);
        var map = LoadLanguage(folder);

        // 当前语言以外的 Key 回退到英文，避免漏译时显示 Key 本身
        if (!string.Equals(folder, FallbackFolder, StringComparison.Ordinal))
        {
            foreach (var pair in LoadLanguage(FallbackFolder))
                map.TryAdd(pair.Key, pair.Value);
        }

        _values = map;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    /// <summary>CultureInfo → 语言文件夹名（<c>Resources/Localization/&lt;folder&gt;</c>）。</summary>
    private static string FolderFor(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "zh" ? "zh-Hans" : "en";

    /// <summary>读取某语言文件夹下所有模块的字符串。</summary>
    private static Dictionary<string, string> LoadLanguage(string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in ModuleNames)
        {
            var manager = new ResourceManager(BaseName(folder, module), Assembly);
            ResourceSet? set;
            try
            {
                set = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
            }
            catch (MissingManifestResourceException)
            {
                continue;
            }

            if (set is null) continue;
            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key && entry.Value is string value)
                    map[key] = value;
            }
        }

        return map;
    }

    /// <summary>资源基础名；文件夹名中的 '-' 会被 MSBuild 规范化为 '_'（zh-Hans → zh_Hans）。</summary>
    private static string BaseName(string folder, string module) =>
        $"{ResourceRoot}.{folder.Replace('-', '_')}.{module}";
}
