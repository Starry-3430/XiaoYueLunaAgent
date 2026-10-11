using System.Collections.ObjectModel;

namespace Luna.Models;

/// <summary>
/// 工具分类（设置页中工具列表的分组容器）
/// </summary>
public class ToolCategory
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<ToolDefinition> Tools { get; } = new();
}