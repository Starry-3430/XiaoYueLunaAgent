using System.Collections.ObjectModel;
using System.Text;
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

    private readonly object _chunkLock = new();
    private readonly List<StreamEvent> _chunkBuffer = new();

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

            var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
            Messages.Add(uiReply);

            IsBusy = true;
            Status = "思考中…";
            _cts = new CancellationTokenSource();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            var contentBuf = new StringBuilder();
            var reasoningBuf = new StringBuilder();
            var pendingToolCalls = new Dictionary<int, ToolCallEntry>();

            timer.Tick += (_, _) =>
            {
                StreamEvent[] batch;
                lock (_chunkLock)
                {
                    if (_chunkBuffer.Count == 0) return;
                    batch = _chunkBuffer.ToArray();
                    _chunkBuffer.Clear();
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

                            if (t.Name is not null)
                                entry.ToolName = t.Name;
                            if (t.ArgumentsFragment is not null)
                                entry.ArgumentsJson += t.ArgumentsFragment;
                            break;
                    }
                }
            };

            try
            {
                var consumeTask = Task.Run(() => ConsumeStreamAsync(_cts.Token), _cts.Token);

                timer.Start();
                await consumeTask;

                StreamEvent[] remaining;
                lock (_chunkLock)
                {
                    remaining = _chunkBuffer.ToArray();
                    _chunkBuffer.Clear();
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

                var replyContent = uiReply.Content;
                var reasoningContent = uiReply.Reasoning;
                var toolCallsJson = pendingToolCalls.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(pendingToolCalls.Values.Select(tc => new { tc.ToolName, tc.ArgumentsJson }))
                    : "";

                var assistantMsg = new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = replyContent,
                    ReasoningContent = reasoningContent,
                    ToolCallsJson = toolCallsJson,
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
            finally
            {
                uiReply.IsStreaming = false;
                timer.Stop();
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

    private async Task ConsumeStreamAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _aiService.ChatStreamAsync(Messages, ct))
            {
                lock (_chunkLock)
                    _chunkBuffer.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // normal cancellation
        }
    }

    public void NewChat()
    {
        Messages.Clear();
        InputText = string.Empty;
        Status = "就绪";
        _currentSessionId = null;
    }

    public async Task RewriteMessageAsync(ChatMessage msg)
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

            var replyContent = await StreamIntoAsync(uiReply, _cts.Token);

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
}