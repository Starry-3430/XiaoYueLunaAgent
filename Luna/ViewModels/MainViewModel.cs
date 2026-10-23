using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Luna.Controls;
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;

namespace Luna.ViewModels;

/// <summary>
/// 胶囊窗口视图模型：输入文本、忙碌状态、待发送附件、消息集合绑定，发送/取消命令，附件导入/移除/清空命令。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SessionRepository _sessionRepo;
    private readonly ChatGenerationService _generation;
    private readonly FileStorageService _fileStorage;
    private readonly IDocumentConverterService _documentConverter;
    private readonly AttachmentRepository _attachmentRepo;
    private readonly ILogger<MainViewModel> _logger;
    private string? _currentSessionId;
    private ChatMessage? _editingUserMessage;
    private SessionRuntime? _runtime;
    private readonly ObservableCollection<ChatMessage> _emptyMessages = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = LocalizationService.Instance["Common_Status_Ready"];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingAttachments;

    private CancellationTokenSource? _attachmentCts;

    private ObservableCollection<ChatMessage> _messages = new();
    public ObservableCollection<ChatMessage> Messages
    {
        get => _messages;
        private set => SetProperty(ref _messages, value);
    }

    /// <summary>待发送附件列表（发送前暂存，发送成功后清空）。</summary>
    public ObservableCollection<Attachment> PendingAttachments { get; } = new();

    public MainViewModel(SessionRepository sessionRepo, ChatGenerationService generation,
        FileStorageService fileStorage, IDocumentConverterService documentConverter,
        AttachmentRepository attachmentRepo, ILogger<MainViewModel> logger)
    {
        _sessionRepo = sessionRepo;
        _generation = generation;
        _fileStorage = fileStorage;
        _documentConverter = documentConverter;
        _attachmentRepo = attachmentRepo;
        _logger = logger;
        _logger.LogInformation("MainViewModel 已创建");

        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture))
                RefreshStatusText();
        };
    }

    /// <summary>语言切换后刷新状态栏（仅在空闲时重置为“就绪”）。</summary>
    private void RefreshStatusText()
    {
        if (_runtime is { IsBusy: true }) return;
        var ready = LocalizationService.Instance["Common_Status_Ready"];
        if (_runtime is not null) _runtime.Status = ready;
        Status = ready;
    }

    /// <summary>当前会话运行期对象；界面状态全部来自它，生成在后台由服务推进。</summary>
    private SessionRuntime? Runtime
    {
        get => _runtime;
        set
        {
            if (ReferenceEquals(_runtime, value)) return;
            if (_runtime is not null) _runtime.PropertyChanged -= Runtime_PropertyChanged;
            _runtime = value;
            if (_runtime is not null) _runtime.PropertyChanged += Runtime_PropertyChanged;

            Messages = _runtime?.Messages ?? _emptyMessages;
            IsBusy = _runtime?.IsBusy ?? false;
            Status = _runtime?.Status ?? LocalizationService.Instance["Common_Status_Ready"];
        }
    }

    private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionRuntime.IsBusy):
                IsBusy = _runtime?.IsBusy ?? false;
                break;
            case nameof(SessionRuntime.Status):
                Status = _runtime?.Status ?? LocalizationService.Instance["Common_Status_Ready"];
                break;
        }
    }

    /// <summary>选择附件（多选），读取内容并估算 Token，加入待发送列表。</summary>
    [RelayCommand]
    private async Task AddAttachmentAsync()
    {
        var picked = AttachmentHelper.PickFiles();
        if (picked.Count == 0) return;
        await LoadAttachmentsAsync(picked);
    }

    /// <summary>接收拖拽进来的文件。</summary>
    public Task AddDroppedFilesAsync(IEnumerable<string> paths)
        => LoadAttachmentsAsync(AttachmentHelper.FromPaths(paths));

    /// <summary>尝试把剪贴板中的图片/文件加入待发送列表；返回是否已处理（是则调用方应阻止默认文本粘贴）。</summary>
    public bool TryPasteClipboardAttachments()
    {
        var attachments = AttachmentHelper.GetClipboardAttachments();
        if (attachments.Count == 0) return false;

        _ = LoadAttachmentsAsync(attachments);
        return true;
    }

    /// <summary>按数量/大小规则筛选后读取内容并加入待发送列表。</summary>
    private async Task LoadAttachmentsAsync(IReadOnlyList<Attachment> candidates)
    {
        if (candidates.Count == 0) return;

        var accepted = new List<Attachment>();
        var skipped = 0;
        foreach (var attachment in candidates)
        {
            if (PendingAttachments.Count + accepted.Count >= AttachmentHelper.MaxCount) { skipped++; continue; }
            if (attachment.FileSize > AttachmentHelper.MaxBytes) { skipped++; continue; }
            accepted.Add(attachment);
        }

        if (accepted.Count == 0)
        {
            Status = LocalizationService.Instance.Format("Common_NoAttachments", AttachmentHelper.MaxCount, AttachmentHelper.FormatSize(AttachmentHelper.MaxBytes));
            return;
        }

        _attachmentCts?.Cancel();
        _attachmentCts?.Dispose();
        var cts = new CancellationTokenSource();
        _attachmentCts = cts;

        IsLoadingAttachments = true;
        Status = LocalizationService.Instance["Common_ReadingAttachments"];
        try
        {
            foreach (var attachment in accepted)
            {
                if (cts.IsCancellationRequested) break;
                await LoadAttachmentContentAsync(attachment, cts.Token);
                if (cts.IsCancellationRequested) break;
                PendingAttachments.Add(attachment);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("附件读取已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取附件失败");
        }
        finally
        {
            if (ReferenceEquals(_attachmentCts, cts))
            {
                IsLoadingAttachments = false;
                Status = skipped > 0
                    ? LocalizationService.Instance.Format("Common_SkippedAttachments", skipped, AttachmentHelper.MaxCount, AttachmentHelper.FormatSize(AttachmentHelper.MaxBytes))
                    : LocalizationService.Instance["Common_Status_Ready"];
            }
        }
    }

    /// <summary>取消正在进行的附件转换。</summary>
    [RelayCommand]
    private void CancelAttachmentLoad() => _attachmentCts?.Cancel();

    /// <summary>转换附件内容并估算 Token。图片不做转换，交由 AI 的识图能力直接识别。</summary>
    private async Task LoadAttachmentContentAsync(Attachment attachment, CancellationToken ct)
    {
        if (AttachmentHelper.IsImage(attachment))
        {
            attachment.ConvertedMarkdown = null;
            attachment.TokenEstimate = 0;
            return;
        }

        var sourcePath = attachment.StoredPath;
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return;

        attachment.ConvertedMarkdown = await _documentConverter.ConvertToMarkdownAsync(sourcePath, ct);
        attachment.TokenEstimate = AttachmentContentProcessor.EstimateTokens(attachment.ConvertedMarkdown);
    }

    /// <summary>从待发送列表移除附件。</summary>
    [RelayCommand]
    private void RemoveAttachment(Attachment? attachment)
    {
        if (attachment is not null) PendingAttachments.Remove(attachment);
    }

    /// <summary>清空待发送附件。</summary>
    [RelayCommand]
    private void ClearAttachments() => PendingAttachments.Clear();

    private static Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    /// <summary>保存文件到内容寻址存储；内容与 Token 已在导入时准备好。</summary>
    private async Task<List<Attachment>> PrepareAttachmentsAsync()
    {
        var prepared = new List<Attachment>();
        foreach (var attachment in PendingAttachments.ToList())
        {
            var sourcePath = attachment.StoredPath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;

            try
            {
                var stored = await _fileStorage.StoreAsync(sourcePath);
                attachment.Sha256 = stored.Sha256;
                attachment.StoredPath = stored.StoredPath;
                attachment.FileSize = stored.FileSize;

                // 图片不转换，交由 AI 识图；其余文档按需转换 Markdown
                if (!AttachmentHelper.IsImage(attachment) && string.IsNullOrWhiteSpace(attachment.ConvertedMarkdown))
                    attachment.ConvertedMarkdown = await _documentConverter.ConvertToMarkdownAsync(stored.StoredPath);
                if (!AttachmentHelper.IsImage(attachment) && attachment.TokenEstimate == 0)
                    attachment.TokenEstimate = AttachmentContentProcessor.EstimateTokens(attachment.ConvertedMarkdown);

                prepared.Add(attachment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "准备附件失败：{File}", attachment.FileName);
            }
        }
        return prepared;
    }

    /// <summary>超过 Token 预算时询问用户，按选择决定是否智能截断。</summary>
    private static void ApplyTokenBudget(IReadOnlyList<Attachment> prepared)
    {
        var overLimit = prepared
            .Where(a => a.TokenEstimate > AttachmentContentProcessor.DefaultMaxTokens)
            .ToList();
        if (overLimit.Count == 0) return;

        var mode = AttachmentTruncationDialog.Show(ActiveWindow);
        if (mode != AttachmentImportMode.SmartTruncate) return;

        foreach (var attachment in overLimit)
            attachment.InjectedMarkdown = AttachmentContentProcessor.Truncate(attachment.ConvertedMarkdown);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var hasAttachments = PendingAttachments.Count > 0;
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(InputText) && !hasAttachments) return;

        // 记录原始输入：发送失败（并发冲突、附件/数据库异常等）时原样还给用户。
        var originalInput = InputText;
        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            var prepared = hasAttachments ? await PrepareAttachmentsAsync() : new List<Attachment>();
            if (string.IsNullOrWhiteSpace(userText) && prepared.Count > 0)
                userText = LocalizationService.Instance["Common_ReadAttachmentsPrompt"];
            ApplyTokenBudget(prepared);

            var editing = _editingUserMessage;
            if (editing is not null && !CanEditUserMessage(editing))
            {
                CancelEditing();
                editing = null;
            }

            if (editing is not null)
            {
                var editSessionId = _currentSessionId!;
                Runtime = await _generation.GetOrLoadAsync(editSessionId);
                await _generation.EditUserMessageAsync(editSessionId, editing, userText, prepared);
                CancelEditing();
                PendingAttachments.Clear();
                return;
            }

            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;
            }

            var sessionId = _currentSessionId;
            Runtime = await _generation.GetOrLoadAsync(sessionId);

            // 生成在服务中后台执行：即使窗口隐藏/切换，输出也会继续并写库。
            await _generation.SendAsync(sessionId, userText, prepared);

            PendingAttachments.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送失败");
            Status = LocalizationService.Instance.Format("Common_SendFailed", ex.Message);

            // 失败时若用户还没有输入新内容，就恢复刚才的草稿，避免心血蒸发。
            if (string.IsNullOrEmpty(InputText))
                InputText = originalInput;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_currentSessionId is not null)
            _generation.Cancel(_currentSessionId);
    }

    public void NewChat()
    {
        CancelEditing();
        _currentSessionId = null;
        _attachmentCts?.Cancel();
        Runtime = null;
        InputText = string.Empty;
        PendingAttachments.Clear();
        Status = LocalizationService.Instance["Common_Status_Ready"];
    }

    /// <summary>是否允许编辑：只允许当前会话最后一条激活的用户消息。</summary>
    public bool CanEditUserMessage(ChatMessage? msg)
    {
        if (msg is null || msg.Role != "user" || IsBusy || _currentSessionId is null)
            return false;

        var runtimeMessages = Runtime?.Messages;
        return runtimeMessages is not null &&
               ReferenceEquals(runtimeMessages.LastOrDefault(m => m.IsActive && m.Role == "user"), msg);
    }

    /// <summary>进入编辑模式：恢复用户消息内容与同轮附件到输入区。</summary>
    public async Task BeginEditUserMessageAsync(ChatMessage msg)
    {
        if (!CanEditUserMessage(msg)) return;

        _editingUserMessage = msg;
        InputText = msg.Content;
        PendingAttachments.Clear();

        try
        {
            if (!string.IsNullOrEmpty(msg.TurnId))
            {
                var attachments = await _attachmentRepo.GetByTurnAsync(msg.TurnId);
                foreach (var attachment in attachments)
                    PendingAttachments.Add(CloneAttachment(attachment));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复编辑消息的附件失败：{TurnId}", msg.TurnId);
        }

        Status = LocalizationService.Instance["Common_Status_Editing"];
    }

    private void CancelEditing() => _editingUserMessage = null;

    /// <summary>按 Esc 取消编辑：清空恢复的内容与附件，回到就绪状态。</summary>
    public bool TryCancelEditing()
    {
        if (_editingUserMessage is null) return false;

        CancelEditing();
        InputText = string.Empty;
        PendingAttachments.Clear();
        Status = Runtime?.Status ?? LocalizationService.Instance["Common_Status_Ready"];
        return true;
    }

    private static Attachment CloneAttachment(Attachment source) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        FileName = source.FileName,
        FileExtension = source.FileExtension,
        FileSize = source.FileSize,
        Sha256 = source.Sha256,
        StoredPath = source.StoredPath,
        ConvertedMarkdown = source.ConvertedMarkdown,
        SourcePath = source.SourcePath,
        CreatedAtUtc = DateTime.UtcNow,
    };

    [RelayCommand]
    public async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (_currentSessionId is null || IsBusy) return;
        await _generation.RewriteAsync(_currentSessionId, msg);
    }

    [RelayCommand]
    private Task SwitchToPrevVersionAsync(ChatMessage msg)
        => SwitchVersionAsync(msg, -1);

    [RelayCommand]
    private Task SwitchToNextVersionAsync(ChatMessage msg)
        => SwitchVersionAsync(msg, 1);

    private async Task SwitchVersionAsync(ChatMessage msg, int direction)
    {
        if (_currentSessionId is null || IsBusy) return;
        CancelEditing();
        await _generation.SwitchVersionAsync(_currentSessionId, msg, direction);
        Status = Runtime?.Status ?? LocalizationService.Instance["Common_Status_Ready"];
    }
}
