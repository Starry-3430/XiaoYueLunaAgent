using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Services.Tools;

namespace Luna.Models;

public partial class ToolDefinition : ObservableObject
{
    private static readonly Brush MediumBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0x79, 0x56));
    private static readonly Brush HighBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26));

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

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

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>是否需要在名称右侧标注风险等级。</summary>
    public bool HasRiskLabel => Risk is ToolRiskLevel.Medium or ToolRiskLevel.High;

    /// <summary>风险等级中文标签：危险 / 高危。</summary>
    public string RiskLabel => Risk switch
    {
        ToolRiskLevel.Medium => "危险",
        ToolRiskLevel.High => "高危",
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
}