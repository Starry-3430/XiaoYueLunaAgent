using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Localization;
using Luna.Services.Tools;

namespace Luna.Models;

/// <summary>
/// 工具定义视图模型（名称、说明、启用状态、风险等级与标签颜色）。
/// 显示文本由本地化 Key 驱动，支持切换语言后动态刷新。
/// </summary>
public partial class ToolDefinition : ObservableObject
{
    private static readonly Brush MediumBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0x79, 0x56));
    private static readonly Brush HighBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26));

    [ObservableProperty]
    private string _id = string.Empty;

    /// <summary>显示名本地化 Key（约定：Tool_&lt;Name&gt;_DisplayName）。</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>说明本地化 Key（约定：Tool_&lt;Name&gt;_Description）。</summary>
    public string DescriptionKey { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _requiresConfig;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private ToolRiskLevel _risk = ToolRiskLevel.None;

    public string Name => LocalizationService.Instance[NameKey];

    public string Description => LocalizationService.Instance[DescriptionKey];

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>是否需要在名称右侧标注风险等级。</summary>
    public bool HasRiskLabel => Risk is ToolRiskLevel.Medium or ToolRiskLevel.High;

    /// <summary>风险等级本地化标签。</summary>
    public string RiskLabel => Risk switch
    {
        ToolRiskLevel.Medium => LocalizationService.Instance["Tool_Risk_Medium"],
        ToolRiskLevel.High => LocalizationService.Instance["Tool_Risk_High"],
        _ => string.Empty,
    };

    /// <summary>风险标签颜色：Medium #FD7956 / High #A93226。</summary>
    public Brush RiskBrush => Risk == ToolRiskLevel.High ? HighBrush : MediumBrush;

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusText));
    }

    partial void OnRiskChanged(ToolRiskLevel value)
    {
        OnPropertyChanged(nameof(HasRiskLabel));
        OnPropertyChanged(nameof(RiskLabel));
        OnPropertyChanged(nameof(RiskBrush));
    }

    /// <summary>语言切换后刷新由本地化驱动的显示文本。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(RiskLabel));
    }
}
