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
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.Services.Data;
using Luna.Services.Tools;

namespace Luna.ViewModels;

/// <summary>
/// 主界面视图模型：会话列表、当前会话、输入与发送、附件、导航状态
/// </summary>
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
    private readonly SearchRepository _searchRepo;
    private readonly ILogger<HomeViewModel> _logger;
    private string? _currentSessionId;
    private readonly Dictionary<string, string> _sessionDrafts = new();
    private readonly Dictionary<string, List<Attachment>> _sessionAttachments = new();
    private SessionRuntime? _runtime;
    private readonly ObservableCollection<ChatMessage> _emptyMessages = new();
    private CancellationTokenSource? _searchCts;
    private const int SearchResultLimit = 50;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = LocalizationService.Instance["Common_Status_Ready"];

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

    /// <summary>历史会话搜索关键词。</summary>
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchLoading;

    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool ShowSearchEmpty => IsSearchActive && !IsSearchLoading && SearchResults.Count == 0;

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
        IDocumentConverterService documentConverter, SearchRepository searchRepo,
        ILogger<HomeViewModel> logger)
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
        _searchRepo = searchRepo;
        _logger = logger;

        _tavilyApiKey = _aiSettings.TavilyApiKey;

        WeakReferenceMessenger.Default.Register<SessionUpdateMessage>(this);
        _ = LoadSessionsAsync();

        PopulateTools();

        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture))
            {
                ApplyToolLocalization();
                RefreshStatusText();
            }
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

    private void PopulateTools()
    {
        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Info",
            Tools =
            {
                new ToolDefinition { Id = "web_search", NameKey = "Tool_WebSearch_DisplayName", DescriptionKey = "Tool_WebSearch_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "fetch_url", NameKey = "Tool_FetchUrl_DisplayName", DescriptionKey = "Tool_FetchUrl_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "read_clipboard", NameKey = "Tool_ReadClipboard_DisplayName", DescriptionKey = "Tool_ReadClipboard_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "news_calendar", NameKey = "Tool_NewsCalendar_DisplayName", DescriptionKey = "Tool_NewsCalendar_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_System",
            Tools =
            {
                new ToolDefinition { Id = "powershell", NameKey = "Tool_Powershell_DisplayName", DescriptionKey = "Tool_Powershell_Description", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.High },
                new ToolDefinition { Id = "search_files", NameKey = "Tool_SearchFiles_DisplayName", DescriptionKey = "Tool_SearchFiles_Description", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "read_file", NameKey = "Tool_ReadFile_DisplayName", DescriptionKey = "Tool_ReadFile_Description", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "write_file", NameKey = "Tool_WriteFile_DisplayName", DescriptionKey = "Tool_WriteFile_Description", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "file_open", NameKey = "Tool_FileOpen_DisplayName", DescriptionKey = "Tool_FileOpen_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "screenshot", NameKey = "Tool_Screenshot_DisplayName", DescriptionKey = "Tool_Screenshot_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "window_manager", NameKey = "Tool_WindowManager_DisplayName", DescriptionKey = "Tool_WindowManager_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "volume_brightness", NameKey = "Tool_VolumeBrightness_DisplayName", DescriptionKey = "Tool_VolumeBrightness_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Memory",
            Tools =
            {
                new ToolDefinition { Id = "diary_search", NameKey = "Tool_DiarySearch_DisplayName", DescriptionKey = "Tool_DiarySearch_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "turn_search", NameKey = "Tool_TurnSearch_DisplayName", DescriptionKey = "Tool_TurnSearch_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "long_term_facts", NameKey = "Tool_LongTermFacts_DisplayName", DescriptionKey = "Tool_LongTermFacts_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Organize",
            Tools =
            {
                new ToolDefinition { Id = "todo", NameKey = "Tool_Todo_DisplayName", DescriptionKey = "Tool_Todo_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "clipboard_history", NameKey = "Tool_ClipboardHistory_DisplayName", DescriptionKey = "Tool_ClipboardHistory_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Input",
            Tools =
            {
                new ToolDefinition { Id = "vision", NameKey = "Tool_Vision_DisplayName", DescriptionKey = "Tool_Vision_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "ocr", NameKey = "Tool_Ocr_DisplayName", DescriptionKey = "Tool_Ocr_Description", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "speech", NameKey = "Tool_Speech_DisplayName", DescriptionKey = "Tool_Speech_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Dev",
            Tools =
            {
                new ToolDefinition { Id = "code_execute", NameKey = "Tool_CodeExecute_DisplayName", DescriptionKey = "Tool_CodeExecute_Description", IsEnabled = true, IsAvailable = false, RequiresConfig = true, Risk = ToolRiskLevel.Medium },
                new ToolDefinition { Id = "git", NameKey = "Tool_Git_DisplayName", DescriptionKey = "Tool_Git_Description", IsEnabled = true, IsAvailable = false, RequiresConfig = true },
                new ToolDefinition { Id = "file_tree", NameKey = "Tool_FileTree_DisplayName", DescriptionKey = "Tool_FileTree_Description", IsEnabled = true, IsAvailable = false },
            }
        });

        ToolCategories.Add(new ToolCategory
        {
            NameKey = "Tool_Category_Communication",
            Tools =
            {
                new ToolDefinition { Id = "send_notification", NameKey = "Tool_SendNotification_DisplayName", DescriptionKey = "Tool_SendNotification_Description", IsEnabled = true, IsAvailable = false, Risk = ToolRiskLevel.Low },
                new ToolDefinition { Id = "email", NameKey = "Tool_Email_DisplayName", DescriptionKey = "Tool_Email_Description", IsEnabled = true, IsAvailable = false, RequiresConfig = true },
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

        // 载入用户保存的启用状态
        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            tool.IsEnabled = _toolSettings.IsEnabled(tool.Id);
        }

        var webSearch = ToolCategories.SelectMany(c => c.Tools).FirstOrDefault(t => t.Id == "web_search");
        if (webSearch is not null)
            webSearch.IsEnabled = !string.IsNullOrEmpty(_aiSettings.TavilyApiKey) && webSearch.IsEnabled;

        ApplyToolLocalization();

        // 用户切换开关时持久化，并即时影响暴露给 AI 的工具集合
        foreach (var cat in ToolCategories)
        foreach (var tool in cat.Tools)
        {
            tool.PropertyChanged += Tool_PropertyChanged;
        }
    }

    /// <summary>按当前语言刷新工具页的分组名、工具名/说明、风险标签与状态文本。</summary>
    private void ApplyToolLocalization()
    {
        var loc = LocalizationService.Instance;
        foreach (var cat in ToolCategories)
        {
            if (!string.IsNullOrEmpty(cat.NameKey))
                cat.Name = loc[cat.NameKey];

            foreach (var tool in cat.Tools)
            {
                tool.RefreshLocalizedText();
                tool.StatusText = !tool.IsAvailable
                    ? loc["Tool_Status_NotImplemented"]
                    : tool.Id == "web_search" && string.IsNullOrEmpty(_aiSettings.TavilyApiKey)
                        ? loc["Tool_Status_NoApiKey"]
                        : string.Empty;
            }
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
        tool.StatusText = hasKey ? "" : LocalizationService.Instance["Tool_Status_NoApiKey"];
        tool.IsEnabled = hasKey;
    }

    // ===== 历史会话搜索 =====

    /// <summary>搜索关键词变化：防抖后异步检索，并刷新空态/激活态。</summary>
    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(ShowSearchEmpty));

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearchLoading = false;
            SearchResults.Clear();
            OnPropertyChanged(nameof(ShowSearchEmpty));
            return;
        }

        IsSearchLoading = true;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = SearchAsync(value.Trim(), cts.Token);
    }

    private async Task SearchAsync(string keyword, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            var hits = await _searchRepo.SearchAsync(keyword, SearchResultLimit);
            ct.ThrowIfCancellationRequested();

            SearchResults.Clear();
            foreach (var hit in hits)
                SearchResults.Add(ToSearchResult(hit, keyword));
            IsSearchLoading = false;
        }
        catch (OperationCanceledException)
        {
            // 已由更新的关键词取代，加载态交给新的查询控制，忽略。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索历史会话失败：{Keyword}", keyword);
            IsSearchLoading = false;
        }
    }

    partial void OnIsSearchLoadingChanged(bool value)
        => OnPropertyChanged(nameof(ShowSearchEmpty));

    private static SearchResult ToSearchResult(SearchRepository.SearchHit hit, string keyword) => new()
    {
        SessionId = hit.SessionId,
        SessionTitle = string.IsNullOrWhiteSpace(hit.SessionTitle)
            ? LocalizationService.Instance["Home_NewChat"]
            : hit.SessionTitle,
        MessageId = hit.MessageId,
        Role = hit.Role,
        Snippet = BuildSnippet(hit.Content, keyword),
        Keyword = keyword,
        TimeText = FormatSearchTime(hit.CreatedAtUtc)
    };

    /// <summary>截取关键词周围的上下文片段，折叠换行并加省略号。</summary>
    private static string BuildSnippet(string content, string keyword, int radius = 40)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var index = content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return Collapse(content.Length > radius * 2 ? content[..(radius * 2)] + "…" : content);

        var start = Math.Max(0, index - radius);
        var end = Math.Min(content.Length, index + keyword.Length + radius);
        var text = Collapse(content[start..end]);
        if (start > 0) text = "…" + text;
        if (end < content.Length) text += "…";
        return text;
    }

    private static string Collapse(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string FormatSearchTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Year == today.Year) return local.ToString("MM-dd HH:mm");
        return local.ToString("yyyy-MM-dd");
    }

    /// <summary>清空搜索关键词并退出搜索态。</summary>
    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    /// <summary>打开搜索结果所在会话（供界面随后滚动到对应消息）。</summary>
    public async Task OpenSessionAtMessageAsync(SearchResult result)
    {
        var session = ChatSessions.FirstOrDefault(s => s.Id == result.SessionId);
        if (session is null)
        {
            await LoadSessionsAsync();
            session = ChatSessions.FirstOrDefault(s => s.Id == result.SessionId);
        }

        session ??= new ChatSessionItem { Id = result.SessionId, Title = result.SessionTitle };
        await SelectSessionCommand.ExecuteAsync(session);
    }

    private async Task LoadSessionsAsync()
    {
        try
        {
            var sessions = await _sessionRepo.GetAllAsync();

            // 集合变更必须在 UI 线程执行（可能由后台线程的通知触发）。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                ApplySessions(sessions);
            else
                await dispatcher.InvokeAsync(() => ApplySessions(sessions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话列表失败");
        }
    }

    private void ApplySessions(List<Session> sessions)
    {
        var selectedId = SelectedSession?.Id;
        ChatSessions.Clear();
        foreach (var s in sessions)
        {
            var item = new ChatSessionItem
            {
                Id = s.Id,
                Title = s.Title ?? LocalizationService.Instance["Home_NewChat"],
                Preview = s.Preview ?? "",
            };
            ChatSessions.Add(item);
            if (item.Id == selectedId)
                SelectedSession = item;
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
        Status = LocalizationService.Instance["Common_Status_Ready"];
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

            // 打开会话时收起所有历史思考过程（正在流式输出的那条除外，保持实时展开）。
            foreach (var message in Runtime.Messages)
            {
                if (!message.IsStreaming)
                    message.IsReasoningExpanded = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话消息失败");
            Status = LocalizationService.Instance["Home_Status_LoadFailed"];
        }
    }

    /// <summary>重命名会话：写入数据库并就地刷新界面标题。</summary>
    public async Task RenameSessionAsync(ChatSessionItem session, string newTitle)
    {
        newTitle = newTitle.Trim();
        if (string.IsNullOrEmpty(newTitle) || newTitle == session.Title) return;

        try
        {
            await _sessionRepo.UpdateTitleAsync(session.Id, newTitle);
            session.Title = newTitle; // 可观察属性：左侧列表与顶部标题会立即刷新
            _logger.LogInformation("会话已重命名：{Id} -> {Title}", session.Id, newTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名会话失败：{Id}", session.Id);
            Status = LocalizationService.Instance.Format("Home_Status_RenameFailed", ex.Message);
        }
    }

    /// <summary>删除会话及其全部关联数据（消息 / 附件引用 / 轮次摘要），日记不受影响。</summary>
    public async Task DeleteSessionAsync(ChatSessionItem session)
    {
        try
        {
            _generation.Cancel(session.Id);
            await _sessionRepo.DeleteAsync(session.Id);
            _toolPermission.ClearConversation(session.Id);
            _generation.RemoveRuntime(session.Id);

            var wasSelected = ReferenceEquals(SelectedSession, session) || session.Id == _currentSessionId;
            ChatSessions.Remove(session);
            _sessionDrafts.Remove(session.Id);
            _sessionAttachments.Remove(session.Id);

            if (wasSelected)
            {
                Runtime = null;
                _currentSessionId = null;
                SelectedSession = null;
                InputText = string.Empty;
                PendingAttachments.Clear();
                Status = LocalizationService.Instance["Common_Status_Ready"];
            }

            _logger.LogInformation("会话已删除：{Id}", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除会话失败：{Id}", session.Id);
            Status = LocalizationService.Instance.Format("Home_Status_DeleteFailed", ex.Message);
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
            // 先保存文件并转换 Markdown（可能耗时），再启动 AI 循环
            var prepared = hasAttachments ? await PrepareAttachmentsAsync() : new List<Attachment>();
            if (string.IsNullOrWhiteSpace(userText) && prepared.Count > 0)
                userText = LocalizationService.Instance["Common_ReadAttachmentsPrompt"];
            ApplyTokenBudget(prepared);

            if (_currentSessionId is null)
            {
                var session = new Session();
                await _sessionRepo.InsertAsync(session);
                _currentSessionId = session.Id;

                var item = new ChatSessionItem
                {
                    Id = session.Id,
                    Title = LocalizationService.Instance["Home_NewChat"],
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

    [RelayCommand]
    private async Task RewriteMessageAsync(ChatMessage msg)
    {
        if (_currentSessionId is null || IsBusy) return;
        await _generation.RewriteAsync(_currentSessionId, msg);
    }

    public void Receive(SessionUpdateMessage message)
    {
        // 通知可能来自后台线程（如标题生成），集合变更必须在 UI 线程执行，否则会破坏绑定。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplySessionUpdate(message);
        else
            dispatcher.BeginInvoke(() => ApplySessionUpdate(message));
    }

    /// <summary>就地更新会话列表（不重建），保持后台缓存不丢失。</summary>
    private void ApplySessionUpdate(SessionUpdateMessage message)
    {
        if (!string.IsNullOrEmpty(message.SessionId))
        {
            var item = ChatSessions.FirstOrDefault(s => s.Id == message.SessionId);
            if (item is not null)
            {
                if (!string.IsNullOrWhiteSpace(message.Title))
                    item.Title = message.Title!;

                // 该会话刚刚有活动，置顶（与按 UpdatedAtUtc 倒序一致）
                var index = ChatSessions.IndexOf(item);
                if (index > 0) ChatSessions.Move(index, 0);
                return;
            }
        }

        // 找不到对应项（如新增会话）时才整体刷新
        _ = LoadSessionsAsync();
    }
}

public partial class ChatSessionItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    private string _preview = string.Empty;

    /// <summary>侧栏预览：只取用户第一句话的首行（遇到换行即截断），避免换行把预览撑成两行。</summary>
    public string Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, FirstLine(value));
    }

    /// <summary>取文本首行并去除首尾空白；无换行时返回整段。</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var index = text.IndexOfAny(new[] { '\r', '\n' });
        var line = index >= 0 ? text[..index] : text;
        return line.Trim();
    }
}
