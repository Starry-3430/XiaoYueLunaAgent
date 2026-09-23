using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainViewModel(IAiService aiService, ILogger<MainViewModel> logger)
    {
        _aiService = aiService;
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsBusy)
        {
            return;
        }

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
            Status = "已取消";
            _logger.LogInformation("请求被取消");
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