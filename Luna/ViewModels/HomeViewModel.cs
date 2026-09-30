using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services;

namespace Luna.ViewModels;

public partial class HomeViewModel : ObservableObject, IRecipient<SessionUpdateMessage>
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _cts;
    private string? _currentSessionId;
    private readonly Dictionary<string, string> _sessionDrafts = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ChatSessionItem? _selectedSession;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();
    public ObservableCollection<SettingsItem> SettingsItems { get; } = new();

    public HomeViewModel(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, ILogger<HomeViewModel> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _logger = logger;

        WeakReferenceMessenger.Default.Register<SessionUpdateMessage>(this);
        _ = LoadSessionsAsync();

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

    private async Task LoadSessionsAsync()
    {
        try
        {
            var sessions = await _sessionRepo.GetAllAsync();
            var selectedId = SelectedSession?.Id;
            ChatSessions.Clear();
            foreach (var s in sessions)
            {
                var item = new ChatSessionItem
                {
                    Id = s.Id,
                    Title = s.Title ?? "新对话",
                    Preview = s.Preview ?? "",
                };
                ChatSessions.Add(item);
                if (item.Id == selectedId)
                    SelectedSession = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话列表失败");
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        SaveCurrentDraft();
        Messages.Clear();
        InputText = string.Empty;
        Status = "就绪";
        _currentSessionId = null;
        SelectedSession = null;
    }

    [RelayCommand]
    private async Task SelectSession(ChatSessionItem? session)
    {
        if (session is null) return;

        SaveCurrentDraft();

        _currentSessionId = session.Id;
        SelectedSession = session;
        Messages.Clear();

        if (_sessionDrafts.TryGetValue(session.Id, out var draft))
            InputText = draft;
        else
            InputText = string.Empty;

        try
        {
            var msgs = await _messageRepo.GetBySessionAsync(session.Id);
            foreach (var m in msgs)
            {
                Messages.Add(new ChatMessage { Role = m.Role, Content = m.Content });
            }
            Status = "就绪";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话消息失败");
            Status = "加载失败";
        }
    }

    private void SaveCurrentDraft()
    {
        if (_currentSessionId is not null)
            _sessionDrafts[_currentSessionId] = InputText;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsBusy) return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            // 1. 创建会话（如无）
            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;

                ChatSessions.Insert(0, new ChatSessionItem
                {
                    Id = session.Id,
                    Title = "新对话",
                    Preview = userText,
                });
            }

            // 2. 生成 TurnId
            var turnId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var logicalDate = now.ToString("yyyy-MM-dd");

            // 3. 写 user 消息
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

            var uiUserMsg = new ChatMessage { Role = "user", Content = userText };
            Messages.Add(uiUserMsg);

            IsBusy = true;
            Status = "思考中…";
            _cts = new CancellationTokenSource();

            // 4. 调用 AI 流式回复
            var replyContent = string.Empty;
            var uiReply = new ChatMessage { Role = "assistant", Content = "" };
            Messages.Add(uiReply);

            try
            {
                var buffer = new StringBuilder();
                var lastFlush = DateTime.UtcNow;

                await foreach (var chunk in _aiService.ChatStreamAsync(Messages, _cts.Token))
                {
                    buffer.Append(chunk);

                    if ((DateTime.UtcNow - lastFlush).TotalMilliseconds > 100 ||
                        buffer.Length >= 80)
                    {
                        replyContent += buffer.ToString();
                        uiReply.Content = replyContent;
                        lastFlush = DateTime.UtcNow;
                        buffer.Clear();
                    }
                }

                if (buffer.Length > 0)
                {
                    replyContent += buffer.ToString();
                    uiReply.Content = replyContent;
                }

                // 5. 写 assistant 消息
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

                // 6. 刷新会话时间并重新加载列表（保证 DB 中的 UpdatedAtUtc 排序）
                await _sessionRepo.TouchAsync(_currentSessionId);
                await LoadSessionsAsync();

                Status = "就绪";
            }
            catch (OperationCanceledException)
            {
                Status = "就绪（已取消）";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 回复失败");
                Status = "出错：" + ex.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送消息失败");
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

    public void Receive(SessionUpdateMessage message)
    {
        _ = LoadSessionsAsync();
    }
}

public class ChatSessionItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
}