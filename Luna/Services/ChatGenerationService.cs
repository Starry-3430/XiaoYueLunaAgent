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
    private readonly FileStorageService _fileStorage;
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
        MessageRepository messageRepo, AttachmentRepository attachmentRepo,
        FileStorageService fileStorage, ToolRegistry toolRegistry,
        ToolPermissionService toolPermission, ToolPermissionRepository auditRepo,
        TurnSummaryService turnSummaryService,
        SessionTitleService sessionTitleService,
        DiaryService diaryService, ILogger<ChatGenerationService> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _attachmentRepo = attachmentRepo;
        _fileStorage = fileStorage;
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

        var userChatMessage = new ChatMessage
        {
            Role = "user",
            Content = userText,
            DbId = userMsgDbId,
            TurnId = turnId,
        };

        if (attachments is { Count: > 0 })
        {
            // 附件与本次轮次关联后落库
            foreach (var attachment in attachments)
            {
                attachment.SessionId = sessionId;
                attachment.TurnId = turnId;
                await _attachmentRepo.AddAsync(attachment);
            }

            await EnforceAttachmentStorageLimitAsync();

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
                    TurnId = turnId,
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

    /// <summary>
    /// 附件存储只保留最新 <see cref="AttachmentHelper.MaxStoredCount"/> 条记录；
    /// 新附件成功入库后调用，删除最旧记录并清理不再被引用的 blob 文件。
    /// </summary>
    private async Task EnforceAttachmentStorageLimitAsync()
    {
        try
        {
            var removed = await _attachmentRepo.TrimToLimitAsync(AttachmentHelper.MaxStoredCount);
            if (removed.Count == 0) return;

            foreach (var attachment in removed)
            {
                // 内容寻址下多个记录可能指向同一个 blob；仅在没有其他记录引用时删除文件。
                if (string.IsNullOrEmpty(attachment.Sha256)) continue;
                if (await _attachmentRepo.GetBySha256Async(attachment.Sha256) is not null) continue;
                _fileStorage.DeleteBlob(attachment.Sha256);
            }

            _logger.LogInformation("附件存储超过上限，已删除最旧的 {Count} 个附件记录", removed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理超限附件存储失败");
        }
    }

    /// <summary>一次版本分支的公共准备结果。</summary>
    private sealed record BranchPreparation(
        string VersionGroupId,
        int OldVersionIndex,
        int NewVersionIndex,
        int TotalVersions,
        ChatMessage UserMessage,
        List<ChatMessage> BranchMessages);

    /// <summary>
    /// 把当前激活分支（用户消息 + 附件上下文 + 全部 AI 生成内容）归档到旧版本并隐藏。
    /// 新版本会重新创建用户消息副本，保证切换版本时用户消息与 AI 回答一起恢复。
    /// </summary>
    private async Task<BranchPreparation?> PrepareBranchAsync(
        ObservableCollection<ChatMessage> messages, ChatMessage userMessage)
    {
        var userIndex = messages.IndexOf(userMessage);
        if (userIndex < 0 || userMessage.Role != "user") return null;

        var branch = new List<ChatMessage>();
        for (var i = userIndex; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!message.IsActive) continue;
            if (i > userIndex && message.Role == "user") break;
            if (message.Role is "user" or "system" or "assistant" or "tool")
                branch.Add(message);
        }

        var versionGroupId = userMessage.VersionGroupId;
        if (string.IsNullOrEmpty(versionGroupId))
        {
            versionGroupId = branch
                .Select(m => m.VersionGroupId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id))
                ?? Guid.NewGuid().ToString("N");
        }

        var existingIndexes = messages
            .Where(m => string.Equals(m.VersionGroupId, versionGroupId, StringComparison.Ordinal))
            .Select(m => m.VersionIndex)
            .Where(i => i > 0)
            .Distinct()
            .ToList();

        var oldVersionIndex = userMessage.VersionGroupId is not null && userMessage.VersionIndex > 0
            ? userMessage.VersionIndex
            : branch
                .Where(m => string.Equals(m.VersionGroupId, versionGroupId, StringComparison.Ordinal))
                .Select(m => m.VersionIndex)
                .Where(i => i > 0)
                .DefaultIfEmpty(1)
                .Max();

        var newVersionIndex = (existingIndexes.Count > 0 ? existingIndexes.Max() : 0) + 1;
        if (newVersionIndex <= oldVersionIndex)
            newVersionIndex = oldVersionIndex + 1;
        var totalVersions = Math.Max(newVersionIndex,
            existingIndexes.Count > 0 ? existingIndexes.Max() : 0);

        foreach (var message in branch)
        {
            message.VersionGroupId = versionGroupId;
            message.VersionIndex = oldVersionIndex;
            message.TotalVersions = totalVersions;
            message.IsActive = false;

            if (message.DbId > 0)
            {
                await _messageRepo.UpdateMessageVersionAsync(
                    message.DbId, false, versionGroupId, oldVersionIndex);
            }
        }

        return new BranchPreparation(versionGroupId, oldVersionIndex, newVersionIndex,
            totalVersions, userMessage, branch);
    }

    private async Task<ChatMessage> InsertBranchUserAsync(ObservableCollection<ChatMessage> messages,
        string sessionId, string turnId, string logicalDate, string content,
        IEnumerable<string> images, BranchPreparation branch)
    {
        var user = new ChatMessage
        {
            Role = "user",
            Content = content,
            TurnId = turnId,
            VersionGroupId = branch.VersionGroupId,
            VersionIndex = branch.NewVersionIndex,
            TotalVersions = branch.TotalVersions,
        };

        foreach (var image in images)
            user.Images.Add(image);

        messages.Add(user);
        user.DbId = await _messageRepo.InsertAsync(new Message
        {
            SessionId = sessionId,
            TurnId = turnId,
            Role = "user",
            Content = content,
            IsActive = true,
            VersionGroupId = branch.VersionGroupId,
            VersionIndex = branch.NewVersionIndex,
            CreatedAtUtc = DateTime.UtcNow,
            LogicalDate = logicalDate,
        });
        return user;
    }

    /// <summary>把旧轮次的附件元数据复制到新轮次，保证后续编辑新版本时仍能恢复附件。</summary>
    private async Task CopyTurnAttachmentsAsync(string sessionId, string newTurnId, string sourceTurnId)
    {
        if (string.IsNullOrEmpty(sourceTurnId) || string.Equals(sourceTurnId, newTurnId, StringComparison.Ordinal))
            return;

        var attachments = await _attachmentRepo.GetByTurnAsync(sourceTurnId);
        foreach (var source in attachments)
        {
            await _attachmentRepo.AddAsync(new Attachment
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                TurnId = newTurnId,
                FileName = source.FileName,
                FileExtension = source.FileExtension,
                FileSize = source.FileSize,
                Sha256 = source.Sha256,
                StoredPath = source.StoredPath,
                ConvertedMarkdown = source.ConvertedMarkdown,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await EnforceAttachmentStorageLimitAsync();
    }

    private async Task InsertBranchSystemAsync(ObservableCollection<ChatMessage> messages,
        string sessionId, string turnId, string logicalDate, string content,
        string attachmentSummary, BranchPreparation branch)
    {
        var dbId = await _messageRepo.InsertAsync(new Message
        {
            SessionId = sessionId,
            TurnId = turnId,
            Role = "system",
            Content = content,
            IsActive = true,
            VersionGroupId = branch.VersionGroupId,
            VersionIndex = branch.NewVersionIndex,
            CreatedAtUtc = DateTime.UtcNow,
            LogicalDate = logicalDate,
        });

        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = content,
            AttachmentSummary = attachmentSummary,
            DbId = dbId,
            TurnId = turnId,
            VersionGroupId = branch.VersionGroupId,
            VersionIndex = branch.NewVersionIndex,
            TotalVersions = branch.TotalVersions,
        });
    }

    /// <summary>新版本生成失败时删除本次新增内容，并恢复旧版本。</summary>
    private async Task CleanupFailedBranchAsync(ObservableCollection<ChatMessage> messages,
        BranchPreparation branch, string newTurnId)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var added = messages[i];
            if (!string.Equals(added.VersionGroupId, branch.VersionGroupId, StringComparison.Ordinal) ||
                added.VersionIndex != branch.NewVersionIndex)
                continue;

            if (added.DbId > 0)
                await _messageRepo.DeleteAsync(added.DbId);
            messages.RemoveAt(i);
        }

        // 失败分支的附件也不应留下，避免附件管理页出现孤立记录。
        await _attachmentRepo.DeleteByTurnAsync(newTurnId);

        foreach (var old in messages.Where(m =>
                     string.Equals(m.VersionGroupId, branch.VersionGroupId, StringComparison.Ordinal) &&
                     m.VersionIndex == branch.OldVersionIndex))
        {
            old.IsActive = true;
            if (old.DbId > 0)
            {
                await _messageRepo.UpdateMessageVersionAsync(
                    old.DbId, true, branch.VersionGroupId, branch.OldVersionIndex);
            }
        }
    }

    /// <summary>
    /// 重写最后一条 AI 回复：把当前分支（用户消息、附件上下文和全部 AI 生成内容）
    /// 整体归档为旧版本，再基于同一用户输入生成完整的新版本。
    /// </summary>
    public async Task RewriteAsync(string sessionId, ChatMessage msg)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        var index = messages.IndexOf(msg);
        if (index < 0 || msg.Role != "assistant") return;

        // 只支持最后一轮的最终回复；同一轮中最终回复之后可能还有 tool 消息，
        // 它们会随本轮一起归入旧版本，不能因此拒绝重写。
        if (!msg.IsFinalReply ||
            messages.Skip(index + 1).Any(m => m.IsActive && m.Role == "user"))
        {
            _logger.LogWarning("目前仅支持重写最后一条 AI 回复");
            return;
        }

        var userIndex = -1;
        for (var i = index; i >= 0; i--)
        {
            if (messages[i].Role == "user" && messages[i].IsActive)
            {
                userIndex = i;
                break;
            }
        }
        if (userIndex < 0) return;

        var userMessage = messages[userIndex];
        var branch = await PrepareBranchAsync(messages, userMessage);
        if (branch is null) return;

        var turnId = Guid.NewGuid().ToString("N");
        var logicalDate = LogicalDate.Now();

        var success = false;
        try
        {
            // 新版本沿用原用户输入与附件上下文，保证切换版本时用户消息一致。
            await CopyTurnAttachmentsAsync(sessionId, turnId, branch.UserMessage.TurnId);
            await InsertBranchUserAsync(messages, sessionId, turnId, logicalDate,
                branch.UserMessage.Content, branch.UserMessage.Images, branch);
            foreach (var system in branch.BranchMessages.Where(m => m.Role == "system"))
            {
                await InsertBranchSystemAsync(messages, sessionId, turnId, logicalDate,
                    system.Content, system.AttachmentSummary, branch);
            }

            success = await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate,
                branch.VersionGroupId, branch.NewVersionIndex, branch.TotalVersions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重写 AI 回复失败");
        }

        if (!success)
            await CleanupFailedBranchAsync(messages, branch, turnId);

        AssignVersionMetadata(messages);
        MarkFinalReplies(messages);
        UpdateRewriteEligibility(messages);
    }

    /// <summary>
    /// 编辑最后一条用户消息并重新生成：旧分支（含旧用户消息与 AI 回答）整体归档，
    /// 新用户消息、附件上下文和新 AI 回答共享新的版本序号。
    /// </summary>
    public async Task EditUserMessageAsync(string sessionId, ChatMessage userMsg, string newText,
        IReadOnlyList<Attachment>? attachments)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        var index = messages.IndexOf(userMsg);
        if (index < 0 || userMsg.Role != "user") return;

        // 只能编辑当前会话最后一条激活的用户消息，不能修改历史轮次。
        var lastActiveUser = messages.LastOrDefault(m => m.IsActive && m.Role == "user");
        if (!ReferenceEquals(lastActiveUser, userMsg)) return;

        var branch = await PrepareBranchAsync(messages, userMsg);
        if (branch is null) return;

        var turnId = Guid.NewGuid().ToString("N");
        var logicalDate = LogicalDate.Now();
        var preparedAttachments = attachments?.ToList() ?? new List<Attachment>();

        var generated = false;
        try
        {
            foreach (var attachment in preparedAttachments)
            {
                // 每次编辑发送都创建新的附件记录，避免复用旧行主键或影响旧版本附件。
                attachment.Id = Guid.NewGuid().ToString("N");
                attachment.SessionId = sessionId;
                attachment.TurnId = turnId;
                await _attachmentRepo.AddAsync(attachment);
            }

            if (preparedAttachments.Count > 0)
                await EnforceAttachmentStorageLimitAsync();

            var newUser = await InsertBranchUserAsync(messages, sessionId, turnId, logicalDate,
                newText, [], branch);

            if (preparedAttachments.Count > 0)
            {
                foreach (var attachment in preparedAttachments.Where(AttachmentHelper.IsImage))
                {
                    var (success, dataUrl, error) = await AttachmentHelper.TryBuildImageDataUrlAsync(attachment);
                    if (success)
                        newUser.Images.Add(dataUrl);
                    else
                        _logger.LogWarning("图片附件无法作为图像发送：{File}（{Error}）", attachment.FileName, error);
                }

                var context = BuildAttachmentContext(preparedAttachments);
                if (!string.IsNullOrWhiteSpace(context))
                {
                    await InsertBranchSystemAsync(messages, sessionId, turnId, logicalDate,
                        context, ExtractAttachmentSummary(context), branch);
                }
            }

            generated = await RunAiLoopAsync(runtime, sessionId, turnId, logicalDate,
                branch.VersionGroupId, branch.NewVersionIndex, branch.TotalVersions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "编辑用户消息后生成失败");
        }

        if (!generated)
            await CleanupFailedBranchAsync(messages, branch, turnId);

        AssignVersionMetadata(messages);
        MarkFinalReplies(messages);
        UpdateRewriteEligibility(messages);
    }

    /// <summary>在同一版本组内左右切换当前激活的回复（连同其工具调用历史），并同步数据库状态。</summary>
    public async Task SwitchVersionAsync(string sessionId, ChatMessage msg, int direction)
    {
        var runtime = await GetOrLoadAsync(sessionId);
        if (runtime.IsBusy) return;

        var messages = runtime.Messages;
        if (string.IsNullOrEmpty(msg.VersionGroupId) || !messages.Contains(msg)) return;

        var groupMessages = messages
            .Where(m => string.Equals(m.VersionGroupId, msg.VersionGroupId, StringComparison.Ordinal))
            .ToList();

        var indexes = groupMessages
            .Select(m => m.VersionIndex)
            .Where(i => i > 0)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        if (indexes.Count <= 1) return;

        var position = indexes.IndexOf(msg.VersionIndex);
        if (position < 0) return;

        var targetPosition = position + direction;
        if (targetPosition < 0 || targetPosition >= indexes.Count) return;

        var targetIndex = indexes[targetPosition];

        foreach (var version in groupMessages)
            version.IsActive = version.VersionIndex == targetIndex;

        foreach (var version in groupMessages.Where(v => v.DbId > 0))
            await _messageRepo.UpdateMessageVersionAsync(
                version.DbId, version.IsActive, version.VersionGroupId, version.VersionIndex);

        AssignVersionMetadata(messages);
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
        var cancelled = ct.IsCancellationRequested;

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
                if (versionGroupId is not null)
                {
                    uiReply.VersionGroupId = versionGroupId;
                    uiReply.VersionIndex = versionIndex;
                    uiReply.TotalVersions = totalVersions;
                }
                messages.Add(uiReply);
                activeReply = uiReply;

                // 以快照调用接口，避免流式枚举期间集合被界面/其它逻辑修改
                var snapshot = messages.ToList();
                var activeGroups = runtime.ActiveToolGroups
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var (content, reasoning, pendingToolCalls, finishReason) =
                    await StreamOneRoundAsync(uiReply, snapshot, activeGroups, ct);

                // 流式读取在取消时可能正常结束（yield break），不会抛异常；
                // 必须显式识别，否则会把半截内容当成正常回复，导致取消提示与操作按钮丢失。
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

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
                        _logger.LogWarning("检测到重复工具循环，强制中断");
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

                            await AuditAsync(sessionId, tool.Name, "Deny", "Denied");
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
                        await AuditAsync(sessionId, entry.ToolName,
                            permissionResult, executionResult);
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
                    await AuditAsync(sessionId, entry.ToolName,
                        permissionResult, "Canceled");
                }

                // 所有结果按原始调用顺序写入会话，包含被用户拒绝的占位结果。
                // 工具消息与对应 assistant 同属一个回答版本，重试/切换时一起隐藏或恢复。
                var toolChatMessages = new List<ChatMessage>(sortedCalls.Count);
                foreach (var (_, entry) in sortedCalls)
                {
                    var toolChatMessage = new ChatMessage
                    {
                        Role = "tool",
                        Content = entry.ResultJson,
                        ToolCallId = entry.ToolCallId,
                        VersionGroupId = versionGroupId,
                        VersionIndex = versionIndex,
                        TotalVersions = totalVersions,
                    };
                    toolChatMessage.ToolCalls.Add(
                        new ToolCallEntry { ToolCallId = entry.ToolCallId, ToolName = entry.ToolName });
                    messages.Add(toolChatMessage);
                    toolChatMessages.Add(toolChatMessage);
                }

                var toolCallsJson = JsonSerializer.Serialize(
                    sortedCalls.Select(kv => new { kv.Value.ToolCallId, kv.Value.ToolName, kv.Value.ArgumentsJson, kv.Value.ResultJson }));

                uiReply.DbId = await _messageRepo.InsertAsync(new Message
                {
                    SessionId = sessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoning,
                    ToolCallsJson = toolCallsJson,
                    IsActive = uiReply.IsActive,
                    VersionGroupId = versionGroupId,
                    VersionIndex = versionIndex,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                });

                foreach (var toolChatMessage in toolChatMessages)
                {
                    toolChatMessage.DbId = await _messageRepo.InsertAsync(new Message
                    {
                        SessionId = sessionId,
                        TurnId = turnId,
                        Role = "tool",
                        Content = toolChatMessage.Content,
                        ToolCallId = toolChatMessage.ToolCallId,
                        ContentType = "tool_result",
                        IsActive = toolChatMessage.IsActive,
                        VersionGroupId = versionGroupId,
                        VersionIndex = versionIndex,
                        CreatedAtUtc = DateTime.UtcNow,
                        LogicalDate = logicalDate,
                    });
                }

                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 回复失败");
            runtime.Status = LocalizationService.Instance.Format("Common_Status_Error", ex.Message);
            if (activeReply is not null)
            {
                activeReply.IsStreaming = false;
                await PersistNoticeAsync(sessionId, turnId, logicalDate, activeReply,
                    LocalizationService.Instance.Format("Common_SendFailed", ex.Message), versionGroupId, versionIndex);
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

        if (cancelled)
        {
            runtime.Status = LocalizationService.Instance["Common_Status_ReadyCanceled"];
            var cancelText = LocalizationService.Instance["Common_Canceled"];

            // 取消时如果当前回复已经是本轮最后一条 assistant，直接把提示写在它上面；
            // 如果它后面还有工具消息（取消发生在工具调用阶段），则在其后追加一条提示，
            // 保证“最终回复”始终位于本轮末尾，复制/重写按钮不会消失。
            var lastAssistant = messages.LastOrDefault(m => m.IsActive && m.Role == "assistant");
            if (activeReply is { DbId: 0 } && ReferenceEquals(lastAssistant, activeReply))
            {
                activeReply.IsStreaming = false;
                await PersistNoticeAsync(sessionId, turnId, logicalDate, activeReply,
                    cancelText, versionGroupId, versionIndex);
            }
            else
            {
                var notice = new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    IsStreaming = false,
                    VersionGroupId = versionGroupId,
                    VersionIndex = versionIndex,
                    TotalVersions = totalVersions,
                };
                messages.Add(notice);
                await PersistNoticeAsync(sessionId, turnId, logicalDate, notice,
                    cancelText, versionGroupId, versionIndex);
            }
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
                VersionIndex = lastAssistantMsg.VersionIndex,
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
        MarkFinalReplies(messages);
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
        ChatMessage msg, string text, string? versionGroupId, int versionIndex)
    {
        msg.ErrorMessage = text;
        msg.VersionGroupId = versionGroupId;
        msg.VersionIndex = versionIndex;

        var id = await _messageRepo.InsertAsync(new Message
        {
            SessionId = sessionId,
            TurnId = turnId,
            Role = "assistant",
            Content = string.Empty,
            ErrorMessage = text,
            ContentType = "error",
            IsActive = msg.IsActive,
            VersionGroupId = versionGroupId,
            VersionIndex = versionIndex,
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
            TurnId = m.TurnId,
            ToolCallId = m.ToolCallId,
            ErrorMessage = m.ErrorMessage,
            IsActive = m.IsActive,
            VersionGroupId = m.VersionGroupId,
            VersionIndex = m.VersionIndex,
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

        // 每轮用户消息之后，最后一个激活 assistant 就是本轮最终回复。
        // 不能简单用“下一条激活消息是否为 user”判断：取消/出错时工具消息可能排在这条
        // 回复之后（同一版本的 tool 消息），那样会把最终回复误判为非最终，导致操作按钮消失。
        // 也不要求必须有正文/错误：取消可能发生在首个 token 之前，此时仍是可重写的最终回复。
        ChatMessage? candidate = null;
        foreach (var message in messages)
        {
            if (message.Role == "user")
            {
                if (candidate is not null)
                    candidate.IsFinalReply = true;
                candidate = null;
                continue;
            }

            if (!message.IsActive || message.Role != "assistant") continue;
            candidate = message;
        }

        if (candidate is not null)
            candidate.IsFinalReply = true;
    }

    /// <summary>
    /// 只有“最后一条激活的最终 AI 回复”允许重写，避免历史消息上出现无效按钮。
    /// 最终回复之后可能还有同一版本的 tool 消息，因此不能直接取最后一条激活消息。
    /// </summary>
    private static void UpdateRewriteEligibility(ObservableCollection<ChatMessage> messages)
    {
        var lastUserIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user")
            {
                lastUserIndex = i;
                break;
            }
        }

        ChatMessage? target = null;
        for (var i = messages.Count - 1; i > lastUserIndex; i--)
        {
            if (messages[i].IsActive && messages[i].IsFinalReply)
            {
                target = messages[i];
                break;
            }
        }

        foreach (var message in messages)
            message.CanRewrite = ReferenceEquals(message, target);
    }

    /// <summary>
    /// 按版本分组计算 V1/V2 序号与总数，保证重启后 UI 显示正确。
    /// 工具调用与工具结果和对应回复共享版本序号，因此切换版本时能整组恢复/隐藏。
    /// </summary>
    private static void AssignVersionMetadata(ObservableCollection<ChatMessage> messages)
    {
        var groups = messages
            .Where(m => !string.IsNullOrEmpty(m.VersionGroupId))
            .GroupBy(m => m.VersionGroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.ToList();

            // 旧数据没有持久化版本序号：每个版本只有一条 assistant 消息，按插入顺序补全。
            if (members.All(m => m.VersionIndex <= 0))
            {
                var ordered = members.OrderBy(m => m.DbId).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    ordered[i].VersionIndex = i + 1;
                    ordered[i].TotalVersions = ordered.Count;
                }
                continue;
            }

            // 组内可能混有旧数据（VersionIndex = 0）：追加到已有版本之后。
            var lastIndex = members.Where(m => m.VersionIndex > 0)
                .Select(m => m.VersionIndex)
                .DefaultIfEmpty(0).Max();
            foreach (var member in members.Where(m => m.VersionIndex <= 0).OrderBy(m => m.DbId))
                member.VersionIndex = ++lastIndex;

            // 归一化为连续的 1..N（避免历史数据里出现空洞）。
            var indexMap = members.Select(m => m.VersionIndex)
                .Distinct()
                .OrderBy(i => i)
                .Select((index, rank) => (index, Rank: rank + 1))
                .ToDictionary(x => x.index, x => x.Rank);

            var total = indexMap.Count;
            foreach (var member in members)
            {
                member.VersionIndex = indexMap[member.VersionIndex];
                member.TotalVersions = total;
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
            .Where(m => !string.IsNullOrEmpty(m.VersionGroupId))
            .GroupBy(m => m.VersionGroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            if (group.Any(m => m.IsActive)) continue;

            var targetIndex = group.Max(m => m.VersionIndex);
            foreach (var member in group)
            {
                member.IsActive = member.VersionIndex == targetIndex;
                if (member.DbId > 0)
                {
                    await _messageRepo.UpdateMessageVersionAsync(
                        member.DbId, member.IsActive, member.VersionGroupId, member.VersionIndex);
                }
            }
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
                _logger.LogWarning("工具 {Name} 执行失败", entry.ToolName);
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
    private async Task AuditAsync(string sessionId, string toolName,
        string permissionResult, string executionResult)
    {
        try
        {
            await _auditRepo.AddAuditLogAsync(sessionId, toolName,
                permissionResult, executionResult);
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
