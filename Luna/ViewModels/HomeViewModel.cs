using System.Collections.ObjectModel;
using System.ComponentModel;
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

public partial class HomeViewModel : ObservableObject, IRecipient<SessionUpdateMessage>
{
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AiSettings _aiSettings;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolSettingsService _toolSettings;
    private readonly ToolPermissionService _toolPermission;
    private readonly SettingsService _settingsService;
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

    /// <summary>Tavily 搜索 API Key（明文，编辑后立即加密持久化）。</summary>
    [ObservableProperty]
    private string _tavilyApiKey = string.Empty;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatSessionItem> ChatSessions { get; } = new();
    public ObservableCollection<ToolCategory> ToolCategories { get; } = new();

    public HomeViewModel(IAiService aiService, SessionRepository sessionRepo,
        MessageRepository messageRepo, AiSettings aiSettings, ToolRegistry toolRegistry,
        ToolSettingsService toolSettings, ToolPermissionService toolPermission,
        SettingsService settingsService, ILogger<HomeViewModel> logger)
    {
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _aiSettings = aiSettings;
        _toolRegistry = toolRegistry;
        _toolSettings = toolSettings;
        _toolPermission = toolPermission;
        _settingsService = settingsService;
        _logger = logger;

        _tavilyApiKey = _aiSettings.TavilyApiKey;

        WeakReferenceMessenger.Default.Register<SessionUpdateMessage>(this);
        _ = LoadSessionsAsync();

        PopulateTools();
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
                new ToolDefinition { Id = "todo", Category = "组织", Name = "待办事项", Description = "创建和管理待办事项", IsEnabled = true, IsAvailable = false },
                new ToolDefinition { Id = "reminder", Category = "组织", Name = "定时提醒", Description = "设置定时提醒", IsEnabled = true, IsAvailable = false },
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
                var msg = new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    Reasoning = m.ReasoningContent,
                    DbId = m.Id,
                    ToolCallId = m.ToolCallId,
                };

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

                Messages.Add(msg);
            }

            for (var i = 0; i < Messages.Count; i++)
            {
                var m = Messages[i];
                if (m.Role != "assistant") continue;

                var isTurnEnd = i == Messages.Count - 1 || Messages[i + 1].Role == "user";
                if (isTurnEnd && !string.IsNullOrEmpty(m.Content))
                    m.IsFinalReply = true;
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

    private const int MaxToolRounds = 3;

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
            long userMsgDbId = await _messageRepo.InsertAsync(userMsg);

            Messages.Add(new ChatMessage { Role = "user", Content = userText, DbId = userMsgDbId });

            await RunAiLoopAsync(turnId, logicalDate);
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

    private async Task RunAiLoopAsync(string turnId, string logicalDate)
    {
        IsBusy = true;
        Status = "思考中…";
        _cts = new CancellationTokenSource();

        var replyContent = "";
        var replyReasoning = "";
        ChatMessage? lastAssistantMsg = null;
        ChatMessage? activeReply = null;

        try
        {
            for (var round = 0; round < MaxToolRounds; round++)
                {
                    var uiReply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
                    Messages.Add(uiReply);
                    activeReply = uiReply;

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

                        // 中/高风险工具在执行前需要用户授权
                        if (ToolPermissionService.NeedsConfirmation(tool) &&
                            !_toolPermission.IsAlwaysAllowed(_currentSessionId, tool.Name))
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
                                _toolPermission.AllowForConversation(_currentSessionId, tool.Name);
                        }

                        try
                        {
                            using var argsDoc = JsonDocument.Parse(
                                string.IsNullOrWhiteSpace(entry.ArgumentsJson) ? "{}" : entry.ArgumentsJson);
                            var result = await tool.ExecuteAsync(argsDoc.RootElement.Clone(), _cts.Token);
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
                if (activeReply is not null)
                {
                    activeReply.IsStreaming = false;
                    activeReply.ErrorMessage = "已取消";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 回复失败");
                Status = "出错：" + ex.Message;
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
            }

            if (lastAssistantMsg is not null)
            {
                var assistantMsg = new Message
                {
                    SessionId = _currentSessionId,
                    TurnId = turnId,
                    Role = "assistant",
                    Content = replyContent,
                    ReasoningContent = replyReasoning,
                    CreatedAtUtc = DateTime.UtcNow,
                    LogicalDate = logicalDate,
                };
                await _messageRepo.InsertAsync(assistantMsg);
            }

            await _sessionRepo.TouchAsync(_currentSessionId);
            await LoadSessionsAsync();
            Status = "就绪";
    }

    private async Task<(string content, string reasoning, Dictionary<int, ToolCallEntry> pendingToolCalls)>
        StreamOneRoundAsync(ChatMessage uiReply, CancellationToken ct)
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

internal sealed record ToolCallEntryDb(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    string ResultJson
);