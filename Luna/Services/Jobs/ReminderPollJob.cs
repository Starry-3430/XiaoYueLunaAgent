namespace Luna.Services.Jobs;

/// <summary>将原有 ReminderService 的 20 秒轮询迁移为后台作业。</summary>
public sealed class ReminderPollJob : IBackgroundJob
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly ReminderService _reminderService;

    public ReminderPollJob(ReminderService reminderService)
        => _reminderService = reminderService;

    public string Name => "ReminderPoll";

    /// <summary>启动时立即轮询一次，与迁移前 <c>Start()</c> 的行为保持一致。</summary>
    public bool RunOnStartup => true;

    public DateTime GetNextRunTime(DateTime lastRunTime) => lastRunTime + PollInterval;

    public Task ExecuteAsync(CancellationToken stoppingToken)
        => _reminderService.PollAsync(stoppingToken);
}
