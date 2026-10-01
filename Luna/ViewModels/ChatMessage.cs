using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

public partial class ChatMessage : ObservableObject
{
    private bool _userCollapsedReasoning;

    [ObservableProperty]
    private string _role = "user";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _reasoningContent = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isReasoningExpanded;

    public bool IsThinking => IsStreaming && string.IsNullOrEmpty(Content);

    public bool HasReasoning => !string.IsNullOrEmpty(ReasoningContent);

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnReasoningContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
        if (!string.IsNullOrEmpty(value) && !_userCollapsedReasoning)
            IsReasoningExpanded = true;
    }

    partial void OnIsReasoningExpandedChanged(bool value)
    {
        if (!value)
            _userCollapsedReasoning = true;
    }
}