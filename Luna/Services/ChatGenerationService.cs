using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Models;
using Luna.Services.Data;
using Luna.Services.Tools;

namespace Luna.Services;

/// <summary>
/// 单个会话的运行期状态。消息集合本身就是提供给界面与 AI 请求的同一份数据，
/// 因此切换界面/会话时不会丢失正在流式输出的内容，生成也完全在后台继续。
/// </summary>
public sealed partial class SessionRuntime : ObservableObject
{
    public string SessionId { get; }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "就绪";

    internal CancellationTokenSource? Cts;
    internal bool Loaded;
    internal readonly SemaphoreSlim LoadLock = new(1, 1);

    public SessionRuntime(string sessionId) => SessionId = sessionId;
}

/// <summary>
/// 会话生成服务：按会话维护独立的消息集合 / 忙碌状态 / 取消令牌，并执行 AI 循环。
/// 生成与界面解耦——切换窗口或会话时，后台仍持续输出并写库，回到界面即可看到最新内容；
/// 不同会话可以同时并发生成。
/// </summary>
public sealed class ChatGenerationService
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AttachmentRepository _attachmentRepo;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolPermissionService _toolPermission;
    private readonly TurnSummaryService _turnSummaryService;
    private readonly DiaryService _diaryService;
    private readonly ILogger<ChatGenerationService> _logger;

    private readonly ConcurrentDictionary<string, SessionRuntime> _runtimes = new(StringComparer.Ordinal);

    // 仅作为“防死循环”的安全上限；正常的多步工具调用（如代码执行）不会触及。
    private const int MaxSafetyToolRounds = 200;

    public ChatGenerationService(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, AttachmentRepository attachmentRepo, ToolRegistry toolRegistry,
        ToolPermissionService toolPermission, TurnSummaryService turnSummaryService,
        DiaryService diaryService, ILogger<ChatGenerationService> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _attachmentRepo = attachmentRepo;
        _toolRegistry = toolRegistry;
        _toolPermission = toolPermission;
        _turnSummaryService = turnSummaryService;
        _diaryService = diaryService;
        _logger = logger;
    }

    /// <summary>获取（必要时创建）会话运行期对象，不会从数据库加载。</summary>
    public SessionRuntime Get(string sessionId)
        => _runtimes.GetOrAdd(sessionId, id => new SessionRuntime(id));

    /// <summary>获取会话运行期对象，首次访问时从数据库加载历史消息。</summary>
    public async Task<SessionRuntime> GetOrLoadAsync(string sessionId)
    {
        var runtime = Get(sessionId);
        if (runtime.Loaded) return runtime;

        await runtime.LoadLock.WaitAsync();
        try
        {
            if (runtime.Loaded) return runtime;

            var msgs = await _messageRepo.GetBySessionAsync(sessionId);
            runtime.Messages.Clear();
            foreach (var m in msgs)
                runtime.Messages.Add(MapToChatMessage(m));
            MarkFinalReplies(runtime.Messages);
            runtime.Loaded = true;
        }
        finally
        {
            runtime.LoadLock.Release();
        }

        return runtime;
    }

    /// <summary>取消指定会话正在进行的生成。</summary>
    public void Cancel(string sessionId)
    {
        try { Get(sessionId).Cts?.Cancel(); }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 发送用户消息并启动（后台）生成。用户消息在写库后立即加入运行期集合。
    /// </summary>
    public Task SendAsync(string sessionId, string userText)
        => SendAsync(sessionId, userText, null);

    /// <summary>
    /// 发送用户消息并启动（后台）生成。<paramref name="attachments"/> 非空时，
    /// 先把附件记录落库，并把转换后的 Markdown 作为 system 消息注入会话供 AI 参考。
    /// </summary>
    public async Task SendAsync(string sessionId, string userText, IReadOnlyList<Attachment>? attachments)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var turnId = Guid.NewGuid().ToString("N");
        var logicalDate = LogicalDate.Now();

        var userMsg = new Message
        {
            SessionId = sessionId,
            TurnId = turnId,
            Role = "user",
            Content = userText,
            CreatedAtUtc = DateTime.UtcNow,
            LogicalDate = logicalDate,
        };
        var userMsgDbId = await _messageRepo.InsertAsync(userMsg);

        runtime.Messages.Add(new ChatMessage { Role = "user", Content = userText, DbId = userMsgDbId });

        if (attachments is { Count: > 0 })
        {
            // 附件与本次轮次关联后落库
            foreach (var attachment in attachments)
            {
                attachment.SessionId = sessionId;
                attachment.TurnId = turnId;
                await _attachmentRepo.AddAsync(attachment);
            }

            // 把转换后的 Markdown 作为 system 消息注入会话（界面可见，AI 也会读取）
            var context = BuildAttachmentContext(attachments);
            var systemMsg = new Message
            {
                SessionId = sessionId,
                TurnId = turnId,
                Role = "system",
                Content = context,
                CreatedAtUtc = DateTime.UtcNow,
                LogicalDate = logicalDate,
            };
            var systemMsgDbId = await _messageRepo.InsertAsync(systemMsg);
            runtime.Messages.Add(new ChatMessage
            {
                Role = "system",
                Content = context,
                AttachmentSummary = ExtractAttachmentSummary(context),
                DbId = systemMsgDbId,
            });
        }

        await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate);
    }

    /// <summary>把附件转换结果拼装成注入会话的 system 内容。</summary>
    private static string BuildAttachmentContext(IReadOnlyList<Attachment> attachments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("用户上传了以下附件，请结合其内容回答后续问题。");
        foreach (var a in attachments)
        {
            sb.AppendLine();
            sb.AppendLine($"### 附件：{a.FileName} · {AttachmentHelper.FormatSize(a.FileSize)}");
            sb.AppendLine();
            var body = string.IsNullOrWhiteSpace(a.InjectedMarkdown) ? a.ConvertedMarkdown : a.InjectedMarkdown;
            sb.AppendLine(string.IsNullOrWhiteSpace(body)
                ? "（未能提取到文本内容）"
                : body);
        }
        return sb.ToString().TrimEnd();
    }

    private const string AttachmentHeaderPrefix = "### 附件：";

    /// <summary>从 system 附件内容中提取“文件名 · 大小”摘要，用于界面展示。</summary>
    private static string ExtractAttachmentSummary(string content)
    {
        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith(AttachmentHeaderPrefix, StringComparison.Ordinal))
            .Select(l => l[AttachmentHeaderPrefix.Length..].Trim());
        return string.Join("\n", lines);
    }

    /// <summary>重写某条 AI 回复：截断其后内容并重新生成。</summary>
    public async Task RewriteAsync(string sessionId, ChatMessage msg)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        var index = messages.IndexOf(msg);
        if (index < 0) return;

        string userText = "";
        int userIndex = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user")
            {
                userText = messages[i].Content;
                userIndex = i;
                break;
            }
        }

        if (string.IsNullOrEmpty(userText) || userIndex < 0) return;

        var userDbId = messages[userIndex].DbId;

        while (messages.Count > userIndex)
            messages.RemoveAt(userIndex);

        var turnId = Guid.NewGuid().ToString("N");
        var logicalDate = LogicalDate.Now();

        if (userDbId > 0)
        {
            await _messageRepo.DeleteAfterAsync(sessionId, userDbId);
            // 复用的用户消息重新归属到新轮次，并清理旧轮次残留摘要
            await _messageRepo.ReassignTurnAsync(userDbId, turnId, logicalDate);
            await _turnSummaryService.DeleteOrphanedAsync(sessionId);
        }

        messages.Add(new ChatMessage { Role = "user", Content = userText, DbId = userDbId });

        await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate);
    }

    private async Task RunAiLoopAsync(SessionRuntime runtime, string sessionId, string turnId, string logicalDate)
    {
        runtime.IsBusy = true;
        runtime.Status = "思考中…";
        runtime.Cts = new CancellationTokenSource();
        var ct = runtime.Cts.Token;
        var messages = runtime.Messages;

        var replyContent = "";
        var replyReasoning = "";
        ChatMessage? lastAssistantMsg = null;
        ChatMessage? activeReply = null;

        try
        {
            var round = 0;
            while (!ct.IsCancellationRequested)
            {
                if (++round > MaxSafetyToolRounds)
                {
                    _logger.LogWarning("工具轮次达到安全上限 {Max}，提前结束", MaxSafetyToolRounds);
                    break;
                }

                var previousReply = activeReply;
                var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
                messages.Add(uiReply);
                activeReply = uiReply;

                // 以快照调用接口，避免流式枚举期间集合被界面/其它逻辑修改
                var snapshot = messages.ToList();
                var (content, reasoning, pendingToolCalls) =
                    await StreamOneRoundAsync(uiReply, snapshot, ct);

                if (pendingToolCalls.Count == 0)
                {
                    // 工具调用后模型没有产出正文：丢弃这个空回复，
                    // 让上一条（含工具调用/正文）作为最终回复，保证“复制/重写”按钮出现。
                    if (string.IsNullOrWhiteSpace(content) &&
                        string.IsNullOrWhiteSpace(reasoning) &&
                        previousReply is not null)
                    {
                        messages.Remove(uiReply);
                        activeReply = previousReply;
                        lastAssistantMsg = null;
                        break;
                    }

                    replyContent = content;
                    replyReasoning = reasoning;
                    lastAssistantMsg = uiReply;
                    break;
                }

                if (ct.IsCancellationRequested) break;

                runtime.Status = $"执行工具 ({pendingToolCalls.Count})…";

                foreach (var kvp in pendingToolCalls)
                    kvp.Value.Status = ToolCallStatus.Running;

                var sortedCalls = pendingToolCalls.OrderBy(kv => kv.Key).ToList();
                foreach (var (index, entry) in sortedCalls)
                {
                    if (ct.IsCancellationRequested) break;

                    var tool = _toolRegistry.GetTool(entry.ToolName);
                    if (tool is null || !_toolRegistry.IsEnabled(entry.ToolName))
                    {
                        entry.Status = ToolCallStatus.Failed;
                        entry.ResultJson = "工具未启用";
                        _logger.LogWarning("未启用或未注册的工具: {Name}", entry.ToolName);
                        continue;
                    }

                    // 中/高风险工具在执行前需要用户授权
                    if (ToolPermissionService.NeedsConfirmation(tool) &&
                        !_toolPermission.IsAlwaysAllowed(sessionId, tool.Name))
                    {
                        var decision = _toolPermission.RequestConfirmation(tool, entry.ArgumentsJson);
                        if (decision == ToolPermissionDecision.Deny)
                        {
                            entry.Status = ToolCallStatus.Failed;
                            entry.ResultJson = "User Denied";
                            _logger.LogInformation("用户拒绝执行工具 {Name}", entry.ToolName);
                            continue;
                        }
                        if (decision == ToolPermissionDecision.AllowAlways)
                            _toolPermission.AllowForConversation(sessionId, tool.Name);
                    }

                    try
                    {
                        using var argsDoc = JsonDocument.Parse(
                            string.IsNullOrWhiteSpace(entry.ArgumentsJson) ? "{}" : entry.ArgumentsJson);
                        var result = await tool.ExecuteAsync(argsDoc.RootElement.Clone(), ct);
                        entry.ResultJson = TruncateResult(result.Content);
                        entry.Status = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Success;
                        if (result.IsError)
                            _logger.LogWarning("工具 {Name} 返回错误: {Result}", entry.ToolName, result.Content);
                        else
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
                    messages.Add(new ChatMessage
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
                    SessionId = sessionId,
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
                        SessionId = sessionId,
                        TurnId = turnId,
                        Role = "tool",
                        Content = entry.ResultJson,
                        ToolCallId = entry.ToolCallId,
                        ContentType = "tool_result",
                        CreatedAtUtc = DateTime.UtcNow,
                        LogicalDate = logicalDate,
                    });
                }

                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
            runtime.Status = "就绪（已取消）";
            if (activeReply is not null)
            {
                activeReply.IsStreaming = false;
                activeReply.ErrorMessage = "已取消";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 回复失败");
            runtime.Status = "出错：" + ex.Message;
            if (activeReply is not null)
            {
                activeReply.IsStreaming = false;
                activeReply.ErrorMessage = "出错：" + ex.Message;
            }
        }
        finally
        {
            // 达到最大工具轮次 / 取消 / 出错而没有最终文本回复时，
            // 把最后一条回复标记为最终，避免“复制/重写”按钮消失。
            var finalReply = lastAssistantMsg ?? activeReply;
            if (finalReply is not null)
            {
                finalReply.IsStreaming = false;
                finalReply.IsFinalReply = true;
            }

            runtime.IsBusy = false;
            runtime.Cts?.Dispose();
            runtime.Cts = null;
        }

        if (lastAssistantMsg is not null)
        {
            await _messageRepo.InsertAsync(new Message
            {
                SessionId = sessionId,
                TurnId = turnId,
                Role = "assistant",
                Content = replyContent,
                ReasoningContent = replyReasoning,
                CreatedAtUtc = DateTime.UtcNow,
                LogicalDate = logicalDate,
            });

            // 成功结束后异步生成轮次摘要，不阻塞界面
            _turnSummaryService.Trigger(sessionId, turnId, logicalDate);

            // 顺带触发一次日记检查（补齐历史积压；当天不会提前生成）
            _diaryService.Trigger();
        }

        await _sessionRepo.TouchAsync(sessionId);
        runtime.Status = "就绪";
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage());
    }

    private async Task<(string content, string reasoning, Dictionary<int, ToolCallEntry> pendingToolCalls)>
        StreamOneRoundAsync(ChatMessage uiReply, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lockObj = new object();
        var buffer = new List<StreamEvent>();
        var contentBuf = new StringBuilder();
        var reasoningBuf = new StringBuilder();
        var pendingToolCalls = new Dictionary<int, ToolCallEntry>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        void ApplyEvent(StreamEvent evt)
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
                ApplyEvent(evt);
        };

        try
        {
            uiReply.IsStreaming = true;

            var consumeTask = Task.Run(async () =>
            {
                await foreach (var evt in _aiService.ChatStreamAsync(messages, ct))
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
                ApplyEvent(evt);
        }
        finally
        {
            uiReply.IsStreaming = false;
            timer.Stop();
        }

        return (uiReply.Content, uiReply.Reasoning, pendingToolCalls);
    }

    private ChatMessage MapToChatMessage(Message m)
    {
        var msg = new ChatMessage
        {
            Role = m.Role,
            Content = m.Content,
            Reasoning = m.ReasoningContent,
            DbId = m.Id,
            ToolCallId = m.ToolCallId,
        };

        if (m.Role == "system")
            msg.AttachmentSummary = ExtractAttachmentSummary(m.Content);

        if (!string.IsNullOrEmpty(m.ToolCallsJson))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<ToolCallEntryDb>>(m.ToolCallsJson);
                if (entries is not null)
                {
                    foreach (var e in entries)
                    {
                        msg.ToolCalls.Add(new ToolCallEntry
                        {
                            ToolCallId = e.ToolCallId,
                            ToolName = e.ToolName,
                            DisplayName = _toolRegistry.GetTool(e.ToolName)?.DisplayName ?? e.ToolName,
                            ArgumentsJson = e.ArgumentsJson,
                            ResultJson = e.ResultJson,
                            Status = ToolCallStatus.Success,
                        });
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "解析 ToolCallsJson 失败");
            }
        }

        return msg;
    }

    private static void MarkFinalReplies(ObservableCollection<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role != "assistant") continue;

            var isTurnEnd = i == messages.Count - 1 || messages[i + 1].Role == "user";
            if (isTurnEnd && !string.IsNullOrEmpty(m.Content))
                m.IsFinalReply = true;
        }
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

    private sealed record ToolCallEntryDb(
        string ToolCallId,
        string ToolName,
        string ArgumentsJson,
        string ResultJson
    );
}
