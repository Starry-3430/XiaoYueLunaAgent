using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

public partial class ChatMessage : ObservableObject
{
    [ObservableProperty]
    private string _role = "user";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    public bool IsThinking => IsStreaming && string.IsNullOrEmpty(Content);

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThinking));
    }
}