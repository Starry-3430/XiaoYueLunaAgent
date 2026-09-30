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

    public ObservableCollection<SettingsItem> SettingsItems { get; } = new();

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

        SettingsItems.Add(new ToggleSetting
        {
            Key = "streamOutput", Title = "流式输出",
            Description = "启用后 AI 回复将逐字显示。",
            Value = true,
        });
        SettingsItems.Add(new NumberSetting
        {
            Key = "timeout", Title = "超时时间",
            Description = "模型请求的超时时间（毫秒）。",
            DisplayText = "300000", DefaultValue = 300000,
            Min = 1000, Max = 3600000, Step = 1000, Unit = "ms",
        });
        SettingsItems.Add(new SelectSetting
        {
            Key = "proxyMode", Title = "代理模式",
            Description = "当前插件的代理设置模式。",
            DefaultValue = "global",
            Options =
            [
                new SelectOption { Value = "global", Label = "遵循 ChatLuna 主插件的全局代理设置" },
                new SelectOption { Value = "disabled", Label = "禁用代理" },
                new SelectOption { Value = "custom", Label = "使用自定义代理设置" },
            ],
        });
        ((SelectSetting)SettingsItems[^1]).ResetCommand.Execute(null);
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