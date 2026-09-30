using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();

    public HomeViewModel(IAiService aiService, ILogger<HomeViewModel> logger)
    {
        _aiService = aiService;
        _logger = logger;

        for (var i = 0; i < 10; i++)
        {
            ChatSessions.Add(new ChatSessionItem
            {
                Id = i.ToString(),
                Title = $"标题标题标题 {i + 1}",
                Preview = $"这是第 {i + 1} 个历史对话的预览内容..."
            });
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        Messages.Clear();
        InputText = string.Empty;
        Status = "就绪";
    }

    [RelayCommand]
    private void SelectSession(ChatSessionItem? session)
    {
        if (session is null) return;
        Messages.Clear();
        Messages.Add(new ChatMessage { Role = "user", Content = $"进入对话: {session.Title}" });
        Messages.Add(new ChatMessage { Role = "assistant", Content = "这是一个历史对话的模拟回复。" });
        Status = "就绪";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsBusy) return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        Messages.Add(new ChatMessage { Role = "user", Content = userText });

        var reply = new ChatMessage { Role = "assistant", Content = string.Empty };
        Messages.Add(reply);

        IsBusy = true;
        Status = "思考中…";
        _cts = new CancellationTokenSource();

        try
        {
            await foreach (var chunk in _aiService.ChatStreamAsync(Messages, _cts.Token))
            {
                reply.Content += chunk;
            }
            Status = "就绪";
        }
        catch (OperationCanceledException)
        {
            Status = "就绪（已取消）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送失败");
            Status = "出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }
}

public class ChatSessionItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
}