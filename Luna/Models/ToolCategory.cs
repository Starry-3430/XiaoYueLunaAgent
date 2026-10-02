using System.Collections.ObjectModel;

namespace Luna.Models;

public class ToolCategory
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<ToolDefinition> Tools { get; } = new();
}