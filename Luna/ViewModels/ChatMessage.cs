using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

public enum ToolCallStatus
{
    Pending,
    Running,
    Success,
    Failed,
}

public partial class ToolCallEntry : ObservableObject
{
    [ObservableProperty]
    private string _toolCallId = "";

    [ObservableProperty]
    private string _toolName = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _argumentsJson = "";

    [ObservableProperty]
    private string _resultJson = "";

    [ObservableProperty]
    private ToolCallStatus _status;

    [ObservableProperty]
    private bool _isExpanded;
}

public partial class ChatMessage : ObservableObject
{
    private bool _userCollapsedReasoning;

    [ObservableProperty]
    private string _role = "user";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isReasoningExpanded;

    [ObservableProperty]
    private bool _isFinalReply;

    /// <summary>出错或被取消时展示在 AI 输出位置的提示。</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public long DbId { get; set; }

    public string ToolCallId { get; set; } = string.Empty;

    public ObservableCollection<ToolCallEntry> ToolCalls { get; } = new();

    public bool IsThinking => IsStreaming && string.IsNullOrEmpty(Content);

    public bool HasReasoning => !string.IsNullOrEmpty(Reasoning);

    public bool HasToolCalls => ToolCalls.Count > 0;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    /// <summary>system 附件消息：用于界面显示的“文件名 · 大小”摘要（不包含完整 Markdown）。</summary>
    public string AttachmentSummary { get; set; } = string.Empty;

    public bool HasAttachmentSummary => !string.IsNullOrWhiteSpace(AttachmentSummary);

    public ChatMessage()
    {
        ToolCalls.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasToolCalls));
        };
    }

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(HasContent));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnReasoningChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
        if (!string.IsNullOrEmpty(value) && !_userCollapsedReasoning)
            IsReasoningExpanded = true;
    }

    partial void OnIsReasoningExpandedChanged(bool value)
    {
        if (!value) _userCollapsedReasoning = true;
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }
}