using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

public partial class ToolDefinition : ObservableObject
{
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

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusText));
    }
}