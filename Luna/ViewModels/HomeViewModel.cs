using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Luna.Controls;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;
using Luna.Services.Tools;

namespace Luna.ViewModels;

public partial class HomeViewModel : ObservableObject, IRecipient<SessionUpdateMessage>
{
    private readonly SessionRepository _sessionRepo;
    private readonly AiSettings _aiSettings;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolSettingsService _toolSettings;
    private readonly ToolPermissionService _toolPermission;
    private readonly SettingsService _settingsService;
    private readonly ChatGenerationService _generation;
    private readonly FileStorageService _fileStorage;
    private readonly IDocumentConverterService _documentConverter;
    private readonly ILogger<HomeViewModel> _logger;
    private string? _currentSessionId;
    private readonly Dictionary<string, string> _sessionDrafts = new();
    private readonly Dictionary<string, List<Attachment>> _sessionAttachments = new();
    private SessionRuntime? _runtime;
    private readonly ObservableCollection<ChatMessage> _emptyMessages = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingAttachments;

    private CancellationTokenSource? _attachmentCts;

    [ObservableProperty]
    private ChatSessionItem? _selectedSession;

    /// <summary>Tavily 搜索 API Key（明文，编辑后立即加密持久化）。</summary>
    [ObservableProperty]
    private string _tavilyApiKey = string.Empty;

    private ObservableCollection<ChatMessage> _messages = new();
    public ObservableCollection<ChatMessage> Messages
    {
        get => _messages;
        private set => SetProperty(ref _messages, value);
    }

    public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();
    public ObservableCollection<ToolCategory> ToolCategories { get; } = new();

    /// <summary>待发送附件列表（发送前暂存，发送成功后清空）。</summary>
    public ObservableCollection<Attachment> PendingAttachments { get; } = new();

    public HomeViewModel(SessionRepository sessionRepo, AiSettings aiSettings,
        ToolRegistry toolRegistry, ToolSettingsService toolSettings,
        ToolPermissionService toolPermission, SettingsService settingsService,
        ChatGenerationService generation, FileStorageService fileStorage,
        IDocumentConverterService documentConverter, ILogger<HomeViewModel> logger)
    {
        _sessionRepo = sessionRepo;
        _aiSettings = aiSettings;
        _toolRegistry = toolRegistry;
        _toolSettings = toolSettings;
        _toolPermission = toolPermission;
        _settingsService = settingsService;
        _generation = generation;
        _fileStorage = fileStorage;
        _documentConverter = documentConverter;
        _logger = logger;

        _tavilyApiKey = _aiSettings.TavilyApiKey;

        WeakReferenceMessenger.Default.Register<SessionUpdateMessage>(this);
        _ = LoadSessionsAsync();

        PopulateTools();
    }

    /// <summary>当前显示/操作的会话运行期对象。生成的界面状态全部来自它，因此切换会话不会中断后台输出。</summary>
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
            Status = _runtime?.Status ?? "就绪";
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
                Status = _runtime?.Status ?? "就绪";
                break;
        }
    }

    private void PopulateTools()
    {
        ToolCategories.Add(new ToolCategory
        {
            Name = "信息",
            Tools =
            {
                new ToolDefinition { Id = "web_search", Category = "信息", Name = "网页搜索", Description = "通过 Tavily 在互联网上搜索信息，返回标题、URL 与摘要", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "fetch_url", Category = "信息", Name = "网页阅读", Description = "抓取指定 URL 的网页正文，配合搜索使用", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "get_current_time", Category = "信息", Name = "当前时间", Description = "获取当前日期与时间", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "read_clipboard", Category = "信息", Name = "剪贴板读取", Description = "读取系统剪贴板中的文本内容", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "news_calendar", Category = "信息", Name = "新闻/日历", Description = "获取新闻摘要或日历信息", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "系统",
            Tools =
            {
                new ToolDefinition { Id = "powershell", Category = "系统", Name = "PowerShell", Description = "执行 PowerShell 命令", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.High },
                new ToolDefinition { Id = "search_files", Category = "系统", Name = "文件搜索", Description = "按文件名搜索文件，优先查 Windows 搜索索引，未命中时回退全盘扫描", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "read_file", Category = "系统", Name = "读取文件", Description = "读取指定文件内容（限定目录、扩展名与大小）", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "write_file", Category = "系统", Name = "写入文件", Description = "写入文件内容（需沙箱目录与二次确认）", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "file_open", Category = "系统", Name = "打开文件/应用", Description = "打开指定文件或启动应用程序", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "screenshot", Category = "系统", Name = "截图", Description = "截取屏幕截图", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "window_manager", Category = "系统", Name = "窗口管理", Description = "管理窗口位置、大小和状态", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "volume_brightness", Category = "系统", Name = "音量/亮度", Description = "调节系统音量和屏幕亮度", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "记忆",
            Tools =
            {
                new ToolDefinition { Id = "diary_search", Category = "记忆", Name = "检索日记", Description = "在日记中搜索相关内容", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "turn_search", Category = "记忆", Name = "轮次检索", Description = "搜索历史对话轮次", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "long_term_facts", Category = "记忆", Name = "长期事实", Description = "检索和存储长期事实记忆", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "组织",
            Tools =
            {
                new ToolDefinition { Id = "todo", Category = "组织", Name = "待办事项", Description = "创建/管理待办事项，可为待办设置截止时间与提醒时间", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "clipboard_history", Category = "组织", Name = "剪贴板历史", Description = "查看和管理剪贴板历史记录", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "输入",
            Tools =
            {
                new ToolDefinition { Id = "vision", Category = "输入", Name = "视觉", Description = "分析图片内容", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "ocr", Category = "输入", Name = "OCR", Description = "识别图片中的文字", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "speech", Category = "输入", Name = "语音", Description = "语音识别与合成", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "开发",
            Tools =
            {
                new ToolDefinition { Id = "code_execute", Category = "开发", Name = "代码执行（沙箱）", Description = "在沙箱环境中执行代码", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "git", Category = "开发", Name = "Git", Description = "执行 Git 操作", IsEnabled = true, IsAvailable = false, RequiresConfig = true },
                new ToolDefinition { Id = "file_tree", Category = "开发", Name = "文件树结构", Description = "获取目录的文件树结构", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            Name = "通信",
            Tools =
            {
                new ToolDefinition { Id = "send_notification", Category = "通信", Name = "系统通知", Description = "发送系统通知，为“定时提醒”铺路", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Low },
                new ToolDefinition { Id = "email", Category = "通信", Name = "邮件", Description = "发送邮件", IsEnabled = true, IsAvailable = false, RequiresConfig = true },
            }
        });

        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            if (_toolRegistry.IsRegistered(tool.Id))
            {
                tool.IsAvailable = true;
                tool.Risk = _toolRegistry.GetTool(tool.Id)?.Risk ?? ToolRiskLevel.None;
            }
            else if (_toolRegistry.IsGroupRegistered(tool.Id))
            {
                // 同组工具（如 todo → add_task/list_tasks/...）共用一个开关
                tool.IsAvailable = true;
                tool.Risk = _toolRegistry.GetGroupRisk(tool.Id);
            }
        }

        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            if (!tool.IsAvailable)
                tool.StatusText = "未实现";
        }

        // 载入用户保存的启用状态
        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            tool.IsEnabled = _toolSettings.IsEnabled(tool.Id);
        }

        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            if (tool.Id == "web_search")
            {
                var hasKey = !string.IsNullOrEmpty(_aiSettings.TavilyApiKey);
                tool.IsEnabled = hasKey && tool.IsEnabled;
                tool.StatusText = hasKey ? "" : "未配置 API Key";
            }
            if (tool.Id == "read_clipboard")
                tool.StatusText = "";
        }

        // 用户切换开关时持久化，并即时影响暴露给 AI 的工具集合
        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            tool.PropertyChanged += Tool_PropertyChanged;
        }
    }

    private void Tool_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolDefinition.IsEnabled)) return;
        if (sender is ToolDefinition tool)
            _toolSettings.SetEnabled(tool.Id, tool.IsEnabled);
    }

    /// <summary>用户编辑 Tavily Key 后立即加密保存，并同步“网页搜索”的可用状态。</summary>
    partial void OnTavilyApiKeyChanged(string value)
    {
        _aiSettings.TavilyApiKey = value;
        _settingsService.Save(_aiSettings);

        var tool = ToolCategories.SelectMany(c => c.Tools).FirstOrDefault(t => t.Id == "web_search");
        if (tool is null) return;

        var hasKey = !string.IsNullOrEmpty(value);
        tool.StatusText = hasKey ? "" : "未配置 API Key";
        tool.IsEnabled = hasKey;
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
        _toolPermission.ClearConversation(_currentSessionId);
        _attachmentCts?.Cancel();
        Runtime = null;
        InputText = string.Empty;
        PendingAttachments.Clear();
        Status = "就绪";
        _currentSessionId = null;
        SelectedSession = null;
    }

    [RelayCommand]
    private async Task SelectSession(ChatSessionItem? session)
    {
        if (session is null) return;

        // 会话列表刷新（如生成结束）会重新选中同一项，此时不要重置输入框或重新加载
        if (session.Id == _currentSessionId)
        {
            SelectedSession = session;
            return;
        }

        SaveCurrentDraft();
        PendingAttachments.Clear();

        _currentSessionId = session.Id;
        SelectedSession = session;

        if (_sessionDrafts.TryGetValue(session.Id, out var draft))
            InputText = draft;
        else
            InputText = string.Empty;

        // 恢复该会话尚未发送的附件
        if (_sessionAttachments.TryGetValue(session.Id, out var attachments))
        {
            foreach (var attachment in attachments)
                PendingAttachments.Add(attachment);
        }

        try
        {
            // 复用会话运行期对象：正在后台生成的内容会原样显示，不会因切换而丢失。
            Runtime = await _generation.GetOrLoadAsync(session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话消息失败");
            Status = "加载失败";
        }
    }

    private void SaveCurrentDraft()
    {
        if (_currentSessionId is null) return;
        _sessionDrafts[_currentSessionId] = InputText;
        _sessionAttachments[_currentSessionId] = PendingAttachments.ToList();
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

    /// <summary>按数量/大小规则筛选后读取内容并加入待发送列表。</summary>
    private async Task LoadAttachmentsAsync(IReadOnlyList<Attachment> candidates)
    {
        if (candidates.Count == 0) return;

        // 规则：最多 10 个附件，每个不超过 100 MB
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
            Status = $"没有可添加的附件（最多 {AttachmentHelper.MaxCount} 个，每个 ≤ {AttachmentHelper.FormatSize(AttachmentHelper.MaxBytes)}）";
            return;
        }

        _attachmentCts?.Cancel();
        _attachmentCts?.Dispose();
        var cts = new CancellationTokenSource();
        _attachmentCts = cts;

        IsLoadingAttachments = true;
        Status = "正在读取附件…";
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
                    ? $"已忽略 {skipped} 个附件（最多 {AttachmentHelper.MaxCount} 个，每个 ≤ {AttachmentHelper.FormatSize(AttachmentHelper.MaxBytes)}）"
                    : "就绪";
            }
        }
    }

    /// <summary>取消正在进行的附件转换。</summary>
    [RelayCommand]
    private void CancelAttachmentLoad() => _attachmentCts?.Cancel();

    /// <summary>转换附件内容并估算 Token。</summary>
    private async Task LoadAttachmentContentAsync(Attachment attachment, CancellationToken ct)
    {
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

                if (string.IsNullOrWhiteSpace(attachment.ConvertedMarkdown))
                    attachment.ConvertedMarkdown = await _documentConverter.ConvertToMarkdownAsync(stored.StoredPath);
                if (attachment.TokenEstimate == 0)
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

        var userText = InputText.Trim();
        InputText = string.Empty;

        try
        {
            // 先保存文件并转换 Markdown（可能耗时），再启动 AI 循环
            var prepared = hasAttachments ? await PrepareAttachmentsAsync() : new List<Attachment>();
            if (string.IsNullOrWhiteSpace(userText) && prepared.Count > 0)
                userText = "请阅读我发送的附件内容。";
            ApplyTokenBudget(prepared);

            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;

                var item = new ChatSessionItem
                {
                    Id = session.Id,
                    Title = "新对话",
                    Preview = userText,
                };
                ChatSessions.Insert(0, item);

                // 立即选中新会话，让界面跳转到它的历史记录，而不是停留在“新对话”状态
                SelectedSession = item;
            }

            var sessionId = _currentSessionId;
            Runtime = await _generation.GetOrLoadAsync(sessionId);

            // 生成在服务中后台执行：切换界面/会话不会中断，用户消息与 AI 输出由服务写库并推进。
            await _generation.SendAsync(sessionId, userText, prepared);

            // 发送成功后清空待发送附件（并清掉该会话的暂存）
            PendingAttachments.Clear();
            _sessionAttachments[sessionId] = new List<Attachment>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送消息失败");
            Status = "发送失败：" + ex.Message;
        }
    }


    [RelayCommand]
    private void Cancel()
    {
        if (_currentSessionId is not null)
            _generation.Cancel(_currentSessionId);
    }

    [RelayCommand]
    private async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (_currentSessionId is null || IsBusy) return;
        await _generation.RewriteAsync(_currentSessionId, msg);
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
