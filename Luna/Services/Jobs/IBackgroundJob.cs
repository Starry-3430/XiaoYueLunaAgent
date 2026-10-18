namespace Luna.Services.Jobs;

/// <summary>
/// 后台作业：由 <see cref="BackgroundSchedulerService"/> 统一调度。
/// 实现方只负责“下次何时运行”和“运行什么”，无需关心定时器与防重叠。
/// </summary>
public interface IBackgroundJob
{
    /// <summary>作业名称（用于日志记录）。</summary>
    string Name { get; }

    /// <summary>
    /// 根据上次运行时间计算下次运行时间（本地时间）。
    /// 支持每几分钟、每小时、每天固定时刻等灵活频率。
    /// </summary>
    DateTime GetNextRunTime(DateTime lastRunTime);

    /// <summary>是否在调度器启动时立即执行一次。</summary>
    bool RunOnStartup => false;

    /// <summary>执行作业逻辑；<paramref name="stoppingToken"/> 用于应用退出时取消。</summary>
    Task ExecuteAsync(CancellationToken stoppingToken);
}
