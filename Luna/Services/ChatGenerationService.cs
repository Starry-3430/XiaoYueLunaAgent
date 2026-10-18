using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Localization;
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

    /// <summary>本会话已解锁的工具组（渐进披露）。</summary>
    public HashSet<string> ActiveToolGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = LocalizationService.Instance["Common_Status_Ready"];

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
    private readonly ToolPermissionRepository _auditRepo;
    private readonly TurnSummaryService _turnSummaryService;
    private readonly SessionTitleService _sessionTitleService;
    private readonly DiaryService _diaryService;
    private readonly ILogger<ChatGenerationService> _logger;

    private readonly ConcurrentDictionary<string, SessionRuntime> _runtimes = new(StringComparer.Ordinal);

    // 仅作为“防死循环”的安全上限；正常的多步工具调用（如代码执行）不会触及。
    private const int MaxSafetyToolRounds = 200;

    // 单轮对话的最大执行时间：防止模型卡死或不断刷工具。
    private static readonly TimeSpan MaxTurnDuration = TimeSpan.FromMinutes(10);

    // 连续出现相同工具调用（工具名 + 参数）的次数上限。
    private const int MaxRepeatedToolCalls = 2;

    // 只读/无副作用工具的最大并发数：太高容易触发搜索接口限流。
    private const int MaxConcurrentToolCalls = 3;

    /// <summary>
    /// 可安全并发执行的只读/无副作用工具白名单。
    /// 不在名单内的工具（如 write_file、powershell、send_notification、read_screen、ask_user）
    /// 会被视为屏障，串行执行，避免顺序依赖或共享状态冲突。
    /// </summary>
    private static readonly HashSet<string> ConcurrentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "file_tree", "search_files", "read_clipboard",
        "web_search", "fetch_url", "list_tasks", "list_facts", "diary_search", "turn_search",
    };

    public ChatGenerationService(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, AttachmentRepository attachmentRepo, ToolRegistry toolRegistry,
        ToolPermissionService toolPermission, ToolPermissionRepository auditRepo,
        TurnSummaryService turnSummaryService,
        SessionTitleService sessionTitleService,
        DiaryService diaryService, ILogger<ChatGenerationService> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _attachmentRepo = attachmentRepo;
        _toolRegistry = toolRegistry;
        _toolPermission = toolPermission;
        _auditRepo = auditRepo;
        _turnSummaryService = turnSummaryService;
        _sessionTitleService = sessionTitleService;
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
            AssignVersionMetadata(runtime.Messages);
            await RepairVersionGroupsAsync(runtime.Messages);
            MarkFinalReplies(runtime.Messages);
            UpdateRewriteEligibility(runtime.Messages);
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

    /// <summary>移除会话运行期缓存（会话被删除时调用）。</summary>
    public void RemoveRuntime(string sessionId)
    {
        if (!_runtimes.TryRemove(sessionId, out var runtime)) return;

        try { runtime.Cts?.Cancel(); } catch { /* 忽略 */ }
        runtime.Cts?.Dispose();
        runtime.Cts = null;
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

        // 并发发送时明确失败，避免上层误以为消息已发出而清空输入框。
        if (runtime.IsBusy)
            throw new InvalidOperationException(LocalizationService.Instance["Common_SendBusy"]);

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

        var userChatMessage = new ChatMessage { Role = "user", Content = userText, DbId = userMsgDbId };

        if (attachments is { Count: > 0 })
        {
            // 附件与本次轮次关联后落库
            foreach (var attachment in attachments)
            {
                attachment.SessionId = sessionId;
                attachment.TurnId = turnId;
                await _attachmentRepo.AddAsync(attachment);
            }

            // 图片：压缩后作为多模态图像附加到用户消息，交由 AI 的识图能力识别（不走本地工具）
            foreach (var attachment in attachments.Where(AttachmentHelper.IsImage))
            {
                var (success, dataUrl, imageError) =
                    await AttachmentHelper.TryBuildImageDataUrlAsync(attachment);
                if (success)
                    userChatMessage.Images.Add(dataUrl);
                else
                    _logger.LogWarning("图片附件无法作为图像发送：{File}（{Error}）", attachment.FileName, imageError);
            }
        }

        runtime.Messages.Add(userChatMessage);

        if (attachments is { Count: > 0 })
        {
            // 文档/其它附件把转换后的文本作为 system 消息注入会话（界面可见，AI 也会读取）
            var context = BuildAttachmentContext(attachments);
            if (!string.IsNullOrWhiteSpace(context))
            {
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
        }

        // 用户消息与附件上下文已经入库：立即通知会话列表，
        // 这样主界面在生成过程中打开/已打开时都能马上看到这次新对话。
        UpdateRewriteEligibility(runtime.Messages);
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage(sessionId));

        try
        {
            await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate);
        }
        catch (Exception ex)
        {
            // 用户消息已经入库：此后的生成/持久化失败不能再抛给调用方，
            // 否则界面会误以为“没发出去”而恢复草稿，导致重复发送。
            _logger.LogError(ex, "消息已入库，但后续生成流程失败：Session={SessionId} Turn={TurnId}",
                sessionId, turnId);
            runtime.Status = LocalizationService.Instance.Format("Common_Status_Error", ex.Message);
        }
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

            // 图片已作为图像随用户消息直接发送，这里只做展示，提示模型直接查看、不要读取文件
            if (AttachmentHelper.IsImage(a))
            {
                sb.AppendLine("（图片，已随本条消息作为图像直接发送，请直接查看图像内容，不要尝试读取本地文件。）");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(a.SourcePath))
                sb.AppendLine($"原始路径：{a.SourcePath}");
            sb.AppendLine();
            var body = string.IsNullOrWhiteSpace(a.InjectedMarkdown) ? a.ConvertedMarkdown : a.InjectedMarkdown;
            sb.AppendLine(string.IsNullOrWhiteSpace(body)
                ? "（未能提取到文本内容，如需可依据上面的原始路径读取该文件。）"
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

    /// <summary>重写最后一条 AI 回复：旧版本保留并隐藏，生成一个新版本。</summary>
    public async Task RewriteAsync(string sessionId, ChatMessage msg)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        var index = messages.IndexOf(msg);
        if (index < 0 || msg.Role != "assistant") return;

        // 目前只支持重写最后一条“激活”的回复，避免破坏其后的有效上下文。
        if (messages.Skip(index + 1).Any(m => m.IsActive))
        {
            _logger.LogWarning("目前仅支持重写最后一条 AI 回复");
            return;
        }

        var versionGroupId = msg.VersionGroupId ?? Guid.NewGuid().ToString("N");
        var existingVersions = messages
            .Where(m => m.Role == "assistant" &&
                        string.Equals(m.VersionGroupId, versionGroupId, StringComparison.Ordinal))
            .OrderBy(m => m.DbId)
            .ToList();

        if (!existingVersions.Contains(msg))
            existingVersions.Add(msg);

        var totalVersions = existingVersions.Count + 1;

        // 先把旧版本全部隐藏并归组；内存中保留，供 UI 切换。
        for (var i = 0; i < existingVersions.Count; i++)
        {
            var version = existingVersions[i];
            version.VersionGroupId = versionGroupId;
            version.VersionIndex = i + 1;
            version.TotalVersions = totalVersions;
            version.IsActive = false;

            if (version.DbId > 0)
                await _messageRepo.UpdateMessageVersionAsync(version.DbId, false, versionGroupId);
        }

        var turnId = Guid.NewGuid().ToString("N");
        var logicalDate = LogicalDate.Now();
        var messageCountBefore = messages.Count;

        var success = false;
        try
        {
            success = await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate,
                versionGroupId, totalVersions, totalVersions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重写 AI 回复失败");
        }

        if (!success)
        {
            // 清理本次失败尝试新增的消息：数据库标记为非激活，内存中移除，避免挡住后续重写。

            for (var i = messages.Count - 1; i >= messageCountBefore; i--)
            {
                var added = messages[i];
                if (added.DbId > 0)
                {
                    await _messageRepo.UpdateMessageVersionAsync(
                        added.DbId, false, added.VersionGroupId);
                }
                messages.RemoveAt(i);
            }

            // 恢复旧版本，避免会话没有可见回复。
            msg.IsActive = true;
            foreach (var version in existingVersions)
            {
                version.TotalVersions = existingVersions.Count;
                if (version.DbId > 0)
                {
                    await _messageRepo.UpdateMessageVersionAsync(
                        version.DbId, ReferenceEquals(version, msg), versionGroupId);
                }
            }

            if (existingVersions.Count == 1)
            {
                msg.VersionIndex = 1;
                msg.TotalVersions = 1;
            }

            UpdateRewriteEligibility(messages);
        }
    }

    /// <summary>在同一版本组内左右切换当前激活的回复，并同步数据库状态。</summary>
    public async Task SwitchVersionAsync(string sessionId, ChatMessage msg, int direction)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        if (string.IsNullOrEmpty(msg.VersionGroupId) || !messages.Contains(msg)) return;

        var versions = messages
            .Where(m => m.Role == "assistant" &&
                        string.Equals(m.VersionGroupId, msg.VersionGroupId, StringComparison.Ordinal))
            .OrderBy(m => m.VersionIndex)
            .ToList();
        if (versions.Count <= 1) return;

        var targetIndex = msg.VersionIndex + direction;
        if (targetIndex < 1 || targetIndex > versions.Count) return;

        var target = versions.FirstOrDefault(m => m.VersionIndex == targetIndex);
        if (target is null || ReferenceEquals(target, msg)) return;

        foreach (var version in versions)
            version.IsActive = ReferenceEquals(version, target);

        foreach (var version in versions.Where(v => v.DbId > 0))
            await _messageRepo.UpdateMessageVersionAsync(
                version.DbId, version.IsActive, version.VersionGroupId);

        MarkFinalReplies(messages);
        UpdateRewriteEligibility(messages);
    }

    private async Task<bool> RunAiLoopAsync(SessionRuntime runtime, string sessionId, string turnId, string logicalDate,
        string? versionGroupId = null, int versionIndex = 1, int totalVersions = 1)
    {
        runtime.IsBusy = true;
        runtime.Status = LocalizationService.Instance["Common_Status_Thinking"];
        runtime.Cts = new CancellationTokenSource();
        var ct = runtime.Cts.Token;
        var messages = runtime.Messages;

        // 单轮执行时间预算：覆盖模型流式输出与全部工具调用的总耗时。
        var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 工具死循环检测：记录上一轮调用签名与连续重复次数。
        string? lastToolCallSignature = null;
        var repeatedToolCallCount = 0;

        var replyContent = "";
        var replyReasoning = "";
        string? replyFinishReason = null;
        ChatMessage? lastAssistantMsg = null;
        ChatMessage? activeReply = null;

        try
        {
            var round = 0;
            while (!ct.IsCancellationRequested)
            {
                if (turnStopwatch.Elapsed > MaxTurnDuration)
                {
                    _logger.LogWarning("单轮执行超时（{Max} 分钟），强制结束", MaxTurnDuration.TotalMinutes);
                    throw new TimeoutException(LocalizationService.Instance["Common_TurnTimeout"]);
                }

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
                var activeGroups = runtime.ActiveToolGroups
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var (content, reasoning, pendingToolCalls, finishReason) =
                    await StreamOneRoundAsync(uiReply, snapshot, activeGroups, ct);

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
                    replyFinishReason = finishReason;

                    if (versionGroupId is not null)
                    {
                        uiReply.VersionGroupId = versionGroupId;
                        uiReply.VersionIndex = versionIndex;
                        uiReply.TotalVersions = totalVersions;
                    }

                    lastAssistantMsg = uiReply;
                    break;
                }

                if (ct.IsCancellationRequested) break;

                // 死循环检测：连续多次出现完全相同的工具名与参数时直接中断。
                var currentSignature = string.Join("|", pendingToolCalls.Values
                    .OrderBy(x => x.ToolName)
                    .ThenBy(x => x.ArgumentsJson)
                    .Select(x => $"{x.ToolName}:{x.ArgumentsJson}"));

                if (currentSignature == lastToolCallSignature)
                {
                    repeatedToolCallCount++;
                    if (repeatedToolCallCount >= MaxRepeatedToolCalls)
                    {
                        _logger.LogWarning("检测到工具死循环，强制中断：{Signature}", currentSignature);
                        throw new InvalidOperationException(LocalizationService.Instance["Common_ToolLoopDetected"]);
                    }
                }
                else
                {
                    lastToolCallSignature = currentSignature;
                    repeatedToolCallCount = 0;
                }

                runtime.Status = LocalizationService.Instance.Format("Common_Status_RunningTools", pendingToolCalls.Count);

                foreach (var kvp in pendingToolCalls)
                    kvp.Value.Status = ToolCallStatus.Running;

                var sortedCalls = pendingToolCalls.OrderBy(kv => kv.Key).ToList();

                // 批量审批：先扫描所有调用，把需要确认的工具聚合为一次弹窗。
                var toolsNeedingConfirmation = new List<(ITool Tool, ToolCallEntry Entry)>();
                var autoAllowedEntries = new HashSet<ToolCallEntry>();
                foreach (var (_, entry) in sortedCalls)
                {
                    var tool = _toolRegistry.GetTool(entry.ToolName);
                    if (tool is null || !_toolRegistry.IsEnabled(entry.ToolName)) continue;
                    if (!ToolPermissionService.NeedsConfirmation(tool)) continue;
                    if (await _toolPermission.IsAlwaysAllowedAsync(sessionId, tool.Name))
                    {
                        autoAllowedEntries.Add(entry);
                        continue;
                    }
                    if (IsUploadedAttachmentRead(tool, runtime, entry.ArgumentsJson)) continue;

                    toolsNeedingConfirmation.Add((tool, entry));
                }

                var permissionResults = new Dictionary<ToolCallEntry, string>();
                var deniedEntries = new HashSet<ToolCallEntry>();
                if (toolsNeedingConfirmation.Count > 0)
                {
                    var decision = _toolPermission.RequestBatchConfirmation(
                        sessionId, toolsNeedingConfirmation);

                    if (decision == ToolPermissionDecision.Deny)
                    {
                        foreach (var (tool, entry) in toolsNeedingConfirmation)
                        {
                            entry.Status = ToolCallStatus.Failed;
                            entry.ResultJson = "User Denied";
                            deniedEntries.Add(entry);
                            permissionResults[entry] = "Deny";

                            await AuditAsync(sessionId, tool.Name, entry.ArgumentsJson, "Deny", "Denied");
                        }

                        _logger.LogInformation("用户拒绝执行工具（共 {Count} 个）", toolsNeedingConfirmation.Count);
                    }
                    else
                    {
                        var permissionResult = decision == ToolPermissionDecision.AllowOnce
                            ? "AllowOnce"
                            : "AllowPersistent";

                        foreach (var (_, entry) in toolsNeedingConfirmation)
                            permissionResults[entry] = permissionResult;

                        if (decision == ToolPermissionDecision.AllowAlways)
                        {
                            await _toolPermission.ApplyDecisionAsync(
                                sessionId, toolsNeedingConfirmation.Select(x => x.Tool), decision);
                        }
                    }
                }

                // ====== 分组并发执行 ======
                // 只有连续的只读工具会并行；遇到写操作/命令等屏障时先等待前面的批次完成。
                using var concurrencySemaphore = new SemaphoreSlim(MaxConcurrentToolCalls);
                var completedEntries = new HashSet<ToolCallEntry>();
                var executableCalls = sortedCalls
                    .Select(call => call.Value)
                    .Where(entry => !deniedEntries.Contains(entry));
                var batches = GroupCallsIntoBatches(executableCalls);

                foreach (var batch in batches)
                {
                    if (ct.IsCancellationRequested) break;

                    var tasks = new List<Task<(ToolCallEntry Entry, ToolResult Result)>>(batch.Count);
                    foreach (var entry in batch)
                        tasks.Add(ExecuteToolIsolatedAsync(entry, concurrencySemaphore, ct));

                    var results = await Task.WhenAll(tasks);

                    // 结果在 UI 线程按原顺序写回条目，避免跨线程绑定问题。
                    foreach (var (entry, result) in results)
                    {
                        entry.ResultJson = TruncateResult(result.Content);
                        entry.Status = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Success;
                        completedEntries.Add(entry);

                        // enable_tool_group 本身不执行业务，只把组名写入会话运行时，
                        // 下一轮 StreamOneRoundAsync 会把该组工具一起下发给模型。
                        if (!result.IsError &&
                            string.Equals(entry.ToolName, "enable_tool_group", StringComparison.OrdinalIgnoreCase))
                        {
                            var group = ToolArgumentFormatter.TryGetStringArgument(entry.ArgumentsJson, "group_name");
                            if (!string.IsNullOrWhiteSpace(group))
                            {
                                runtime.ActiveToolGroups.Add(group.Trim().ToLowerInvariant());
                                _logger.LogInformation("已解锁工具组：{Group}", group);
                            }
                        }

                        var permissionResult = permissionResults.GetValueOrDefault(entry,
                            autoAllowedEntries.Contains(entry) ? "AllowPersistent" : "NotRequired");
                        var executionResult = result.IsTimeout ? "Timeout"
                            : result.IsCanceled ? "Canceled"
                            : result.IsError ? "Failed"
                            : "Success";
                        await AuditAsync(sessionId, entry.ToolName, entry.ArgumentsJson,
                            permissionResult, executionResult, result.IsError ? result.Content : null);
                    }
                }

                // 取消导致未执行的调用也要给出结果，保证每条 tool_call 都有对应的 tool 消息。
                foreach (var (_, entry) in sortedCalls)
                {
                    if (completedEntries.Contains(entry) || deniedEntries.Contains(entry)) continue;
                    entry.Status = ToolCallStatus.Failed;
                    entry.ResultJson = LocalizationService.Instance["Common_Canceled"];

                    var permissionResult = permissionResults.GetValueOrDefault(entry,
                        autoAllowedEntries.Contains(entry) ? "AllowPersistent" : "NotRequired");
                    await AuditAsync(sessionId, entry.ToolName, entry.ArgumentsJson,
                        permissionResult, "Canceled", entry.ResultJson);
                }

                // 所有结果按原始调用顺序写入会话，包含被用户拒绝的占位结果。
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
            runtime.Status = LocalizationService.Instance["Common_Status_ReadyCanceled"];
            if (activeReply is not null)
            {
                activeReply.IsStreaming = false;
                await PersistNoticeAsync(sessionId, turnId, logicalDate, activeReply, LocalizationService.Instance["Common_Canceled"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 回复失败");
            runtime.Status = LocalizationService.Instance.Format("Common_Status_Error", ex.Message);
            if (activeReply is not null)
            {
                activeReply.IsStreaming = false;
                await PersistNoticeAsync(sessionId, turnId, logicalDate, activeReply, LocalizationService.Instance.Format("Common_SendFailed", ex.Message));
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
            // 达到输出上限被截断：在回复末尾追加红色提示（同时持久化，重开会话仍可见）
            var truncated = IsOutputTruncated(replyFinishReason);
            if (truncated)
                lastAssistantMsg.ErrorMessage = OutputTruncatedNotice;

            var id = await _messageRepo.InsertAsync(new Message
            {
                SessionId = sessionId,
                TurnId = turnId,
                Role = "assistant",
                Content = replyContent,
                ReasoningContent = replyReasoning,
                ErrorMessage = truncated ? OutputTruncatedNotice : string.Empty,
                ContentType = truncated ? "error" : "text",
                IsActive = lastAssistantMsg.IsActive,
                VersionGroupId = lastAssistantMsg.VersionGroupId,
                CreatedAtUtc = DateTime.UtcNow,
                LogicalDate = logicalDate,
            });
            lastAssistantMsg.DbId = id;

            // 成功结束后异步生成轮次摘要，不阻塞界面
            _turnSummaryService.Trigger(sessionId, turnId, logicalDate);

            // 顺带触发一次日记检查（补齐历史积压；当天不会提前生成）
            _diaryService.Trigger();

            // 首轮完成且尚无标题时，异步生成会话标题
            var session = await _sessionRepo.GetByIdAsync(sessionId);
            if (session is not null && string.IsNullOrWhiteSpace(session.Title))
            {
                var firstUserText = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
                _sessionTitleService.Trigger(sessionId, firstUserText, replyContent);
            }
        }

        await _sessionRepo.TouchAsync(sessionId);
        runtime.Status = LocalizationService.Instance["Common_Status_Ready"];
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage(sessionId));
        UpdateRewriteEligibility(messages);
        return lastAssistantMsg is not null && !string.IsNullOrWhiteSpace(lastAssistantMsg.Content);
    }

    /// <summary>达到输出上限被截断时展示在 AI 输出末尾的提示。</summary>
    private static string OutputTruncatedNotice =>
        LocalizationService.Instance["Common_OutputTruncated"];

    /// <summary>判断流式结束原因是否表示被输出上限截断（不同提供商用词不一）。</summary>
    private static bool IsOutputTruncated(string? finishReason)
        => finishReason is not null &&
           (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
            finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) ||
            finishReason.Equals("max_output_tokens", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 持久化一条“提示”消息（已取消 / 发送失败）。正文留空、错误信息写入 ErrorMessage 列，
    /// 因此它会在会话中一直显示，但不会作为 AI 上下文（OpenAiService 会跳过正文为空的 assistant 消息）。
    /// </summary>
    private async Task PersistNoticeAsync(string sessionId, string turnId, string logicalDate,
        ChatMessage msg, string text)
    {
        msg.ErrorMessage = text;

        var id = await _messageRepo.InsertAsync(new Message
        {
            SessionId = sessionId,
            TurnId = turnId,
            Role = "assistant",
            Content = string.Empty,
            ErrorMessage = text,
            ContentType = "error",
            CreatedAtUtc = DateTime.UtcNow,
            LogicalDate = logicalDate,
        });
        msg.DbId = id;
    }

    private async Task<(string content, string reasoning, Dictionary<int, ToolCallEntry> pendingToolCalls, string? finishReason)>
        StreamOneRoundAsync(ChatMessage uiReply, IReadOnlyList<ChatMessage> messages,
            IReadOnlySet<string> activeToolGroups, CancellationToken ct)
    {
        var lockObj = new object();
        var buffer = new List<StreamEvent>();
        var contentBuf = new StringBuilder();
        var reasoningBuf = new StringBuilder();
        var pendingToolCalls = new Dictionary<int, ToolCallEntry>();
        string? finishReason = null;
        var requestOptions = new AiRequestOptions { ActiveToolGroups = activeToolGroups };
        // 批次定时器用 Input 优先级：高于 Background 的 Markdown pump，
        // 这样文档变大、pump 变重时也不会把流式文本的更新节奏饿死（避免 16ms 拖成几百 ms 的大块）。
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };

        // 把一批事件合并处理：文本只在外层累加，批次结束后统一 set 一次。
        // 这样 ChatMessage.Content/Reasoning 的 PropertyChanged 从“每个 token 一次”降为“每批一次”。
        void ApplyBatch(StreamEvent[] batch)
        {
            var contentChanged = false;
            var reasoningChanged = false;

            foreach (var evt in batch)
            {
                switch (evt)
                {
                    case ReasoningDelta r:
                        reasoningBuf.Append(r.Text);
                        reasoningChanged = true;
                        break;
                    case ContentDelta c:
                        contentBuf.Append(c.Text);
                        contentChanged = true;
                        break;
                    case StreamFinish f:
                        finishReason = f.Reason;
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

            if (contentChanged)
                uiReply.Content = contentBuf.ToString();
            if (reasoningChanged)
                uiReply.Reasoning = reasoningBuf.ToString();
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
            ApplyBatch(batch);
        };

        try
        {
            uiReply.IsStreaming = true;

            var consumeTask = Task.Run(async () =>
            {
                await foreach (var evt in _aiService.ChatStreamAsync(messages, requestOptions, ct))
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
            ApplyBatch(remaining);
        }
        finally
        {
            uiReply.IsStreaming = false;
            timer.Stop();
        }

        return (uiReply.Content, uiReply.Reasoning, pendingToolCalls, finishReason);
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
            ErrorMessage = m.ErrorMessage,
            IsActive = m.IsActive,
            VersionGroupId = m.VersionGroupId,
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
        foreach (var message in messages)
            message.IsFinalReply = false;

        var active = messages.Where(m => m.IsActive).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            var m = active[i];
            if (m.Role != "assistant") continue;

            var isTurnEnd = i == active.Count - 1 || active[i + 1].Role == "user";
            if (isTurnEnd && (!string.IsNullOrEmpty(m.Content) || !string.IsNullOrEmpty(m.ErrorMessage)))
                m.IsFinalReply = true;
        }
    }

    /// <summary>
    /// 只有“最后一条激活的最终 AI 回复”允许重写，避免历史消息上出现无效按钮。
    /// </summary>
    private static void UpdateRewriteEligibility(ObservableCollection<ChatMessage> messages)
    {
        var lastActive = messages.LastOrDefault(m => m.IsActive);
        var target = lastActive is { Role: "assistant", IsFinalReply: true } ? lastActive : null;

        foreach (var message in messages)
            message.CanRewrite = ReferenceEquals(message, target);
    }

    /// <summary>按版本分组计算 V1/V2 序号与总数，保证重启后 UI 显示正确。</summary>
    private static void AssignVersionMetadata(ObservableCollection<ChatMessage> messages)
    {
        var groups = messages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.VersionGroupId))
            .GroupBy(m => m.VersionGroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var versions = group.OrderBy(m => m.DbId).ToList();
            for (var i = 0; i < versions.Count; i++)
            {
                versions[i].VersionIndex = i + 1;
                versions[i].TotalVersions = versions.Count;
            }
        }
    }

    /// <summary>
    /// 崩溃恢复：如果某个版本组没有任何激活版本（例如重写途中应用退出），
    /// 自动激活最新版本，避免会话完全没有可见回复。
    /// </summary>
    private async Task RepairVersionGroupsAsync(IReadOnlyList<ChatMessage> messages)
    {
        var groups = messages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.VersionGroupId))
            .GroupBy(m => m.VersionGroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            if (group.Any(m => m.IsActive)) continue;

            var latest = group.OrderByDescending(m => m.DbId).First();
            latest.IsActive = true;
            if (latest.DbId > 0)
                await _messageRepo.UpdateMessageVersionAsync(latest.DbId, true, latest.VersionGroupId);
        }
    }

    private string GetToolDisplayName(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return "";
        return _toolRegistry.GetTool(toolName)?.DisplayName ?? toolName;
    }

    /// <summary>
    /// 把工具调用按“可并发”与“屏障”切分为连续批次。
    /// 例如 [读A, 读B, 写C, 读D] → [[读A, 读B], [写C], [读D]]。
    /// </summary>
    private static List<List<ToolCallEntry>> GroupCallsIntoBatches(IEnumerable<ToolCallEntry> calls)
    {
        var batches = new List<List<ToolCallEntry>>();
        List<ToolCallEntry>? currentBatch = null;

        foreach (var entry in calls)
        {
            if (ConcurrentTools.Contains(entry.ToolName))
            {
                currentBatch ??= [];
                currentBatch.Add(entry);
                continue;
            }

            // 屏障工具：先把已累积的并发批次收尾，再让屏障单独成批（保证前后顺序）。
            if (currentBatch is { Count: > 0 })
            {
                batches.Add(currentBatch);
                currentBatch = null;
            }

            batches.Add([entry]);
        }

        if (currentBatch is { Count: > 0 })
            batches.Add(currentBatch);

        return batches;
    }

    /// <summary>
    /// 执行单个工具（受并发信号量限制），只返回结果，不修改会话/条目状态。
    /// 条目状态由调用方在 UI 线程统一写回，避免跨线程绑定问题。
    /// </summary>
    private async Task<(ToolCallEntry Entry, ToolResult Result)> ExecuteToolIsolatedAsync(
        ToolCallEntry entry, SemaphoreSlim semaphore, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(ct);
            acquired = true;
            ct.ThrowIfCancellationRequested();

            var tool = _toolRegistry.GetTool(entry.ToolName);
            if (tool is null || !_toolRegistry.IsEnabled(entry.ToolName))
            {
                _logger.LogWarning("未启用或未注册的工具: {Name}", entry.ToolName);
                return (entry, ToolResult.Error(LocalizationService.Instance["Common_Tool_NotEnabled"]));
            }

            using var argsDoc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(entry.ArgumentsJson) ? "{}" : entry.ArgumentsJson);
            var result = await ExecuteToolWithTimeoutAsync(tool, argsDoc.RootElement.Clone(), ct);

            if (result.IsError)
                _logger.LogWarning("工具 {Name} 返回错误: {Result}", entry.ToolName, result.Content);
            else
                _logger.LogInformation("工具 {Name} 执行成功", entry.ToolName);

            return (entry, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (entry, ToolResult.Canceled(LocalizationService.Instance["Common_Canceled"]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工具 {Name} 执行失败", entry.ToolName);
            return (entry, ToolResult.Error(
                LocalizationService.Instance.Format("Common_Tool_ExecFailed", ex.Message)));
        }
        finally
        {
            if (acquired)
                semaphore.Release();
        }
    }

    /// <summary>
    /// 在工具自身超时之外再加一层兜底：超过 <see cref="ITool.ExecutionTimeout"/> 仍未返回时，
    /// 取消工具并把“超时”作为工具结果交还给模型，让对话继续，而不是卡死整个 AI 循环。
    /// </summary>
    private async Task<ToolResult> ExecuteToolWithTimeoutAsync(ITool tool, JsonElement args, CancellationToken ct)
    {
        var timeout = tool.ExecutionTimeout;
        var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        toolCts.CancelAfter(timeout);

        var execution = tool.ExecuteAsync(args, toolCts.Token);
        var delay = Task.Delay(Timeout.InfiniteTimeSpan, toolCts.Token);
        var completed = await Task.WhenAny(execution, delay);

        if (ReferenceEquals(completed, execution))
        {
            try
            {
                return await execution;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 工具自己响应了超时取消，走下面的超时结果。
            }
            finally
            {
                toolCts.Dispose();
            }
        }
        else
        {
            toolCts.Cancel();

            // 工具可能不响应取消：不要等待它，但要在它最终结束时释放 CTS 并观察异常。
            _ = execution.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "工具 {Name} 在超时取消后仍抛出了异常", tool.Name);
                toolCts.Dispose();
            }, TaskScheduler.Default);
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogWarning("工具 {Name} 超过 {Seconds} 秒未完成，已中止", tool.Name, timeout.TotalSeconds);
        return ToolResult.Timeout(LocalizationService.Instance.Format(
            "Common_Tool_Timeout", tool.DisplayName, timeout.TotalSeconds));
    }

    /// <summary>写入工具审计日志；失败只记录警告，不影响工具执行与对话。</summary>
    private async Task AuditAsync(string sessionId, string toolName, string argumentsJson,
        string permissionResult, string executionResult, string? errorMessage = null)
    {
        try
        {
            await _auditRepo.AddAuditLogAsync(sessionId, toolName, argumentsJson,
                permissionResult, executionResult, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入工具审计日志失败：{Tool}", toolName);
        }
    }

    private static string TruncateResult(string result, int maxLen = 5000)
    {
        if (result.Length <= maxLen) return result;
        return result[..maxLen] + "\n\n" + LocalizationService.Instance["Common_Tool_ResultTruncated"];
    }

    /// <summary>
    /// 若工具是 read_file，且目标路径对应用户在本会话中主动上传的附件，则返回 true（无需弹窗）。
    /// </summary>
    private static bool IsUploadedAttachmentRead(ITool tool, SessionRuntime runtime, string argumentsJson)
    {
        if (!ToolArgumentFormatter.IsReadFile(tool.Name)) return false;

        var path = ToolArgumentFormatter.TryGetStringArgument(argumentsJson, "path");
        return IsUploadedAttachmentPath(runtime, path);
    }

    /// <summary>
    /// 判断路径是否是本会话中用户上传过的附件原始路径。
    /// 附件上下文以 system 消息持久化，内容中包含“原始路径：...”行，重启后依然可用。
    /// </summary>
    private static bool IsUploadedAttachmentPath(SessionRuntime runtime, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string target;
        try
        {
            target = Path.GetFullPath(path.Trim().Trim('"', '\''));
        }
        catch
        {
            return false;
        }

        const string prefix = "原始路径：";

        foreach (var message in runtime.Messages)
        {
            if (message.Role != "system" || string.IsNullOrEmpty(message.Content)) continue;

            foreach (var line in message.Content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var candidate = trimmed[prefix.Length..].Trim().Trim('"', '\'');
                if (candidate.Length == 0) continue;

                try
                {
                    if (string.Equals(Path.GetFullPath(candidate), target, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 忽略非法路径行
                }
            }
        }

        return false;
    }

    private sealed record ToolCallEntryDb(
        string ToolCallId,
        string ToolName,
        string ArgumentsJson,
        string ResultJson
    );
}
