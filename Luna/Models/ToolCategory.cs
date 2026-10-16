using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

/// <summary>
/// 工具分类（设置页中工具列表的分组容器）。名称由本地化 Key 驱动。
/// </summary>
public partial class ToolCategory : ObservableObject
{
    /// <summary>分组名本地化 Key（约定：Tool_Category_&lt;Name&gt;）。</summary>
    public string NameKey { get; set; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    public ObservableCollection<ToolDefinition> Tools { get; } = new();
}
