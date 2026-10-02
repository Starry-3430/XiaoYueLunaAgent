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
using Luna.Services.Tools;

namespace Luna.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _cts;
    private string? _currentSessionId;

    private const int MaxToolRounds = 3;

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
        MessageRepository messageRepo, ToolRegistry toolRegistry,
        ILogger<MainViewModel> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _toolRegistry = toolRegistry;
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
            long userMsgDbId = await _messageRepo.InsertAsync(userMsg);

            Messages.Add(new ChatMessage { Role = "user", Content = userText, DbId = userMsgDbId });

            await RunAiLoopAsync(turnId, logicalDate);
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

    private async Task RunAiLoopAsync(string turnId, string logicalDate)
    {
        IsBusy = true;
        Status = "思考中…";
        _cts = new CancellationTokenSource();

        var replyContent = "";
        var replyReasoning = "";
        ChatMessage? lastAssistantMsg = null;

        try
        {
            for (var round = 0; round < MaxToolRounds; round++)
            {
                var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
                Messages.Add(uiReply);

                var (content, reasoning, pendingToolCalls) =
                    await StreamOneRoundAsync(uiReply, _cts.Token);

                if (pendingToolCalls.Count == 0)
                {
                    replyContent = content;
                    replyReasoning = reasoning;
                    lastAssistantMsg = uiReply;
                    break;
                }

                if (_cts.IsCancellationRequested) break;

                Status = $"执行工具 ({pendingToolCalls.Count})…";

                foreach (var kvp in pendingToolCalls)
                    kvp.Value.Status = ToolCallStatus.Running;

                var sortedCalls = pendingToolCalls.OrderBy(kv => kv.Key).ToList();
                foreach (var (index, entry) in sortedCalls)
                {
                    if (_cts.IsCancellationRequested) break;

                    var tool = _toolRegistry.GetTool(entry.ToolName);
                    if (tool is null || !_toolRegistry.IsEnabled(entry.ToolName))
                    {
                        entry.Status = ToolCallStatus.Failed;
                        entry.ResultJson = "工具未启用";
                        _logger.LogWarning("未启用或未注册的工具: {Name}", entry.ToolName);
                        continue;
                    }

                    try
                    {
                        var result = await tool.ExecuteAsync(entry.ArgumentsJson, _cts.Token);
                        entry.ResultJson = TruncateResult(result);
                        entry.Status = ToolCallStatus.Success;
                        _logger.LogInformation("工具 {Name} 执行成功", entry.ToolName);
                    }
                    catch (Exception ex)
                    {
                        entry.Status = ToolCallStatus.Failed;
                        entry.ResultJson = $"执行失败: {ex.Message}";
                        _logger.LogError(ex, "工具 {Name} 执行失败", entry.ToolName);
                    }
                }

                foreach (var (_, entry) in sortedCalls)
                {
                    Messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        Content = entry.ResultJson,
                        ToolCallId = entry.ToolCallId,
                        ToolCalls = { new ToolCallEntry { ToolCallId = entry.ToolCallId, ToolName = entry.ToolName } },
                    });
                }

                var toolCallsJson = JsonSerializer.Serialize(
                    sortedCalls.Select(kv => new { kv.Value.ToolCallId, kv.Value.ToolName, kv.Value.ArgumentsJson, kv.Value.ResultJson }));

                await _messageRepo.InsertAsync(new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoning,
                    ToolCallsJson = toolCallsJson,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                });

                foreach (var (_, entry) in sortedCalls)
                {
                    await _messageRepo.InsertAsync(new Message
                    {
                        SessionId = _currentSessionId,
                        TurnId = turnId,
                        Role = "tool",
                        Content = entry.ResultJson,
                        ToolCallId = entry.ToolCallId,
                        ContentType = "tool_result",
                        CreatedAtUtc = DateTime.UtcNow,
                        LogicalDate = logicalDate,
                    });
                }

                if (_cts.IsCancellationRequested) break;
            }
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
            if (lastAssistantMsg is not null)
            {
                lastAssistantMsg.IsStreaming = false;
                lastAssistantMsg.IsFinalReply = true;
            }
        }

        if (lastAssistantMsg is not null)
        {
            await _messageRepo.InsertAsync(new Message
            {
                SessionId = _currentSessionId,
                TurnId = turnId,
                Role = "assistant",
                Content = replyContent,
                ReasoningContent = replyReasoning,
                CreatedAtUtc = DateTime.UtcNow,
                LogicalDate = logicalDate,
            });
        }

        await _sessionRepo.TouchAsync(_currentSessionId);
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage());
        Status = "就绪";
    }

    private async Task<(string content, string reasoning, Dictionary<int, ToolCallEntry> pendingToolCalls)>
        StreamOneRoundAsync(ChatMessage uiReply, CancellationToken ct)
    {
        var contentBuf = new StringBuilder();
        var reasoningBuf = new StringBuilder();
        var pendingToolCalls = new Dictionary<int, ToolCallEntry>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

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
                                DisplayName = GetToolDisplayName(t.Name),
                                Status = ToolCallStatus.Pending,
                            };
                            pendingToolCalls[t.Index] = entry;
                            uiReply.ToolCalls.Add(entry);
                        }
                        if (t.Id is not null)
                            entry.ToolCallId = t.Id;
                        if (t.Name is not null)
                        {
                            entry.ToolName = t.Name;
                            entry.DisplayName = GetToolDisplayName(t.Name);
                        }
                        if (t.ArgumentsFragment is not null)
                            entry.ArgumentsJson += t.ArgumentsFragment;
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
                    lock (_chunkLock)
                        _chunkBuffer.Add(evt);
                }
            }, ct);

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
                                DisplayName = GetToolDisplayName(t.Name),
                                Status = ToolCallStatus.Pending,
                            };
                            pendingToolCalls[t.Index] = entry;
                            uiReply.ToolCalls.Add(entry);
                        }
                        if (t.Id is not null) entry.ToolCallId = t.Id;
                        if (t.Name is not null)
                        {
                            entry.ToolName = t.Name;
                            entry.DisplayName = GetToolDisplayName(t.Name);
                        }
                        if (t.ArgumentsFragment is not null) entry.ArgumentsJson += t.ArgumentsFragment;
                        break;
                }
            }
            if (remaining.Length > 0)
            {
                uiReply.Content = contentBuf.ToString();
                uiReply.Reasoning = reasoningBuf.ToString();
            }
        }
        finally
        {
            uiReply.IsStreaming = false;
            timer.Stop();
        }

        return (uiReply.Content, uiReply.Reasoning, pendingToolCalls);
    }

    private string GetToolDisplayName(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return "";
        return _toolRegistry.GetTool(toolName)?.DisplayName ?? toolName;
    }

    private static string TruncateResult(string result, int maxLen = 5000)
    {
        if (result.Length <= maxLen) return result;
        return result[..maxLen] + "\n\n… (结果已截断)";
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

        string userText = "";
        int userIndex = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                userText = Messages[i].Content;
                userIndex = i;
                break;
            }
        }

        if (string.IsNullOrEmpty(userText) || userIndex < 0) return;

        var userDbId = Messages[userIndex].DbId;

        while (Messages.Count > userIndex)
            Messages.RemoveAt(userIndex);

        try
        {
            if (_currentSessionId is not null && userDbId > 0)
                await _messageRepo.DeleteAfterAsync(_currentSessionId, userDbId);

            var turnId = Guid.NewGuid().ToString("N");
            var logicalDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            Messages.Add(new ChatMessage { Role = "user", Content = userText, DbId = userDbId });
            await RunAiLoopAsync(turnId, logicalDate);
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