using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _cts;
    private string? _currentSessionId;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainViewModel(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, ILogger<MainViewModel> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsBusy) return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;
            }

            var turnId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var logicalDate = now.ToString("yyyy-MM-dd");

            var userMsg = new Message
            {
                SessionId = _currentSessionId,
                TurnId = turnId,
                Role = "user",
                Content = userText,
                CreatedAtUtc = now,
                LogicalDate = logicalDate,
            };
            await _messageRepo.InsertAsync(userMsg);

            Messages.Add(new ChatMessage { Role = "user", Content = userText });

            var replyContent = string.Empty;
            var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty };
            Messages.Add(uiReply);

            IsBusy = true;
            Status = "思考中…";
            _cts = new CancellationTokenSource();

            try
            {
                await foreach (var chunk in _aiService.ChatStreamAsync(Messages, _cts.Token))
                {
                    replyContent += chunk;
                    uiReply.Content = replyContent;
                }

                var assistantMsg = new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = replyContent,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                };
                await _messageRepo.InsertAsync(assistantMsg);

                await _sessionRepo.TouchAsync(_currentSessionId);

                WeakReferenceMessenger.Default.Send(new SessionUpdateMessage());
                Status = "就绪";
            }
            catch (OperationCanceledException)
            {
                Status = "就绪（已取消）";
                _logger.LogInformation("请求被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 回复失败");
                Status = "出错：" + ex.Message;
            }
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