using CommunityToolkit.Mvvm.Messaging;
using Luna.Localization;
using Luna.Models;
using Luna.Services.Tools;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Jobs;

/// <summary>
/// 每日早报：每天本地时间 06:30 汇总今日待办与昨天的日记，调用模型生成一段早安问候，
/// 写入一个名为「🌅 今日早报」的新会话，并发送一条系统通知。
/// 若应用在 06:30 时未运行，则在 10:00 前启动时补发当天早报。
/// </summary>
public sealed class DailyBriefingJob : IBackgroundJob
{
    private static readonly TimeSpan TargetTime = new(6, 30, 0);

    /// <summary>补发窗口截止时间：晚于该时刻启动不再补发，避免晚上突然弹出早报。</summary>
    private static readonly TimeSpan CatchUpWindowEnd = new(10, 0, 0);

    /// <summary>早报中最多列出的待办数，避免 Prompt 过长。</summary>
    private const int MaxTasks = 5;

    /// <summary>日记正文注入 Prompt 的最大长度。</summary>
    private const int MaxDiaryLength = 1500;

    private readonly TaskRepository _taskRepo;
    private readonly DiaryService _diaryService;
    private readonly IAiService _aiService;
    private readonly SessionRepository _sessionRepo;
    private readonly MessageRepository _messageRepo;
    private readonly NotificationService _notifications;
    private readonly ILogger<DailyBriefingJob> _logger;

    public DailyBriefingJob(TaskRepository taskRepo, DiaryService diaryService,
        IAiService aiService, SessionRepository sessionRepo, MessageRepository messageRepo,
        NotificationService notifications, ILogger<DailyBriefingJob> logger)
    {
        _taskRepo = taskRepo;
        _diaryService = diaryService;
        _aiService = aiService;
        _sessionRepo = sessionRepo;
        _messageRepo = messageRepo;
        _notifications = notifications;
        _logger = logger;
    }

    public string Name => "DailyBriefingJob";

    /// <summary>
    /// 每天 06:30 触发；启动时也检查一次，用于在 06:30-10:00 补发窗口内补生成当天的早报。
    /// </summary>
    public bool RunOnStartup => true;

    /// <summary>计算下一个本地时间 06:30。</summary>
    public DateTime GetNextRunTime(DateTime lastRunTime)
    {
        var todayTarget = lastRunTime.Date + TargetTime;
        return lastRunTime < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await GenerateAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用退出时的正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成每日早报失败");
        }
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        var nowLocal = DateTime.Now;
        var timeOfDay = nowLocal.TimeOfDay;

        // 定时触发或启动补发都走这里：早于 06:30 或晚于 10:00 直接跳过。
        if (timeOfDay < TargetTime || timeOfDay > CatchUpWindowEnd)
        {
            _logger.LogDebug("当前不在早报生成窗口（06:30-10:00），跳过");
            return;
        }

        var loc = LocalizationService.Instance;
        var title = loc.Format("Common_DailyBriefing_SessionTitle", nowLocal.ToString("yyyy-MM-dd"));

        // 幂等：同一天已经生成过（包括应用重启、时钟回拨）就不再重复早报。
        if (await _sessionRepo.ExistsByTitleAsync(title))
        {
            _logger.LogInformation("今日早报已存在，跳过生成：{Title}", title);
            return;
        }

        // 1. 今日待办：已逾期、今天到期，以及没有截止时间的待办
        var pending = await _taskRepo.ListAsync("pending");
        var todayTasks = pending
            .Where(t => t.DueAtUtc is null || t.DueAtUtc.Value.ToLocalTime().Date <= nowLocal.Date)
            .Take(MaxTasks)
            .ToList();

        var taskContext = todayTasks.Count == 0
            ? "今天没有紧急待办。"
            : string.Join(Environment.NewLine, todayTasks.Select(t =>
                t.DueAtUtc is { } due
                    ? $"- {t.Title}（截止 {ToolTime.ToLocalString(due)}）"
                    : $"- {t.Title}（未设置截止时间）"));

        // 2. 昨天的日记（可选）
        var yesterdayLogicalDate = DateOnly.FromDateTime(nowLocal.AddDays(-1)).ToString("yyyy-MM-dd");
        string? diary = null;
        try
        {
            diary = await _diaryService.GetEntryContentAsync(yesterdayLogicalDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取昨天日记失败，早报将跳过日记内容");
        }

        var diaryContext = string.IsNullOrWhiteSpace(diary) ? "（昨天没有日记记录）" : diary.Trim();
        if (diaryContext.Length > MaxDiaryLength)
            diaryContext = diaryContext[..MaxDiaryLength] + "…";

        ct.ThrowIfCancellationRequested();

        // 3. 调用模型生成早报；不加载工具定义，避免无谓的 token 消耗。
        var prompt = $"""
        现在是 {nowLocal:yyyy年MM月dd日 HH:mm}。
        请以私人助手的身份，为用户生成一份简短、温馨的“今日早报”。

        要求：
        1. 语气轻松、温柔，符合你的人设。
        2. 结合今日待办与昨天的日记（如果有），用 2-4 句话总结。
        3. 如果今天没有紧急待办，就祝用户有轻松顺利的一天。
        4. 不要编造天气、新闻或日程；没有的信息不要提。
        5. 直接输出早报正文，不要使用 Markdown 标题。

        今日待办：
        {taskContext}

        昨天的日记：
        {diaryContext}
        """;

        var reply = await _aiService.ChatAsync(
            new[] { new ChatMessage { Role = "user", Content = prompt } },
            new AiRequestOptions
            {
                Temperature = 0.7,
                MaxTokens = 600,
                IncludeTools = false,
                DeepThinking = false,
            },
            ct);

        reply = reply.Trim();
        if (string.IsNullOrWhiteSpace(reply))
        {
            _logger.LogWarning("每日早报生成结果为空，已跳过");
            return;
        }

        // 4. 创建早报会话并写入 AI 消息
        var session = new Session
        {
            Title = title,
        };
        await _sessionRepo.InsertAsync(session);

        await _messageRepo.InsertAsync(new Message
        {
            SessionId = session.Id,
            TurnId = Guid.NewGuid().ToString("N"),
            Role = "assistant",
            Content = reply,
            ContentType = "text",
            CreatedAtUtc = DateTime.UtcNow,
            LogicalDate = LogicalDate.Now(),
        });

        // 5. 刷新会话列表并提示用户
        WeakReferenceMessenger.Default.Send(new SessionUpdateMessage(session.Id));
        _notifications.Notify(
            loc["Common_DailyBriefing_NotifyTitle"],
            loc["Common_DailyBriefing_NotifyMessage"],
            timeoutMs: 6000);

        _logger.LogInformation("每日早报生成完毕：SessionId={SessionId}", session.Id);
    }
}
