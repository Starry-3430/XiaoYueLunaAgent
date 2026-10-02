using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
                Messages.Add(new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.ReasoningContent,
                });
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

            var uiUserMsg = new ChatMessage { Role = "user", Content = userText };
            Messages.Add(uiUserMsg);

            IsBusy = true;
            Status = "思考中…";
            _cts = new CancellationTokenSource();

            var uiReply = new ChatMessage { Role = "assistant", Content = "" };
            Messages.Add(uiReply);

            try
            {
                var replyContent = await StreamIntoAsync(uiReply, _cts.Token);

                var assistantMsg = new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = replyContent,
                    ReasoningContent = uiReply.Reasoning,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                };
                await _messageRepo.InsertAsync(assistantMsg);

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

    private async Task<string> StreamIntoAsync(ChatMessage uiReply, CancellationToken ct)
    {
        var lockObj = new object();
        var buffer = new List<StreamEvent>();
        var contentBuf = new StringBuilder();
        var reasoningBuf = new StringBuilder();
        var pendingToolCalls = new Dictionary<int, ToolCallEntry>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            StreamEvent[] batch;
            lock (lockObj)
            {
                if (buffer.Count == 0) return;
                batch = buffer.ToArray();
                buffer.Clear();
            }
            foreach (var evt in batch)
            {
                switch (evt)
                {
                    case ReasoningDelta r:
                        reasoningBuf.Append(r.Text);
                        uiReply.Reasoning = reasoningBuf.ToString();
                        break;
                    case ContentDelta c:
                        contentBuf.Append(c.Text);
                        uiReply.Content = contentBuf.ToString();
                        break;
                    case ToolCallDelta t:
                        if (!pendingToolCalls.TryGetValue(t.Index, out var entry))
                        {
                            entry = new ToolCallEntry
                            {
                                ToolName = t.Name ?? "",
                                Status = ToolCallStatus.Running,
                            };
                            pendingToolCalls[t.Index] = entry;
                            uiReply.ToolCalls.Add(entry);
                        }
                        if (t.Name is not null) entry.ToolName = t.Name;
                        if (t.ArgumentsFragment is not null) entry.ArgumentsJson += t.ArgumentsFragment;
                        break;
                }
            }
        };

        try
        {
            uiReply.IsStreaming = true;

            var consumeTask = Task.Run(async () =>
            {
                await foreach (var evt in _aiService.ChatStreamAsync(Messages, ct))
                {
                    lock (lockObj)
                        buffer.Add(evt);
                }
            }, ct);

            timer.Start();
            await consumeTask;

            StreamEvent[] remaining;
            lock (lockObj)
            {
                remaining = buffer.ToArray();
                buffer.Clear();
            }
            foreach (var evt in remaining)
            {
                switch (evt)
                {
                    case ReasoningDelta r:
                        reasoningBuf.Append(r.Text);
                        break;
                    case ContentDelta c:
                        contentBuf.Append(c.Text);
                        break;
                    case ToolCallDelta t:
                        if (!pendingToolCalls.TryGetValue(t.Index, out var entry))
                        {
                            entry = new ToolCallEntry
                            {
                                ToolName = t.Name ?? "",
                                Status = ToolCallStatus.Running,
                            };
                            pendingToolCalls[t.Index] = entry;
                            uiReply.ToolCalls.Add(entry);
                        }
                        if (t.Name is not null) entry.ToolName = t.Name;
                        if (t.ArgumentsFragment is not null) entry.ArgumentsJson += t.ArgumentsFragment;
                        break;
                }
            }
            if (remaining.Length > 0)
            {
                uiReply.Content = contentBuf.ToString();
                uiReply.Reasoning = reasoningBuf.ToString();
            }

            foreach (var entry in pendingToolCalls.Values)
            {
                if (entry.Status == ToolCallStatus.Running)
                    entry.Status = ToolCallStatus.Success;
            }

            var toolCallsJson = pendingToolCalls.Count > 0
                ? JsonSerializer.Serialize(pendingToolCalls.Values.Select(tc => new { tc.ToolName, tc.ArgumentsJson }))
                : "";

            return uiReply.Content;
        }
        finally
        {
            uiReply.IsStreaming = false;
            timer.Stop();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (IsBusy) return;

        var index = Messages.IndexOf(msg);
        if (index < 0) return;

        while (Messages.Count > index)
            Messages.RemoveAt(index);

        string userText = "";
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                userText = Messages[i].Content;
                break;
            }
        }

        if (string.IsNullOrEmpty(userText)) return;

        IsBusy = true;
        Status = "思考中…";
        _cts = new CancellationTokenSource();

        try
        {
            var uiReply = new ChatMessage { Role = "assistant", Content = "" };
            Messages.Add(uiReply);

            await StreamIntoAsync(uiReply, _cts.Token);

            Status = "就绪";
        }
        catch (OperationCanceledException)
        {
            Status = "就绪（已取消）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重写失败");
            Status = "出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
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