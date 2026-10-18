using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Jobs;

/// <summary>
/// 轻量后台调度器：统一管理 <see cref="IBackgroundJob"/>，负责定时触发、
/// 防重叠与异常隔离。调度基于本地时间，每 5 秒检查一次到期作业。
/// </summary>
/// <remarks>
/// 不注册为 HostedService，而是在数据库初始化与主窗口订阅完成后手动启动，
/// 避免提醒作业在 UI 尚未就绪、数据库尚未建表时触发。
/// </remarks>
public sealed class BackgroundSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IBackgroundJob> _jobs;
    private readonly ILogger<BackgroundSchedulerService> _logger;

    private readonly ConcurrentDictionary<IBackgroundJob, DateTime> _nextRunTimes = new();
    private readonly ConcurrentDictionary<IBackgroundJob, Task> _running = new();

    public BackgroundSchedulerService(IEnumerable<IBackgroundJob> jobs,
        ILogger<BackgroundSchedulerService> logger)
    {
        _jobs = jobs.ToList();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("后台调度器已启动，共 {Count} 个作业", _jobs.Count);

        var now = DateTime.Now;
        foreach (var job in _jobs)
        {
            _nextRunTimes[job] = job.RunOnStartup ? now : job.GetNextRunTime(now);
            _logger.LogInformation("已注册后台作业：{Name}，下次运行 {Next:yyyy-MM-dd HH:mm:ss}",
                job.Name, _nextRunTimes[job]);
        }

        // 启动即处理一次到期的作业（提醒轮询等）
        TryStartDueJobs(DateTime.Now, stoppingToken);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                TryStartDueJobs(DateTime.Now, stoppingToken);
            }
        }
        finally
        {
            // 等待正在运行的作业响应取消，避免退出后仍有后台写入。
            var running = _running.Values.ToArray();
            if (running.Length > 0)
            {
                try
                {
                    await Task.WhenAll(running);
                }
                catch (OperationCanceledException)
                {
                    // 正常退出
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "等待后台作业退出时出错");
                }
            }

            _logger.LogInformation("后台调度器已停止。");
        }
    }

    /// <summary>启动所有已到期且未在运行的作业；先更新下次运行时间，防止长任务造成堆积。</summary>
    private void TryStartDueJobs(DateTime checkTime, CancellationToken stoppingToken)
    {
        foreach (var job in _jobs)
        {
            if (!_nextRunTimes.TryGetValue(job, out var nextRun) || checkTime < nextRun)
                continue;

            // 防重叠：上一次还没结束就跳过，等下一 tick 再判断。
            if (_running.ContainsKey(job))
                continue;

            _nextRunTimes[job] = job.GetNextRunTime(checkTime);
            var next = _nextRunTimes[job];

            // 防御性兜底：作业若返回过去时间，避免每 tick 都触发。
            if (next <= checkTime)
                _nextRunTimes[job] = checkTime + TimeSpan.FromSeconds(1);

            var task = RunJobSafelyAsync(job, stoppingToken);
            _running[job] = task;
            _ = task.ContinueWith(_ => _running.TryRemove(job, out Task? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>异常隔离：单个作业崩溃不能杀死整个调度器。</summary>
    private async Task RunJobSafelyAsync(IBackgroundJob job, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogDebug("开始执行后台作业：{Name}", job.Name);
            await job.ExecuteAsync(stoppingToken);
            _logger.LogDebug("后台作业执行完成：{Name}", job.Name);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 应用退出时的正常取消，不记录为异常。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台作业执行失败：{Name}", job.Name);
        }
    }
}
