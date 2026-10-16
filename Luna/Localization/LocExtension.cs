using System.Windows.Data;
using System.Windows.Markup;

namespace Luna.Localization;

/// <summary>
/// XAML 本地化标记扩展：<c>Text="{loc:Loc Settings_Font_Title}"</c>。
/// 内部生成一个指向 <see cref="LocalizationService.Instance"/> 索引器的单向绑定，
/// 切换语言时服务触发 PropertyChanged，所有绑定自动刷新，无需重启。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>资源 Key，约定为 <c>区域_组件_元素</c>。</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
