using Luna.Localization;
using Luna.Services.Tools;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Jobs;

/// <summary>
/// 主动任务跟进：每 2 小时检查一次逾期或 24 小时内到期的未完成任务，
/// 汇总后发一条系统通知。每个任务每天（本地日期）最多跟进一次。
/// </summary>
public sealed class TaskFollowUpJob : IBackgroundJob
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);

    /// <summary>通知正文中最多逐条列出的任务数，其余合并为“还有 N 项”。</summary>
    private const int MaxListedTasks = 3;

    private readonly TaskRepository _repo;
    private readonly NotificationService _notifications;
    private readonly ILogger<TaskFollowUpJob> _logger;

    public TaskFollowUpJob(TaskRepository repo, NotificationService notifications,
        ILogger<TaskFollowUpJob> logger)
    {
        _repo = repo;
        _notifications = notifications;
        _logger = logger;
    }

    public string Name => "TaskFollowUpJob";

    /// <summary>启动时检查一次，避免错过当天应跟进的任务；按天去重保证不会重复打扰。</summary>
    public bool RunOnStartup => true;

    public DateTime GetNextRunTime(DateTime lastRunTime) => lastRunTime + CheckInterval;

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nowUtc = DateTime.UtcNow;
        var tasks = await _repo.GetFollowUpCandidatesAsync(nowUtc);
        if (tasks.Count == 0)
            return;

        stoppingToken.ThrowIfCancellationRequested();

        var loc = LocalizationService.Instance;
        var lines = new List<string>();

        foreach (var task in tasks.Take(MaxListedTasks))
        {
            var dueText = task.DueAtUtc is { } due ? ToolTime.ToLocalString(due) : string.Empty;
            lines.Add(task.DueAtUtc < nowUtc
                ? "• " + loc.Format("Common_TaskFollowUp_Overdue", task.Title, dueText)
                : "• " + loc.Format("Common_TaskFollowUp_DueSoon", task.Title, dueText));
        }

        if (tasks.Count > MaxListedTasks)
            lines.Add(loc.Format("Common_TaskFollowUp_More", tasks.Count - MaxListedTasks));

        _notifications.Notify(
            loc["Common_TaskFollowUp_Title"],
            string.Join(Environment.NewLine, lines),
            timeoutMs: 8000);

        // 通知已发出（即使 Toast 失败也会走托盘兜底），统一标记为今天已跟进。
        foreach (var task in tasks)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await _repo.MarkFollowUpAsync(task.Id, nowUtc);
        }

        _logger.LogInformation("已主动跟进 {Count} 个待办", tasks.Count);
    }
}
